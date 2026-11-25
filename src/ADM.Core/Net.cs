using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ADM.Core;

/// <summary>How a download's connections wait between tries; tests shorten the waits, per downloader.</summary>
internal sealed class RetryPolicy
{
    /// <summary>The wait after a first failure; each further one doubles it, up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long connection failures keep being retried without progress; null keeps on until stopped.</summary>
    public TimeSpan? ReconnectWindow { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>How long a request may go unanswered before it counts as a lost connection.</summary>
    public TimeSpan AnswerTimeout { get; init; } = Net.ReadTimeout;

    /// <summary>The least time a request goes unanswered before a free connection takes over its piece.</summary>
    public TimeSpan UnansweredAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether Windows reports a network; while it doesn't, the reconnect window is paused.</summary>
    public Func<bool> NetworkAvailable { get; init; } = NetworkInterface.GetIsNetworkAvailable;

    public static RetryPolicy For(Settings settings) => new()
    {
        ReconnectWindow = settings.ReconnectMinutes > 0 ? TimeSpan.FromMinutes(settings.ReconnectMinutes) : null,
    };

    public TimeSpan Backoff(int attempt) =>
        TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Clamp(attempt, 0, 20)));

    public Task BackoffAsync(int attempt, CancellationToken ct) => Task.Delay(Backoff(attempt), ct);

    /// <summary>How long a ZIP preview's read may go silent before it counts as a lost connection.</summary>
    public TimeSpan ReadTimeout { get; init; } = Net.ReadTimeout;

    /// <summary>
    /// Waits before reconnecting. While Windows reports no network the wait doesn't count against the
    /// window, and it ends as soon as the network is back.
    /// </summary>
    /// <param name="show">Shows why the download is waiting.</param>
    /// <param name="waiting">Runs the wait, which its caller can end early.</param>
    /// <returns>When to give up now.</returns>
    public async Task<DateTime?> WaitToReconnectAsync(int reconnects, DateTime? giveUpAt, Action<string> show, Func<Func<CancellationToken, Task>, Task> waiting)
    {
        var wait = Backoff(reconnects);
        bool network = NetworkAvailable();
        show(network ? StatusWords.Current.Reconnecting(reconnects + 1) : StatusWords.Current.WaitingForNetwork);
        var started = DateTime.UtcNow;
        await waiting(token => network ? Task.Delay(wait, token) : WaitForNetworkAsync(wait, token));
        return network ? giveUpAt : giveUpAt + (DateTime.UtcNow - started);
    }

    /// <summary>
    /// A first request's failure that waiting won't mend: with the network up, a name that doesn't exist is
    /// a mistyped address, not an outage. Partway through a download the same answer does mean an outage.
    /// </summary>
    public bool NoSuchName(Exception e)
    {
        for (Exception? at = e; at != null; at = at.InnerException)
            if (at is SocketException { SocketErrorCode: SocketError.HostNotFound }) return NetworkAvailable();
        return false;
    }

    /// <summary>Runs a wait that <paramref name="wake"/> ends early. Only <paramref name="ct"/> cancels it.</summary>
    public static async Task WaitOrWakeAsync(Func<CancellationToken, Task> wait, CancellationToken wake, CancellationToken ct)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(wake, ct);
        try
        {
            await wait(either.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks a server something. With no answer in time, it ends as a lost connection does.</summary>
    public async Task<T> AnswerAsync<T>(Func<CancellationToken, Task<T>> ask, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AnswerTimeout);
        try
        {
            return await ask(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HttpIOException(HttpRequestError.Unknown, "The server didn't answer.");
        }
    }

    /// <summary>Runs <paramref name="action"/>, and up to three times more after an error <paramref name="source"/> says is worth another try.</summary>
    public async Task<T> WithRetryAsync<T>(ISegmentSource source, Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception e) when (source.IsTransient(e, ct) && attempt < 3)
            {
                await BackoffAsync(attempt, ct);
            }
        }
    }

    /// <summary>Waits up to <paramref name="longest"/>, or until Windows reports a network again.</summary>
    public static async Task WaitForNetworkAsync(TimeSpan longest, CancellationToken ct)
    {
        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkAvailabilityChangedEventHandler handler = (_, e) => { if (e.IsAvailable) back.TrySetResult(); };
        NetworkChange.NetworkAvailabilityChanged += handler;
        try
        {
            await Task.WhenAny(back.Task, Task.Delay(longest, ct));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= handler;
        }
    }
}

internal static class Net
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    // One client per proxy configuration: after a settings change new downloads get a new
    // client, while running ones finish on the client they started with.
    static readonly ConcurrentDictionary<ProxySettings, HttpClient> Clients = new();

    public static HttpClient ClientFor(Settings settings) => ClientFor(settings.Proxy);

    public static HttpClient ClientFor(ProxySettings proxy) => Clients.GetOrAdd(proxy, NewClient);

    // One WinHTTP session per script; Windows keeps the script between questions, so asking again costs little.
    static readonly ConcurrentDictionary<ProxySettings, PacProxy> Scripts = new();

    /// <summary>The script behind <paramref name="proxy"/>, whose mode must be Script or AutoDetect.</summary>
    public static PacProxy ScriptFor(ProxySettings proxy) => Scripts.GetOrAdd(proxy, p => new PacProxy(p));

    static readonly TimeSpan LearnedFor = TimeSpan.FromMinutes(10);

    // Servers that refused extra connections, by host and port: how many connections worked, and until when
    // that is believed. Kept in memory only.
    static readonly ConcurrentDictionary<string, (int Connections, DateTime Until)> LearnedLimits = new();

    public static void LearnLimit(Uri server, int connections) =>
        LearnedLimits[server.Authority] = (Math.Max(1, connections), DateTime.UtcNow + LearnedFor);

    /// <summary>How many connections <paramref name="server"/> took when it last refused more; null if it hasn't lately.</summary>
    public static int? LearnedLimit(Uri server) =>
        LearnedLimits.TryGetValue(server.Authority, out var learned) && learned.Until > DateTime.UtcNow ? learned.Connections : null;

    static HttpClient NewClient(ProxySettings proxy)
    {
        var script = proxy.Mode is ProxyMode.Script or ProxyMode.AutoDetect ? ScriptFor(proxy) : null;
        var sockets = NewHandler(proxy, script);
        return new HttpClient(script != null ? new PacHandler(script, sockets) : sockets) { Timeout = Timeout.InfiniteTimeSpan };
    }

    static SocketsHttpHandler NewHandler(ProxySettings proxy, PacProxy? script) => new()
    {
        // SendAsync follows redirects itself, so the browser's cookies never go to another host.
        AllowAutoRedirect = false,
        // Byte ranges address the encoded entity, so content must arrive untransformed.
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        MaxConnectionsPerServer = 64,
        // A response cut short by a split holds bytes another connection now fetches: drop the connection
        // rather than read them to reuse it, which would waste bandwidth and keep the server's slot busy.
        MaxResponseDrainSize = 0,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseProxy = proxy.Mode != ProxyMode.None,
        // Without a proxy object the handler follows the Windows settings, PAC scripts included.
        Proxy = (IWebProxy?)script ?? (proxy.Mode == ProxyMode.Manual ? new ProxyRouter(proxy) : null),
        // Logins go out only in answer to a challenge, never unasked.
        Credentials = LoginResolver.Instance,
        PreAuthenticate = false,
    };

    internal const int MaxRedirects = 20;

    /// <summary>
    /// Sends with <paramref name="client"/>, answering login challenges for this download. Redirects are
    /// followed here rather than by the handler, so the browser's cookies go only to the download's own
    /// site; as in .NET, there are at most 20, and https never redirects to http.
    /// </summary>
    /// <param name="refuseRedirect">Says why a redirect's target can't be followed, which ends the request with that; null when it can.</param>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Settings settings, DownloadItem item, HttpRequestMessage request, CancellationToken ct, Func<Uri, string?>? refuseRedirect = null)
    {
        // Set inside this async method, the context flows into the handler and is gone when it returns.
        LoginResolver.Current.Value = new LoginResolver.Context(
            Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : "",
            string.IsNullOrEmpty(item.UserName) ? null : new NetworkCredential(item.UserName, Secrets.Unprotect(item.ProtectedPassword) ?? ""),
            settings.SiteLogins);
        for (int redirects = 0; ; redirects++)
        {
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (redirects == MaxRedirects || RedirectTarget(request.RequestUri!, resp) is not { } target) return resp;
            resp.Dispose();
            if (refuseRedirect?.Invoke(target) is { } refusal) throw new InvalidDataException(refusal);
            request = Redirected(request, target, item);
        }
    }

    /// <summary>Where a redirect leads, if it is one that may be followed: http or https, and never from https to http.</summary>
    internal static Uri? RedirectTarget(Uri from, HttpResponseMessage resp)
    {
        if (resp.StatusCode is not (HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || resp.Headers.Location is not { } location)
            return null;
        var target = location.IsAbsoluteUri ? location : new Uri(from, location);
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return null;
        return from.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps ? null : target;
    }

    // The only headers a redirect carries over. Credentials never do: the handler may have added a login's
    // Authorization to the request, and a login on the next host is answered afresh through LoginResolver.
    static readonly string[] CarriedHeaders = { "User-Agent", "Accept", "Accept-Encoding", "Referer", "Range" };

    /// <summary>The next hop of a redirect: the request's own headers, with the browser's cookies only on the download's own site.</summary>
    static HttpRequestMessage Redirected(HttpRequestMessage previous, Uri target, DownloadItem item)
    {
        var next = new HttpRequestMessage(HttpMethod.Get, target);
        foreach (var name in CarriedHeaders)
            if (previous.Headers.TryGetValues(name, out var values)) next.Headers.TryAddWithoutValidation(name, values);
        if (!string.IsNullOrEmpty(item.Cookies) && SameOrigin(target, item.Url)) next.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
        return next;
    }

    /// <summary>Same scheme, host and port as <paramref name="url"/>.</summary>
    public static bool SameOrigin(Uri target, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var origin)
        && target.Scheme == origin.Scheme
        && target.Port == origin.Port
        && string.Equals(target.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <param name="cookies">False leaves out the browser's cookies, as for an address on another site.</param>
    public static HttpRequestMessage NewRequest(DownloadItem item, string url, string defaultUserAgent, bool cookies = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(item.UserAgent) ? defaultUserAgent : item.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrEmpty(item.Referrer)) req.Headers.TryAddWithoutValidation("Referer", item.Referrer);
        if (cookies && !string.IsNullOrEmpty(item.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
        return req;
    }

    public static void EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("The server refused the login (401).", null, resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
    }

    /// <summary>ERROR_DISK_FULL as an HRESULT, as Windows reports a full disk.</summary>
    public const int DiskFullHResult = unchecked((int)0x80070070);
    /// <summary>ERROR_HANDLE_DISK_FULL as an HRESULT.</summary>
    const int HandleDiskFullHResult = unchecked((int)0x80070027);

    /// <summary>A disk too full to write, as Windows reports it or as a check for free space does.</summary>
    public static bool IsDiskFull(Exception e) => e is IOException { HResult: DiskFullHResult or HandleDiskFullHResult };

    /// <summary>
    /// Network hiccups, read timeouts and 5xx/408/429 are worth retrying; other failures are final,
    /// a full disk among them.
    /// </summary>
    public static bool IsTransient(Exception e, CancellationToken ct) => e switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException h => h.StatusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError,
        IOException { HResult: DiskFullHResult or HandleDiskFullHResult } => false,
        IOException => true,
        _ => false,
    };

    /// <summary>
    /// A try that got no HTTP answer at all, or lost its connection partway through one: refused or reset
    /// connections, DNS failures, read timeouts. HTTP errors are not among them.
    /// </summary>
    public static bool IsConnectionFailure(Exception e, CancellationToken ct) => e switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException h => h.StatusCode is null,
        HttpIOException => true,
        IOException { InnerException: SocketException } => true,
        _ => false,
    };

}
