using System.Net;
using System.Runtime.InteropServices;

namespace ADM.Core;

/// <summary>
/// The proxy a script names for each address, as Windows works it out through WinHTTP: from the script's
/// address, or from one found on the network (DHCP, then DNS). Its answer comes through
/// <see cref="ResolveAsync"/> rather than <see cref="GetProxy"/>, because SocketsHttpHandler treats an
/// exception from an <see cref="IWebProxy"/> as "no proxy" and goes direct; see <see cref="PacHandler"/>.
/// </summary>
/// <remarks>
/// This asks with WinHttpGetProxyForUrlEx. The older WinHttpGetProxyForUrl answers a script that says
/// "SOCKS a:1" with "no proxy", the same as DIRECT, which would show the user's address to the server.
/// Windows answers for a loopback address itself, without running the script: it goes direct.
/// A script at an address is fetched through <see cref="ScriptRelay"/>, as Windows now leaves out SOCKS4 and
/// SOCKS5 entries. One found by detection isn't, as only WinHTTP knows where it is.
/// </remarks>
internal sealed class PacProxy : IWebProxy
{
    public const string LoadFailed = "The proxy script could not be loaded.";

    /// <summary>ERROR_WINHTTP_AUTODETECTION_FAILED: this network offers no script.</summary>
    internal const int AutoDetectionFailed = 12180;
    /// <summary>ERROR_WINHTTP_UNABLE_TO_DOWNLOAD_SCRIPT.</summary>
    internal const int UnableToDownloadScript = 12167;

    /// <summary>ERROR_WINHTTP_BAD_AUTO_PROXY_SCRIPT: the script arrived and isn't one.</summary>
    internal const int BadScript = 12166;
    /// <summary>ERROR_WINHTTP_SCRIPT_EXECUTION_ERROR: the script arrived and can't be run.</summary>
    internal const int ScriptFailed = 12177;

    const int IoPending = 997;
    /// <summary>The longest one answer may take. Finding a script on the network can take several seconds.</summary>
    static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);

    readonly SessionHandle _session;
    readonly AutoProxyOptions _options;
    readonly AsyncLocal<Uri?> _answer = new();

    public PacProxy(ProxySettings settings)
    {
        bool detecting = settings.Mode == ProxyMode.AutoDetect;
        _options = new AutoProxyOptions
        {
            Flags = detecting ? AutoProxyAutoDetect : AutoProxyConfigUrl,
            AutoDetectFlags = detecting ? AutoDetectDhcp | AutoDetectDnsA : 0,
            // Through the relay, which makes the script's answers ones Windows reads in full.
            ConfigUrl = detecting ? null : ScriptRelay.AddressFor(settings.ScriptUrl),
            // A script's server never gets the user's Windows login unasked.
            AutoLogonIfChallenged = false,
        };
        _session = WinHttpOpen("ADM", AccessTypeNoProxy, null, null, FlagAsync);
    }

    bool Detecting => _options.Flags == AutoProxyAutoDetect;

    public ICredentials? Credentials { get; set; }

    /// <summary>What <see cref="PacHandler"/> was told for the request being sent; null is direct.</summary>
    public Uri? GetProxy(Uri destination) => _answer.Value;

    public bool IsBypassed(Uri host) => false;

    /// <summary>Holds the answer for the handler below, in this asynchronous flow only.</summary>
    internal void UseForThisRequest(Uri? answer) => _answer.Value = answer;

    /// <summary>
    /// The proxy for <paramref name="destination"/>, or null to go direct. A script that can't be loaded or
    /// run throws an error that is retried like a connection failure, and never goes direct. Windows
    /// remembers such a failure for some seconds (about 8 when measured), and a script that has loaded
    /// until it is replaced, so a retry recovers soon after the script's server does.
    /// </summary>
    public async Task<Uri?> ResolveAsync(Uri destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var asking = new Asking();
        var context = GCHandle.Alloc(asking);
        int error = _session.IsInvalid ? UnableToDownloadScript : WinHttpCreateProxyResolver(_session, out asking.Resolver);
        if (error == 0)
        {
            WinHttpSetStatusCallback(asking.Resolver, CallbackPointer, CallbackFlagComplete | CallbackFlagError, IntPtr.Zero);
            var options = _options;
            error = WinHttpGetProxyForUrlEx(asking.Resolver, destination.AbsoluteUri, ref options, GCHandle.ToIntPtr(context));
        }
        if (error != IoPending)
        {
            asking.Dispose();
            context.Free();
            return AfterError(error, Detecting);
        }

        // A stopped download moves on at once; WinHTTP's own call ends when it ends, and only then is its state freed.
        _ = asking.Done.Task.ContinueWith(_ =>
        {
            asking.Dispose();
            context.Free();
        }, TaskScheduler.Default);
        Answer answer;
        try
        {
            answer = await asking.Done.Task.WaitAsync(AnswerTimeout, ct);
        }
        catch (TimeoutException)
        {
            return AfterError(UnableToDownloadScript, Detecting);
        }
        return answer.Error != 0 ? AfterError(answer.Error, Detecting) : answer.Proxy;
    }

    /// <summary>
    /// Finding no script on a network is how automatic detection says the network has no proxy, so that goes
    /// direct, as in a browser. Every other failure means a script was asked for and could not be used. A
    /// script that arrived and can't run stays that way; one that didn't arrive may be back in a minute.
    /// </summary>
    internal static Uri? AfterError(int error, bool detecting) =>
        detecting && error == AutoDetectionFailed ? null
        : error is BadScript or ScriptFailed ? throw new ProxyException(LoadFailed)
        : throw new HttpRequestException(LoadFailed);

    /// <summary>The script's first entry; a reply with no entries ADM can use is a failure, not "direct".</summary>
    static Answer Read(in ProxyResult result)
    {
        if (result.Count == 0) return new Answer(null, BadScript);
        var first = Marshal.PtrToStructure<ProxyResultEntry>(result.Entries);
        if (!first.Proxy) return new Answer(null, 0);
        var host = Marshal.PtrToStringUni(first.Host) ?? "";
        string? scheme = first.Scheme switch { SchemeHttp => "http", SchemeHttps => "https", SchemeSocks => "socks5", _ => null };
        if (first.Scheme == SchemeSocks)
        {
            // A bare "SOCKS" names no version, and browsers disagree on it; SOCKS5 sends the name to the proxy to look
            // up, and against a SOCKS4 server only fails. The relay marks a SOCKS4 proxy's host.
            if (host.EndsWith(ScriptRelay.Socks4Mark, StringComparison.OrdinalIgnoreCase))
            {
                scheme = "socks4";
                host = host[..^ScriptRelay.Socks4Mark.Length];
            }
            // A script found by detection doesn't go through the relay, and older Windows read "SOCKS4 a:1" as SOCKS
            // with the host "4 a".
            else if (host.StartsWith("4 ", StringComparison.Ordinal))
            {
                scheme = "socks4";
                host = host[2..];
            }
            else if (host.StartsWith("5 ", StringComparison.Ordinal))
            {
                host = host[2..];
            }
        }
        return scheme != null && Uri.TryCreate($"{scheme}://{host}:{first.Port}/", UriKind.Absolute, out var proxy) && proxy.Host.Length > 0
            ? new Answer(proxy, 0)
            : new Answer(null, BadScript);
    }

    static void OnStatus(IntPtr resolver, IntPtr context, uint status, IntPtr info, uint length)
    {
        if (context == IntPtr.Zero || GCHandle.FromIntPtr(context).Target is not Asking asking) return;
        if (status == StatusComplete)
        {
            var result = new ProxyResult();
            int error = WinHttpGetProxyResult(resolver, ref result);
            if (error != 0)
            {
                asking.Done.TrySetResult(new Answer(null, error));
                return;
            }
            var answer = Read(result);
            WinHttpFreeProxyResult(ref result);
            asking.Done.TrySetResult(answer);
        }
        else if (status == StatusError)
        {
            asking.Done.TrySetResult(new Answer(null, (int)Marshal.PtrToStructure<AsyncResult>(info).Error));
        }
    }

    // Never collected: WinHTTP calls it from its own threads.
    static readonly StatusCallback Callback = OnStatus;
    static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);

    readonly record struct Answer(Uri? Proxy, int Error);

    /// <summary>One question: its own resolver, which WinHTTP doesn't share between questions.</summary>
    sealed class Asking : IDisposable
    {
        public IntPtr Resolver;
        public readonly TaskCompletionSource<Answer> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Resolver != IntPtr.Zero) WinHttpCloseHandle(Resolver);
        }
    }

    sealed class SessionHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => WinHttpCloseHandle(handle);
    }

    delegate void StatusCallback(IntPtr handle, IntPtr context, uint status, IntPtr info, uint length);

    const uint AccessTypeNoProxy = 1;
    const uint FlagAsync = 0x10000000;
    const uint AutoProxyAutoDetect = 1;
    const uint AutoProxyConfigUrl = 2;
    const uint AutoDetectDhcp = 1;
    const uint AutoDetectDnsA = 2;
    const uint StatusComplete = 0x01000000;
    const uint StatusError = 0x00200000;
    const uint CallbackFlagComplete = StatusComplete;
    const uint CallbackFlagError = StatusError;
    const int SchemeHttp = 1;
    const int SchemeHttps = 2;
    const int SchemeSocks = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AutoProxyOptions
    {
        public uint Flags;
        public uint AutoDetectFlags;
        public string? ConfigUrl;
        public IntPtr Reserved;
        public uint Reserved2;
        [MarshalAs(UnmanagedType.Bool)] public bool AutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProxyResult
    {
        public uint Count;
        public IntPtr Entries;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProxyResultEntry
    {
        [MarshalAs(UnmanagedType.Bool)] public bool Proxy;
        [MarshalAs(UnmanagedType.Bool)] public bool Bypass;
        public int Scheme;
        public IntPtr Host;
        public ushort Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AsyncResult
    {
        public IntPtr Result;
        public uint Error;
    }

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SessionHandle WinHttpOpen(string agent, uint accessType, string? proxy, string? bypass, uint flags);

    [DllImport("winhttp.dll")]
    static extern int WinHttpCreateProxyResolver(SessionHandle session, out IntPtr resolver);

    [DllImport("winhttp.dll")]
    static extern IntPtr WinHttpSetStatusCallback(IntPtr handle, IntPtr callback, uint flags, IntPtr reserved);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode)]
    static extern int WinHttpGetProxyForUrlEx(IntPtr resolver, string url, ref AutoProxyOptions options, IntPtr context);

    [DllImport("winhttp.dll")]
    static extern int WinHttpGetProxyResult(IntPtr resolver, ref ProxyResult result);

    [DllImport("winhttp.dll")]
    static extern void WinHttpFreeProxyResult(ref ProxyResult result);

    [DllImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WinHttpCloseHandle(IntPtr handle);
}

/// <summary>Asks the script before each request, so one that fails fails the request instead of going direct.</summary>
internal sealed class PacHandler(PacProxy script, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        script.UseForThisRequest(await script.ResolveAsync(request.RequestUri!, ct));
        return await base.SendAsync(request, ct);
    }
}
