using System.IO;
using System.Net.Http;
using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// What a site's video needs, where each was found, and a button that fetches what's missing, each checked against
/// the SHA-256 its publisher posts. With nothing missing, it fetches ADM's own copies again; copies elsewhere are the user's.
/// </summary>
public partial class VideoToolsWindow : ThemedWindow
{
    readonly Settings _settings;
    readonly VideoTools _tools = VideoTools.Current;
    CancellationTokenSource? _fetching;

    public VideoToolsWindow(Settings settings)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _settings = settings;
        Closing += (_, _) => _fetching?.Cancel();
        ShowTools();
    }

    void ShowTools()
    {
        var sources = ToolSources.Official;
        // About how big each was on 2026-11-19.
        YtDlpText.Text = Status(_tools.YtDlpPath, sources.YtDlp, 24);
        RuntimeText.Text = Status(_tools.Runtime?.Path, sources.Deno, 43);
        FfmpegText.Text = Status(_tools.FfmpegPath, sources.Ffmpeg, 180);
        var missing = _tools.Missing.Count > 0;
        FetchButton.Content = missing ? Lang.T("videotools.download") : Lang.T("videotools.update");
        FetchButton.IsEnabled = missing || Own().Count > 0;
    }

    /// <summary>Where it was found; or, missing, where it comes from and about how big it is.</summary>
    static string Status(string? path, Uri source, int megabytes) => path != null
        ? Lang.T("videotools.found", path)
        : Lang.T("videotools.missing", $"{source.Host}/{source.Segments[1].TrimEnd('/')}", Format.Bytes(megabytes * 1024L * 1024));

    /// <summary>The tools in ADM's own folder, which are the ones it fetches again.</summary>
    List<VideoTool> Own()
    {
        bool Mine(string? path) => path != null && Path.GetDirectoryName(path) is { } folder
            && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_tools.Folder), StringComparison.OrdinalIgnoreCase);
        var own = new List<VideoTool>();
        if (Mine(_tools.YtDlpPath)) own.Add(VideoTool.YtDlp);
        if (Mine(_tools.Runtime?.Path)) own.Add(VideoTool.JsRuntime);
        if (Mine(_tools.FfmpegPath)) own.Add(VideoTool.Ffmpeg);
        return own;
    }

    async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var tools = _tools.Missing.Count > 0 ? _tools.Missing.ToList() : Own();
        FetchButton.IsEnabled = false;
        ResultText.Visibility = Visibility.Collapsed;
        using var fetching = _fetching = new CancellationTokenSource();
        try
        {
            await _tools.InstallAsync(_settings, tools, new Progress<ToolProgress>(ShowProgress), fetching.Token);
            ShowResult(Lang.T("videotools.ready"));
        }
        catch (OperationCanceledException) when (fetching.IsCancellationRequested)
        {
            // Closed while fetching: what was half fetched is already gone.
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Error("The video tools could not be fetched.", ex);
            ShowResult(ex.Message);
        }
        finally
        {
            _fetching = null;
        }
        foreach (var bar in new[] { YtDlpBar, RuntimeBar, FfmpegBar }) bar.Visibility = Visibility.Collapsed;
        ShowTools();
    }

    void ShowProgress(ToolProgress progress)
    {
        var bar = progress.Tool switch
        {
            VideoTool.YtDlp => YtDlpBar,
            VideoTool.JsRuntime => RuntimeBar,
            _ => FfmpegBar,
        };
        bar.Visibility = Visibility.Visible;
        bar.IsIndeterminate = progress.Total is not > 0;
        if (progress.Total is long total and > 0) bar.Value = 100.0 * progress.Done / total;
    }

    void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.Visibility = Visibility.Visible;
    }
}
