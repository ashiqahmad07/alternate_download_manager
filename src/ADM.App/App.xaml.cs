using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

public partial class App : Application
{
    readonly CancellationTokenSource _ipc = new();
    Mutex? _singleInstance;
    MainWindow? _main;
    DispatcherTimer? _reconnect;
    DispatcherTimer? _toolUpdates;
    int _closing;

    public DownloadManager Manager { get; private set; } = null!;
    /// <summary>The site grabber's projects; made with the manager, before the scheduler's first tick, so their schedules reach them.</summary>
    public GrabberProjects Projects { get; private set; } = null!;

    /// <summary>ADM is closing: windows close without asking anything.</summary>
    public static bool Exiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = CommandLine.Parse(e.Args);

        _singleInstance = new Mutex(true, @"Local\ADM.SingleInstance", out bool first);
        if (!first)
        {
            // IDM's /q (exit when done) applies only to the first copy, so it is not forwarded.
            var messages = new List<string>();
            if (args.Url != null)
                messages.Add(JsonSerializer.Serialize(new
                {
                    type = "download",
                    url = args.Url,
                    directory = args.Directory,
                    fileName = args.FileName,
                    mode = ModeFor(args),
                    silent = args.Silent,
                }));
            if (args.StartQueue) messages.Add("{\"type\":\"startQueue\"}");
            if (messages.Count == 0) messages.Add("{\"type\":\"show\"}");
            foreach (var message in messages)
                try { await IngestPipe.SendAsync(message, 3000, CancellationToken.None); } catch (Exception) { }
            Shutdown();
            return;
        }

        // Only the copy that stays: a second one, which only hands over its arguments, leaves no trace.
        Log.Start(Path.Combine(Store.LogFolder, "adm.log"));
        DispatcherUnhandledException += (_, crash) =>
        {
            crash.Handled = true;
            // Only the first: a failing timer can throw again while the message box is up, and that mustn't close it.
            if (CloseAfter(crash.Exception)) Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, crash) => CloseAfter(crash.ExceptionObject as Exception);
        // Nothing has stopped: an exception no one looked at, in work that has already ended.
        TaskScheduler.UnobservedTaskException += (_, unseen) =>
        {
            Log.Error("A background task failed.", unseen.Exception);
            unseen.SetObserved();
        };

        try
        {
            OldName.TakeOverState();
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // Not with an empty list: saving it would count as the move being done, and leave the user's list behind.
            Log.Error("The state under the old name could not be moved.", problem);
            MessageBox.Show(Lang.T("app.could_not_take_over", problem.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Stop();
            Shutdown(1);
            return;
        }
        WindowsStartup.TakeOverOldEntry();

        Manager = new DownloadManager(Store.DefaultPath, SynchronizationContext.Current!);
        WindowsStartup.TurnOnTheFirstTime(Manager);
        Projects = new GrabberProjects(Manager);
        Lang.Start(Manager.State.Settings.Language);
        Theme.Start(Manager.State.Settings);
        _main = new MainWindow(Manager, Projects);
        MainWindow = _main;
        if (!args.Background) _main.Show();

        // After sleep, or when the network changes, connections can be dead without knowing it.
        _reconnect = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnect.Tick += (_, _) =>
        {
            _reconnect.Stop();
            Manager.Reconnect();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // A minute after starting, then every six hours: yt-dlp is kept current, as sites change. It asks at most once a day.
        _toolUpdates = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _toolUpdates.Tick += async (_, _) =>
        {
            _toolUpdates.Interval = TimeSpan.FromHours(6);
            try
            {
                if (await VideoTools.Current.UpdateYtDlpAsync(Manager.State.Settings, CancellationToken.None))
                    Log.Info("yt-dlp was updated");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
            {
                Log.Error("yt-dlp could not be checked for a newer version.", ex);
            }
        };
        _toolUpdates.Start();

        _ = IngestPipe.RunServerAsync(HandleIngestAsync, _ipc.Token);
        if (args.Url != null && ToRequest(args.Url, args.Directory, args.FileName) is { } request)
        {
            var item = _main.HandleExternalDownload(request, ModeFor(args), args.Silent);
            if (args.ExitWhenDone && item != null)
                Manager.DownloadCompleted += done => { if (done == item) _ = ExitAsync(); };
        }
        Manager.StartQueuesOnLaunch();
        if (args.StartQueue) Manager.StartQueue(Manager.MainQueue);
        // Once the window is up, so the question doesn't hold up anything above.
        _ = Dispatcher.InvokeAsync(_main.OfferToDeleteOrphanedParts, DispatcherPriority.ApplicationIdle);
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(ReconnectSoon);
    }

    void OnNetworkAddressChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ReconnectSoon);

    /// <summary>Address changes come in bursts; one reconnect, a second after the last, is enough.</summary>
    void ReconnectSoon()
    {
        _reconnect?.Stop();
        _reconnect?.Start();
    }

    /// <summary>
    /// /a adds to the queue without starting; /n starts silently; otherwise the user is asked. /n with /a queues
    /// silently: the mode says where it goes, and the silent flag that nothing is asked.
    /// </summary>
    static string ModeFor(CommandLine args) => args.AddToQueueOnly ? "queue" : args.Silent ? "now" : "prompt";

    /// <summary>Runs on a pipe thread; validates, then hands the work to the UI thread.</summary>
    Task<string> HandleIngestAsync(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Task.FromResult(Reply(false, "invalid JSON")); }

        switch ((string?)node?["type"])
        {
            case "ping":
                return Task.FromResult(Reply(true));
            case "show":
                Dispatcher.InvokeAsync(() => _main?.ShowAndActivate());
                return Task.FromResult(Reply(true));
            case "config":
                return Dispatcher.InvokeAsync(() => CaptureRules.ConfigReply(Manager.State.Settings)).Task;
            case "startQueue":
                Dispatcher.InvokeAsync(() => Manager.StartQueue(Manager.MainQueue));
                return Task.FromResult(Reply(true));
            case "setupVideoTools":
                Dispatcher.InvokeAsync(() => _main?.ShowVideoTools());
                return Task.FromResult(Reply(true));
            case "download":
                var request = ToRequest(node!);
                if (request == null) return Task.FromResult(Reply(false, "only http and https URLs are supported"));
                var mode = (string?)node!["mode"];
                var silent = node!["silent"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
                Dispatcher.InvokeAsync(() => _main?.HandleExternalDownload(request, mode, silent));
                return Task.FromResult(Reply(true));
            case "links":
                var page = PageLinks.FromMessage(node!);
                Dispatcher.InvokeAsync(() => _main?.ShowLinksDialog(page));
                return Task.FromResult(Reply(true));
            default:
                return Task.FromResult(Reply(false, "unknown message type"));
        }
    }

    static DownloadRequest? ToRequest(JsonNode node)
    {
        var request = ToRequest((string?)node["url"], (string?)node["directory"], (string?)node["fileName"]);
        if (request == null) return null;
        request.Referrer = NullIfEmpty((string?)node["referrer"]);
        request.Cookies = NullIfEmpty((string?)node["cookies"]);
        request.UserAgent = NullIfEmpty((string?)node["userAgent"]);
        request.BrowserProxy = NullIfEmpty((string?)node["browserProxy"]);
        request.Kind = (string?)node["kind"] switch
        {
            "hls" => DownloadKind.Hls,
            "dash" => DownloadKind.Dash,
            "http" => DownloadKind.Http,
            "site" => DownloadKind.Site,
            "joined" => DownloadKind.Joined,
            // A second copy of ADM passes its command line on without one, and the address decides.
            _ => request.Kind,
        };
        request.Captured = node["captured"]?.GetValueKind() == JsonValueKind.True;
        if (node["hlsBandwidth"] is JsonValue bandwidth && bandwidth.TryGetValue<long>(out var bps) && bps > 0) request.HlsBandwidth = bps;
        request.HlsAudioOnly = node["audioOnly"]?.GetValueKind() == JsonValueKind.True;
        if (request.Kind == DownloadKind.Site)
        {
            var choice = (string?)node["siteChoice"] ?? "";
            request.SiteChoice = SiteChoiceKey().IsMatch(choice) ? choice : "original";
            // yt-dlp never gets the browser's cookies, and the site's files are elsewhere, so they aren't kept.
            request.Cookies = null;
        }
        if (request.Kind == DownloadKind.Joined)
        {
            if (!Uri.TryCreate((string?)node["audioUrl"], UriKind.Absolute, out var audio) || audio.Scheme is not ("http" or "https")) return null;
            request.AudioUrl = audio.ToString();
        }
        return request;
    }

    [GeneratedRegex(@"^(mp4:\d{2,4}|original|audio|file:[A-Za-z0-9_.-]{1,64})$")]
    private static partial Regex SiteChoiceKey();

    /// <summary>Accepts only http, https and ftp URLs; a save folder must be a full path or it is ignored.</summary>
    static DownloadRequest? ToRequest(string? url, string? directory, string? fileName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Links.IsDownloadable(uri))
            return null;
        directory = NullIfEmpty(directory);
        return new DownloadRequest
        {
            Url = uri.ToString(),
            Directory = directory != null && Path.IsPathFullyQualified(directory) ? directory : null,
            FileName = NullIfEmpty(fileName),
            Kind = Links.StreamKind(url!),
        };
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static string Reply(bool ok, string? error = null) =>
        new JsonObject { ["ok"] = ok, ["error"] = error }.ToJsonString();

    /// <summary>What can still be done once something has gone wrong that ADM can't carry on from: the log, the state, and telling the user.</summary>
    /// <returns>False if this has already been done, because something else went wrong first.</returns>
    bool CloseAfter(Exception? exception)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return false;
        Log.Error("ADM has to close.", exception);
        try
        {
            Manager?.Save();
        }
        catch (Exception e)
        {
            Log.Error("The state could not be saved while closing.", e);
        }
        MessageBox.Show(Lang.T("app.has_to_close", Store.LogFolder), "ADM", MessageBoxButton.OK, MessageBoxImage.Error);
        return true;
    }

    /// <param name="restart">Starts ADM again once this copy has saved everything.</param>
    public async Task ExitAsync(bool restart = false)
    {
        Exiting = true;
        _ipc.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Theme.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _reconnect?.Stop();
        _toolUpdates?.Stop();
        _main?.DisposeTray();
        await Manager.ShutdownAsync();
        Log.Stop();
        if (restart)
        {
            // While this copy holds the mutex, the new one would hand over to it and leave.
            _singleInstance?.ReleaseMutex();
            Process.Start(Environment.ProcessPath!);
        }
        Shutdown();
    }

    // Windows is logging off or shutting down: persist segment progress now. Items still
    // marked Downloading load as Paused next time.
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        Manager.Save();
        Log.ExitingCleanly();
    }
}
