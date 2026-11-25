using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ADM.Core.Tests;

/// <summary>
/// Local Kestrel server serving one deterministic payload at /file.bin with byte-range support.
/// Each request is held briefly so concurrent connections overlap and can be counted.
/// </summary>
sealed class TestServer : IAsyncDisposable
{
    readonly WebApplication _app;
    int _active;
    int _maxActive;
    int _proxied;
    int _secure;
    readonly ConcurrentQueue<string> _mirrorRanges = new();
    readonly ConcurrentDictionary<string, int> _hits = new();
    readonly ConcurrentQueue<(string Path, string Range)> _ranges = new();
    readonly ConcurrentDictionary<string, byte> _connectionIds = new();
    readonly ConcurrentDictionary<int, byte[]> _versions = new();
    int _limitedActive;
    int _refused;
    int _stalled;
    int _perRequestKBps = 512;
    int _version;
    bool _validators = true;
    bool _down;

    TestServer(WebApplication app, byte[] payload)
    {
        _app = app;
        Payload = payload;
    }

    public byte[] Payload { get; }
    public string BaseUrl { get; private set; } = "";
    public string FileUrl => BaseUrl + "/file.bin";

    /// <summary>The payload behind HTTP Basic auth as user:pass.</summary>
    public string SecureUrl => BaseUrl + "/secure.bin";
    public int SecureRequests => Volatile.Read(ref _secure);

    /// <summary>The Range header of every request to /mirror/file.bin, which serves the same payload.</summary>
    public IReadOnlyCollection<string> MirrorRanges => _mirrorRanges;
    public int MaxConcurrentRequests => Volatile.Read(ref _maxActive);
    /// <summary>Requests being served now.</summary>
    public int ActiveRequests => Volatile.Read(ref _active);

    /// <summary>
    /// The host name and Cookie header of each request for /cookies.bin, which serves the payload, reached
    /// through /to-same-host/cookies.bin or through /to-other-host/cookies.bin (which moves to "localhost").
    /// </summary>
    public IReadOnlyCollection<(string Host, string Cookie)> CookiesSeen => _cookies;
    readonly ConcurrentQueue<(string Host, string Cookie)> _cookies = new();

    /// <summary>The Referer and User-Agent headers of each request for /cookies.bin.</summary>
    public IReadOnlyCollection<(string Referer, string UserAgent)> HeadersSeen => _headers;
    readonly ConcurrentQueue<(string Referer, string UserAgent)> _headers = new();

    /// <summary>
    /// The Authorization header of each request for /open.bin on "localhost", which /secure-redirect.bin
    /// redirects to once it has accepted the login user:pass.
    /// </summary>
    public IReadOnlyCollection<string> AuthorizationsElsewhere => _authorizationsElsewhere;
    readonly ConcurrentQueue<string> _authorizationsElsewhere = new();

    /// <summary>Requests for a path, whether or not it exists; unknown paths answer 404.</summary>
    public int Hits(string path) => _hits.GetValueOrDefault(path);
    public string MissingUrl => BaseUrl + "/missing.bin";

    /// <summary>A file whose content, ETag and Last-Modified all change with <see cref="SyncVersion"/>.</summary>
    public string SyncUrl => BaseUrl + "/sync.bin";
    public int SyncVersion { get => Volatile.Read(ref _syncVersion); set => Volatile.Write(ref _syncVersion, value); }
    int _syncVersion;
    public byte[] SyncPayload(int version) => Payload.Select(b => (byte)(b ^ (0x30 + version))).ToArray();

    /// <summary>Requests that arrived in absolute form ("GET http://host/path"), as a client sends them to a proxy.</summary>
    public int ProxiedRequests => Volatile.Read(ref _proxied);

    // Windows remembers a script by its address, across processes, so each server's scripts have addresses of their own.
    readonly string _scriptRun = Guid.NewGuid().ToString("N");

    /// <summary>A proxy script that sends adm.invalid through this server, which doubles as a proxy, and everything else direct.</summary>
    public string ProxyScriptUrl => $"{BaseUrl}/proxy.pac?run={_scriptRun}";

    /// <summary>A proxy script that answers every address with <paramref name="says"/>.</summary>
    public string ScriptSaying(string says) => $"{BaseUrl}/reply.pac?run={_scriptRun}&says={Uri.EscapeDataString(says)}";

    /// <summary>A script address that answers 404.</summary>
    public string MissingScriptUrl => $"{BaseUrl}/missing.pac?run={_scriptRun}";

    /// <summary>A script that loads but can't be run.</summary>
    public string BrokenScriptUrl => $"{BaseUrl}/broken.pac?run={_scriptRun}";

    /// <summary>A script that takes 5 seconds to arrive.</summary>
    public string HangingScriptUrl => $"{BaseUrl}/hanging.pac?run={_scriptRun}";

    /// <summary>A script answering 500 for its first <see cref="ScriptFailures"/> requests, then saying DIRECT.</summary>
    public string LateScriptUrl => $"{BaseUrl}/late.pac?run={_scriptRun}";
    public int ScriptFailures { get => Volatile.Read(ref _scriptFailures); set => Volatile.Write(ref _scriptFailures, value); }
    int _scriptFailures;
    int _lateScriptServed;
    int _fileBeforeLateScript;

    /// <summary>Requests for <see cref="FileUrl"/> that came before <see cref="LateScriptUrl"/> was first served: ones that went direct.</summary>
    public int FileHitsBeforeLateScript => Volatile.Read(ref _fileBeforeLateScript);

    /// <summary>The payload with ranges for a request that came through a proxy, and 403 for any other.</summary>
    public string ProxiedOnlyUrl => BaseUrl + "/proxied-only.bin";

    /// <summary>Always 403.</summary>
    public string ForbiddenUrl => BaseUrl + "/forbidden.bin";

    static IResult Pac(string answer) =>
        Results.Text($"function FindProxyForURL(url, host) {{ return {answer}; }}", "application/x-ns-proxy-autoconfig");

    /// <summary>The Range header of every request for a path, in arrival order; empty when there was none.</summary>
    public List<string> Ranges(string path) => _ranges.Where(r => r.Path == path).Select(r => r.Range).ToList();

    /// <summary>The distinct TCP connections requests arrived on.</summary>
    public IReadOnlyCollection<string> ConnectionIds => (IReadOnlyCollection<string>)_connectionIds.Keys;

    /// <summary>While true, every request's connection is dropped without an answer, as in a network outage.</summary>
    public bool Down { get => Volatile.Read(ref _down); set => Volatile.Write(ref _down, value); }

    /// <summary>The payload with ranges, each request sent at no more than <see cref="PerRequestKBps"/>.</summary>
    public string ThrottledUrl => BaseUrl + "/throttled.bin";
    public int PerRequestKBps { get => Volatile.Read(ref _perRequestKBps); set => Volatile.Write(ref _perRequestKBps, value); }

    /// <summary>A request whose range starts at byte 0 gets <see cref="SlowStartKBps"/>; the others are not capped.</summary>
    public string SlowStartUrl => BaseUrl + "/slowstart.bin";
    public int SlowStartKBps { get => Volatile.Read(ref _slowStartKBps); set => Volatile.Write(ref _slowStartKBps, value); }
    int _slowStartKBps = 64;

    /// <summary>A 302 to /file.bin.</summary>
    public string RedirectUrl => BaseUrl + "/redirect/file.bin";

    /// <summary>The payload, ignoring Range headers: every answer is a 200 with the whole file.</summary>
    public string NoRangesUrl => BaseUrl + "/noranges.bin";

    /// <summary>Honours a Range header for the first request only; later ones get a 200 with the whole file.</summary>
    public string RangesOnceUrl => BaseUrl + "/ranges-once.bin";
    int _rangesOnce;

    /// <summary>
    /// The payload as <see cref="ThrottledUrl"/> sends it, with a later Last-Modified on every request and no
    /// ETag, as servers do that stamp a file with the time of each request.
    /// </summary>
    public string TouchedUrl => BaseUrl + "/touched.bin";
    int _touches;

    /// <summary>
    /// The same size whatever the <see cref="Version"/>, with content that depends on it. ETag and
    /// Last-Modified follow the version, and are sent only while <see cref="Validators"/> is true.
    /// </summary>
    public string VersionedUrl => BaseUrl + "/versioned.bin";
    public int Version { get => Volatile.Read(ref _version); set => Volatile.Write(ref _version, value); }
    public bool Validators { get => Volatile.Read(ref _validators); set => Volatile.Write(ref _validators, value); }
    public byte[] VersionedPayload(int version) => _versions.GetOrAdd(version, v => Payload.Select(b => (byte)(b ^ (0x60 + v))).ToArray());

    /// <summary>
    /// Every request to /versioned.bin gets a new version, and the first request for the whole file
    /// ("bytes=0-") is dropped halfway, so the client has to continue it.
    /// </summary>
    public bool KeepsChanging { get; set; }
    int _droppedFromStart;

    /// <summary>Answers a one-byte probe, and every other request with a 500, counted in <see cref="ServerErrors"/>.</summary>
    public string ErrorUrl => BaseUrl + "/error.bin";
    public int ServerErrors => Volatile.Read(ref _serverErrors);
    int _serverErrors;

    /// <summary>
    /// At most two requests at once, each at no more than 1 MB/s. Others wait up to 200 ms for a place,
    /// then get a 503 with Retry-After: 1, counted in <see cref="Refused"/>.
    /// </summary>
    public string LimitedUrl => BaseUrl + "/limited.bin";
    public int Refused => Volatile.Read(ref _refused);

    /// <summary>
    /// Every request at no more than <see cref="PerRequestKBps"/>, and whatever the properties below ask for. The ones
    /// counting requests down look only at those starting past byte 0, which leaves a fresh download's first request alone.
    /// </summary>
    public string HoldingUrl => BaseUrl + "/holding.bin";

    /// <summary>This many requests starting past byte 0 get no answer until the client gives up, counted in <see cref="AbortedUnanswered"/>.</summary>
    public int NeverAnswerNext { get => Volatile.Read(ref _neverAnswerNext); set => Volatile.Write(ref _neverAnswerNext, value); }
    int _neverAnswerNext;

    /// <summary>This many requests starting at byte 0 get no answer until the client gives up.</summary>
    public int NeverAnswerFirst { get => Volatile.Read(ref _neverAnswerFirst); set => Volatile.Write(ref _neverAnswerFirst, value); }
    int _neverAnswerFirst;

    /// <summary>How long every request waits to be answered.</summary>
    public TimeSpan AnswerAfter { get; set; }

    /// <summary>The next request starting past byte 0 sends this many bytes and drops the connection.</summary>
    public int DropNextAfter { get => Volatile.Read(ref _dropNextAfter); set => Volatile.Write(ref _dropNextAfter, value); }
    int _dropNextAfter;

    /// <summary>With this set, only a request starting at exactly this offset is dropped by <see cref="DropNextAfter"/>.</summary>
    public long DropFrom { get; set; } = -1;

    /// <summary>From that drop on, requests get <see cref="FlippedPayload"/>, as if the file had changed on the server.</summary>
    public bool FlipOnDrop { get; set; }
    bool _flipped;

    public byte[] FlippedPayload => _flippedPayload ??= Payload.Select(b => (byte)(b ^ 0xFF)).ToArray();
    byte[]? _flippedPayload;

    /// <summary>This many requests starting past byte 0 get a 500.</summary>
    public int ErrorNext { get => Volatile.Read(ref _errorNext); set => Volatile.Write(ref _errorNext, value); }
    int _errorNext;

    /// <summary>The first request starting at exactly this offset gets a 500.</summary>
    public long ErrorFrom { get => Volatile.Read(ref _errorFrom); set => Volatile.Write(ref _errorFrom, value); }
    long _errorFrom = -1;

    /// <summary>Any request starting past byte 0 whose range holds this offset gets a 500.</summary>
    public long FailFrom { get; set; } = -1;

    /// <summary>A request starting at exactly this offset sends its first 4 KB over two seconds, and the rest at once.</summary>
    public long TrickleFrom { get; set; } = -1;

    /// <summary>The first request starting at exactly this offset sends 1 KB, then holds the connection open in silence.</summary>
    public long StallFrom { get => Volatile.Read(ref _stallFrom); set => Volatile.Write(ref _stallFrom, value); }
    long _stallFrom = -1;

    /// <summary>Requests given up by the client before they were answered.</summary>
    public int AbortedUnanswered => Volatile.Read(ref _abortedUnanswered);
    int _abortedUnanswered;

    /// <summary>Where each request to <see cref="HoldingUrl"/> started.</summary>
    public IReadOnlyCollection<long> HoldingStarts => _holdingStarts;
    readonly ConcurrentQueue<long> _holdingStarts = new();

    /// <summary>Once this many parts of <see cref="HlsTsUrl"/> have been served, <see cref="Down"/> goes on for <see cref="HlsOutage"/>.</summary>
    public int HlsOutageAfterParts { get => Volatile.Read(ref _hlsOutageAfter); set => Volatile.Write(ref _hlsOutageAfter, value); }
    int _hlsOutageAfter;
    public TimeSpan HlsOutage { get; set; }

    /// <summary>A playlist of <see cref="HlsParts"/> byte ranges of one file, which join to make the payload.</summary>
    public string HlsByteRangeUrl => BaseUrl + "/hls/byterange.m3u8";

    /// <summary>The same, giving each range after the first no offset, so that it follows on from the one before.</summary>
    public string HlsByteRangeFollowingUrl => BaseUrl + "/hls/byterange-following.m3u8";

    /// <summary>A range with no offset, and nothing before it to follow on from.</summary>
    public string HlsByteRangeMalformedUrl => BaseUrl + "/hls/byterange-malformed.m3u8";

    /// <summary>The first range is an initialisation section, named by #EXT-X-MAP with a byte range of its own.</summary>
    public string HlsByteRangeMapUrl => BaseUrl + "/hls/byterange-map.m3u8";

    /// <summary>Ranges of a file holding each part encrypted by itself, as AES-128 in HLS does.</summary>
    public string HlsByteRangeAesUrl => BaseUrl + "/hls/byterange-aes.m3u8";

    /// <summary>Ranges of a file served with no support for ranges.</summary>
    public string HlsByteRangeNoRangesUrl => BaseUrl + "/hls/byterange-noranges.m3u8";

    /// <summary>Ranges of a file that says it is sending a different range from the one asked for.</summary>
    public string HlsByteRangeWrongUrl => BaseUrl + "/hls/byterange-wrong.m3u8";

    /// <summary>Ranges of a file answering the first request for each with half of what it asked for.</summary>
    public string HlsByteRangeShortUrl => BaseUrl + "/hls/byterange-short.m3u8";

    /// <summary>A master playlist with byte-range subtitles.</summary>
    public string HlsByteRangeSubtitlesUrl => BaseUrl + "/hls/byterange-subs/master.m3u8";

    /// <summary>Moves every range in <see cref="HlsByteRangeUrl"/> along the file, with the addresses as they were.</summary>
    public int HlsByteRangeShift { get; set; }

    /// <summary>A stream whose one TS part starts at a presentation time of <see cref="HlsPtsStart"/>, with the same subtitles as <see cref="HlsAvUrl"/>.</summary>
    public string HlsPtsUrl => BaseUrl + "/hls/pts/master.m3u8";
    public long HlsPtsStart { get; set; }

    /// <summary>A live stream with separate audio. Audio parts from <see cref="LiveAudioFailFrom"/> on always answer 500.</summary>
    public string HlsLiveAvUrl(int? end = null) => BaseUrl + "/hls/liveav/master.m3u8" + (end is int n ? $"?end={n}" : "");
    public long LiveAudioFailFrom { get; set; } = -1;
    public static byte[] LiveAudioPart(long sequence) => Enumerable.Repeat((byte)(sequence % 251 + 101), 2048).ToArray();
    int _liveVideoRequests, _liveAudioRequests;
    readonly ConcurrentDictionary<long, int> _shortHits = new();

    /// <summary>A playlist of one part, which always answers 500, counted in <see cref="ServerErrors"/>.</summary>
    public string HlsErrorUrl => BaseUrl + "/hls/error.m3u8";

    /// <summary>
    /// The payload with no size, no validators and no ranges, a little at a time, so that a client hanging up
    /// partway leaves <see cref="NoInfoSent"/> well short of the whole file.
    /// </summary>
    public string NoInfoUrl => BaseUrl + "/noinfo.bin";
    public long NoInfoSent { get => Interlocked.Read(ref _noInfoSent); set => Interlocked.Exchange(ref _noInfoSent, value); }
    long _noInfoSent;

    /// <summary>Serves <see cref="FlippedPayload"/>, as if the file had changed on the server.</summary>
    public bool NoInfoFlipped { get; set; }

    /// <summary>Serves only this much of the file; 0 is all of it.</summary>
    public int NoInfoLength { get; set; }

    // DASH manifests over the same parts as the HLS playlists, so each one's tracks join back to Payload.
    public string DashUrl(string name) => BaseUrl + "/dash/" + name + ".mpd";
    /// <summary>The path of the file <c>whole</c> and <c>segment-base</c> download, which sends byte ranges.</summary>
    public const string DashWholePath = "/hls/whole.bin";
    /// <summary>Whether <c>changing</c> names its parts in another order, as a manifest for a changed stream would, and <c>sized</c> names a shorter file.</summary>
    public bool DashChanged { get; set; }

    // Video tools as their publishers serve them: yt-dlp.exe with a sums file, Deno's zip with its own sum, ffmpeg's zip with a sums file.
    /// <summary>The yt-dlp version /tools/yt-dlp.exe serves, and its sum names.</summary>
    public int ToolsVersion { get => Volatile.Read(ref _toolsVersion); set => Volatile.Write(ref _toolsVersion, value); }
    int _toolsVersion = 1;
    /// <summary>Whether each tool served differs from the sum published for it.</summary>
    public bool ToolsTampered { get; set; }
    public ToolSources ToolSources => new(new Uri(BaseUrl + "/tools/yt-dlp.exe"), new Uri(BaseUrl + "/tools/SHA2-256SUMS"),
        new Uri(BaseUrl + "/tools/deno.zip"), new Uri(BaseUrl + "/tools/deno.zip.sha256sum"),
        new Uri(BaseUrl + "/tools/ffmpeg-master-latest-win64-lgpl.zip"), new Uri(BaseUrl + "/tools/checksums.sha256"));
    /// <summary>The same tools behind links that redirect to another host, as GitHub's release links do.</summary>
    public ToolSources RedirectedToolSources => new(Latest("yt-dlp.exe"), Latest("SHA2-256SUMS"), Latest("deno.zip"),
        Latest("deno.zip.sha256sum"), Latest("ffmpeg-master-latest-win64-lgpl.zip"), Latest("checksums.sha256"));
    Uri Latest(string name) => new(BaseUrl + "/tools/latest/" + name);
    /// <summary>The bytes of a tool's program: what yt-dlp.exe is, and what deno.exe and ffmpeg.exe are inside their zips.</summary>
    public static byte[] ToolBytes(string name, int version = 1) => Encoding.ASCII.GetBytes($"{name} v{version}");

    // A site's files, as yt-dlp would name them: the payload's first half is the picture, its second the sound.
    public string SiteUrl(string name) => BaseUrl + "/site/" + name;
    public byte[] SiteBytes(string name) => name switch
    {
        "video.mp4" or "video.webm" => Payload[..(Payload.Length / 2)],
        "audio.m4a" or "audio.webm" => Payload[(Payload.Length / 2)..],
        "other.mp4" => _otherFile ??= Payload.Reverse().Skip(1000).ToArray(),
        _ => [],
    };
    byte[]? _otherFile;
    /// <summary>Each request for a site's file gets no more than this; 0 is uncapped.</summary>
    public int SiteKBps { get => Volatile.Read(ref _siteKBps); set => Volatile.Write(ref _siteKBps, value); }
    int _siteKBps;
    /// <summary>Until when a site's files are refused with a 403, as YouTube's are until the page's preroll ad would have played.</summary>
    public DateTimeOffset SiteAvailableAt { get; set; } = DateTimeOffset.MinValue;
    /// <summary>While set, each request for a site's file is counted in <see cref="SiteRequests"/>, then waits for it to complete before it is answered.</summary>
    public TaskCompletionSource? SiteHold { get; set; }
    /// <summary>Each request for a site's file: its path, its query, and the User-Agent and Cookie it came with.</summary>
    public IReadOnlyCollection<(string Path, string Query, string UserAgent, string Cookie)> SiteRequests => _siteRequests;
    readonly ConcurrentQueue<(string, string, string, string)> _siteRequests = new();

    /// <summary>This server under another name, which counts as another site.</summary>
    public string OtherSite => BaseUrl.Replace("127.0.0.1", "localhost");

    /// <summary>Parts on <see cref="OtherSite"/>, which <see cref="CookiesSeen"/> sees.</summary>
    public string HlsOtherSiteUrl => BaseUrl + "/hls/other-site.m3u8";
    /// <summary>Parts on the playlist's own site, which <see cref="CookiesSeen"/> sees.</summary>
    public string HlsSameSiteUrl => BaseUrl + "/hls/same-site.m3u8";
    /// <summary>A master playlist whose quality's playlist is on <see cref="OtherSite"/>, which <see cref="CookiesSeen"/> sees.</summary>
    public string HlsOtherSiteMasterUrl => BaseUrl + "/hls/other-site-master.m3u8";
    /// <summary>AES-128 parts whose key is on <see cref="OtherSite"/>, which <see cref="CookiesSeen"/> sees.</summary>
    public string HlsOtherSiteKeyUrl => BaseUrl + "/hls/other-site-key.m3u8";

    /// <summary>A redirect to <see cref="HlsTsUrl"/>, from a folder its relative parts aren't in.</summary>
    public string HlsMovedUrl => BaseUrl + "/hls/moved/ts.m3u8";
    /// <summary>A redirect to <see cref="HlsMasterUrl"/>, from a folder its relative qualities aren't in.</summary>
    public string HlsMovedMasterUrl => BaseUrl + "/hls/moved/master.m3u8";
    /// <summary>A master playlist whose one quality is a redirect to <see cref="HlsTsUrl"/>.</summary>
    public string HlsMovedQualityUrl => BaseUrl + "/hls/moved-quality.m3u8";
    /// <summary>A redirect to <see cref="HlsLiveUrl"/>, whose hits <see cref="Hits"/> counts at this path.</summary>
    public const string HlsMovedLivePath = "/hls/moved/live.m3u8";

    /// <summary>A playlist of the first <see cref="HlsSyncParts"/> parts.</summary>
    public string HlsSyncUrl => BaseUrl + "/hls/sync.m3u8";
    public int HlsSyncParts { get; set; } = HlsParts;

    /// <summary>A playlist whose parts' addresses carry a token that changes on every load.</summary>
    public string HlsTokensUrl => BaseUrl + "/hls/tokens.m3u8";
    int _hlsTokens;

    /// <summary>The first request for more than one byte sends half its range, then holds the connection open in silence.</summary>
    public string StallUrl => BaseUrl + "/stall.bin";

    /// <summary>
    /// Every request at no more than <see cref="PerRequestKBps"/>. The first request whose range starts past
    /// byte 0 sends 64 KB, then holds the connection open in silence, as a connection that dies without closing does.
    /// </summary>
    public string StallingUrl => BaseUrl + "/stalling.bin";
    int _stallingOne;

    /// <summary>
    /// A 302 to a signed address, /signed.bin?t=n, which answers 410 Gone once it is out of date. The
    /// first signed address expires as soon as it is used; the ones after it last.
    /// </summary>
    public string ExpiringUrl => BaseUrl + "/expiring.bin";
    int _signed;

    // HLS playlists over the payload cut into HlsParts parts; each one's segments join back to Payload.
    public const int HlsParts = 8;
    /// <summary>Plain segments.</summary>
    public string HlsTsUrl => BaseUrl + "/hls/ts.m3u8";
    /// <summary>Part 0 as the initialisation section, then parts 1 onwards.</summary>
    public string HlsFmp4Url => BaseUrl + "/hls/fmp4.m3u8";
    /// <summary>AES-128 segments; the IV is the media sequence number.</summary>
    public string HlsAesUrl => BaseUrl + "/hls/aes.m3u8";
    /// <summary>Each segment's first request sends half its bytes, then drops the connection.</summary>
    public string HlsFlakyUrl => BaseUrl + "/hls/flaky.m3u8";
    public int HlsFlakyRequests => _flakyHits.Values.Sum();
    readonly ConcurrentDictionary<int, int> _flakyHits = new();

    /// <summary>Three qualities, each serving <see cref="VariantPayload"/> of its index.</summary>
    public string HlsMasterUrl => BaseUrl + "/hls/master.m3u8";
    public static readonly long[] HlsBandwidths = { 500000, 1500000, 3000000 };
    public byte[] VariantPayload(int variant) => Payload.Select(b => (byte)(b ^ (variant + 1))).ToArray();

    /// <summary>The TS playlist as video, a separate audio track of <see cref="AudioPayload"/>, and two WebVTT segments.</summary>
    public string HlsAvUrl => BaseUrl + "/hls/av/master.m3u8";
    public byte[] AudioPayload => Payload.Select(b => (byte)(b ^ 0x55)).ToArray();

    /// <summary>A live playlist whose three-segment window moves on by one segment per request; with an end, it stops at segment end - 1.</summary>
    public string HlsLiveUrl(int? end = null) => BaseUrl + "/hls/live.m3u8" + (end is int n ? $"?end={n}" : "");
    public int HlsLiveRequests => Volatile.Read(ref _liveRequests);
    int _liveRequests;
    public static byte[] LivePart(long sequence) => Enumerable.Repeat((byte)(sequence % 251 + 1), 4096).ToArray();

    byte[] HlsPart(int i) => Slice(Payload, i);

    static byte[] Slice(byte[] data, int i) => data[(int)((long)data.Length * i / HlsParts)..(int)((long)data.Length * (i + 1) / HlsParts)];

    public static Task<TestServer> StartAsync(int size, TimeSpan? holdEachRequest = null)
    {
        var payload = new byte[size];
        new Random(1234).NextBytes(payload);
        return StartAsync(payload, holdEachRequest);
    }

    /// <summary>A server whose routes all serve <paramref name="payload"/>, such as an archive built by a test.</summary>
    public static async Task<TestServer> StartAsync(byte[] payload, TimeSpan? holdEachRequest = null)
    {
        var hold = holdEachRequest ?? TimeSpan.FromMilliseconds(150);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new TestServer(app, payload);

        app.Use(async (context, next) =>
        {
            // Kestrel serves absolute-form targets, so the server doubles as an HTTP proxy for itself.
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
                Interlocked.Increment(ref server._proxied);
            var path = context.Request.Path.Value ?? "";
            server._hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            server._ranges.Enqueue((path, context.Request.Headers.Range.ToString()));
            server._connectionIds.TryAdd(context.Connection.Id, 0);
            if (server.Down)
            {
                context.Abort();
                return;
            }
            var now = Interlocked.Increment(ref server._active);
            InterlockedMax(ref server._maxActive, now);
            try
            {
                await Task.Delay(hold);
                await next(context);
            }
            finally
            {
                Interlocked.Decrement(ref server._active);
            }
        });
        app.MapGet("/file.bin", () =>
        {
            if (Volatile.Read(ref server._lateScriptServed) == 0) Interlocked.Increment(ref server._fileBeforeLateScript);
            return Results.Bytes(payload, "application/octet-stream", enableRangeProcessing: true);
        });
        app.MapGet("/proxy.pac", () => Pac($"host == \"adm.invalid\" ? \"PROXY {new Uri(server.BaseUrl).Authority}\" : \"DIRECT\""));
        app.MapGet("/reply.pac", (string says) => Pac($"\"{says}\""));
        app.MapGet("/hanging.pac", async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
            return Pac("\"DIRECT\"");
        });
        app.MapGet("/broken.pac", () => Results.Text("this is not a script (((", "application/x-ns-proxy-autoconfig"));
        app.MapGet("/late.pac", () =>
        {
            if (Interlocked.Decrement(ref server._scriptFailures) >= 0) return Results.StatusCode(500);
            Volatile.Write(ref server._lateScriptServed, 1);
            return Pac("\"DIRECT\"");
        });
        app.MapGet("/proxied-only.bin", (HttpContext context) =>
            context.Features.Get<IHttpRequestFeature>()?.RawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? Results.Bytes(payload, "application/octet-stream", enableRangeProcessing: true)
                : Results.StatusCode(403));
        app.MapGet("/forbidden.bin", () => Results.StatusCode(403));
        app.MapGet("/sync.bin", () =>
        {
            var version = server.SyncVersion;
            return Results.Bytes(server.SyncPayload(version), "application/octet-stream", enableRangeProcessing: true,
                lastModified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(version),
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"v{version}\""));
        });

        var login = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass"));
        app.MapGet("/secure.bin", (HttpContext context) =>
        {
            Interlocked.Increment(ref server._secure);
            if (context.Request.Headers.Authorization == login)
                return Results.Bytes(payload, "application/octet-stream", enableRangeProcessing: true);
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"adm\"";
            return Results.Unauthorized();
        });
        // Same server under another host name, so a login tied to 127.0.0.1 must not follow.
        app.MapGet("/to-other-host", () => Results.Redirect(server.BaseUrl.Replace("127.0.0.1", "localhost") + "/secure.bin"));
        app.MapGet("/to-same-host", () => Results.Redirect("/secure.bin"));
        app.MapGet("/to-other-host/cookies.bin", () => Results.Redirect(server.BaseUrl.Replace("127.0.0.1", "localhost") + "/cookies.bin"));
        app.MapGet("/to-same-host/cookies.bin", () => Results.Redirect("/cookies.bin"));
        app.MapGet("/cookies.bin", (HttpContext context) =>
        {
            server._cookies.Enqueue((context.Request.Host.Host, context.Request.Headers.Cookie.ToString()));
            server._headers.Enqueue((context.Request.Headers.Referer.ToString(), context.Request.Headers.UserAgent.ToString()));
            return server.ServeAsync(context, payload, 0);
        });
        // Behind the login, a redirect to another host, which records any Authorization header it is sent.
        app.MapGet("/secure-redirect.bin", (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization == login)
                return Results.Redirect(server.BaseUrl.Replace("127.0.0.1", "localhost") + "/open.bin");
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"adm\"";
            return Results.Unauthorized();
        });
        app.MapGet("/open.bin", (HttpContext context) =>
        {
            server._authorizationsElsewhere.Enqueue(context.Request.Headers.Authorization.ToString());
            return server.ServeAsync(context, payload, 0);
        });
        app.MapGet("/mirror/file.bin", (HttpContext context) =>
        {
            server._mirrorRanges.Enqueue(context.Request.Headers.Range.ToString());
            return Results.Bytes(payload, "application/octet-stream", enableRangeProcessing: true);
        });
        MapSpeedAndResume(app, server);
        MapHls(app, server);
        MapDash(app, server);
        MapTools(app, server);

        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return server;
    }

    static void MapSpeedAndResume(WebApplication app, TestServer server)
    {
        var payload = server.Payload;
        app.MapGet("/throttled.bin", (HttpContext context) => server.ServeAsync(context, payload, server.PerRequestKBps * 1024L));
        app.MapGet("/slowstart.bin", (HttpContext context) =>
            server.ServeAsync(context, payload, ParseRange(context, payload.Length).From == 0 ? server.SlowStartKBps * 1024L : 0));
        app.MapGet("/redirect/file.bin", () => Results.Redirect("/file.bin"));
        app.MapGet("/noranges.bin", () => Results.Bytes(payload, "application/octet-stream"));
        app.MapGet("/ranges-once.bin", () => Results.Bytes(payload, "application/octet-stream", enableRangeProcessing: Interlocked.Increment(ref server._rangesOnce) == 1));
        app.MapGet("/touched.bin", (HttpContext context) =>
        {
            context.Response.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMinutes(Interlocked.Increment(ref server._touches)).ToString("R");
            return server.ServeAsync(context, payload, server.PerRequestKBps * 1024L);
        });

        app.MapGet("/versioned.bin", (HttpContext context) =>
        {
            var version = server.KeepsChanging ? Interlocked.Increment(ref server._version) : server.Version;
            if (server.Validators)
            {
                context.Response.Headers.ETag = $"\"v{version}\"";
                context.Response.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(version).ToString("R");
            }
            var drop = server.KeepsChanging && context.Request.Headers.Range == "bytes=0-"
                && Interlocked.CompareExchange(ref server._droppedFromStart, 1, 0) == 0;
            return server.ServeAsync(context, server.VersionedPayload(version), 0, dropAfter: drop ? payload.Length / 2 : -1);
        });

        app.MapGet("/error.bin", (HttpContext context) =>
        {
            if (context.Request.Headers.Range == "bytes=0-0") return server.ServeAsync(context, payload, 0);
            Interlocked.Increment(ref server._serverErrors);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        app.MapGet("/limited.bin", async (HttpContext context) =>
        {
            // With both places taken, wait a moment for one to free: a client that just closed a
            // connection may already be asking again before its old request has been let go.
            var waited = Stopwatch.StartNew();
            while (Interlocked.Increment(ref server._limitedActive) > 2)
            {
                Interlocked.Decrement(ref server._limitedActive);
                if (waited.ElapsedMilliseconds > 200)
                {
                    Interlocked.Increment(ref server._refused);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "1";
                    return;
                }
                await Task.Delay(5);
            }
            try
            {
                await server.ServeAsync(context, payload, 1024 * 1024);
            }
            finally
            {
                Interlocked.Decrement(ref server._limitedActive);
            }
        });

        app.MapGet("/expiring.bin", () => Results.Redirect($"/signed.bin?t={Volatile.Read(ref server._signed)}"));
        app.MapGet("/signed.bin", (HttpContext context, int t) =>
        {
            if (t != Volatile.Read(ref server._signed))
            {
                context.Response.StatusCode = StatusCodes.Status410Gone;
                return Task.CompletedTask;
            }
            if (t == 0) Interlocked.CompareExchange(ref server._signed, 1, 0);
            return server.ServeAsync(context, payload, 0);
        });

        app.MapGet("/holding.bin", (HttpContext context) => server.ServeHoldingAsync(context));

        app.MapGet("/secret/{token}/{name}", (HttpContext context) => context.Response.StatusCode = StatusCodes.Status500InternalServerError);

        app.MapGet("/noinfo.bin", async (HttpContext context) =>
        {
            var data = server.NoInfoFlipped ? server.FlippedPayload : payload;
            var length = server.NoInfoLength > 0 ? server.NoInfoLength : data.Length;
            context.Response.ContentType = "application/octet-stream";
            const int piece = 64 * 1024;
            for (int at = 0; at < length; at += piece)
            {
                int count = Math.Min(piece, length - at);
                await context.Response.Body.WriteAsync(data.AsMemory(at, count), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                Interlocked.Add(ref server._noInfoSent, count);
                await Task.Delay(5, context.RequestAborted);
            }
        });

        app.MapGet("/stall.bin", (HttpContext context) =>
        {
            var (from, to) = ParseRange(context, payload.Length);
            var stall = to > from && Interlocked.CompareExchange(ref server._stalled, 1, 0) == 0;
            return server.ServeAsync(context, payload, 0, stall ? (to - from + 1) / 2 : -1);
        });

        app.MapGet("/stalling.bin", (HttpContext context) =>
        {
            var stall = ParseRange(context, payload.Length).From > 0 && Interlocked.CompareExchange(ref server._stallingOne, 1, 0) == 0;
            return server.ServeAsync(context, payload, server.PerRequestKBps * 1024L, stall ? 64 * 1024 : -1);
        });
    }

    async Task ServeHoldingAsync(HttpContext context)
    {
        var (from, to) = ParseRange(context, Payload.Length);
        _holdingStarts.Enqueue(from);
        try
        {
            if (from > 0 ? TakeOne(ref _neverAnswerNext) : TakeOne(ref _neverAnswerFirst))
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            if (AnswerAfter > TimeSpan.Zero) await Task.Delay(AnswerAfter, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _abortedUnanswered);
            return;
        }

        if (from > 0 && (TakeOne(ref _errorNext) || (FailFrom >= from && FailFrom <= to)
            || (from == ErrorFrom && Interlocked.CompareExchange(ref _errorFrom, -1, from) == from)))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        if (from == TrickleFrom)
        {
            await TrickleAsync(context, from, to);
            return;
        }
        var data = Volatile.Read(ref _flipped) ? FlippedPayload : Payload;
        var drop = from > 0 && (DropFrom < 0 || from == DropFrom) ? Interlocked.Exchange(ref _dropNextAfter, 0) : 0;
        if (drop > 0 && FlipOnDrop) Volatile.Write(ref _flipped, true);
        var stall = from == StallFrom && Interlocked.CompareExchange(ref _stallFrom, -1, from) == from;
        await ServeAsync(context, data, PerRequestKBps * 1024L, stall ? 1024 : -1, drop > 0 ? drop : -1);

        static bool TakeOne(ref int count)
        {
            int left;
            while ((left = Volatile.Read(ref count)) > 0)
                if (Interlocked.CompareExchange(ref count, left - 1, left) == left) return true;
            return false;
        }
    }

    async Task TrickleAsync(HttpContext context, long from, long to)
    {
        var response = context.Response;
        response.ContentType = "application/octet-stream";
        response.Headers.AcceptRanges = "bytes";
        response.StatusCode = StatusCodes.Status206PartialContent;
        response.Headers.ContentRange = $"bytes {from}-{to}/{Payload.Length}";
        response.ContentLength = to - from + 1;
        const int piece = 1024;
        int at = (int)from;
        for (int i = 0; i < 4; i++, at += piece)
        {
            await response.Body.WriteAsync(Payload.AsMemory(at, piece), context.RequestAborted);
            await response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(500, context.RequestAborted);
        }
        await response.Body.WriteAsync(Payload.AsMemory(at, (int)(to - at + 1)), context.RequestAborted);
    }

    /// <summary>The requested byte range, or the whole file; suffix and multiple ranges are not supported.</summary>
    static (long From, long To) ParseRange(HttpContext context, long length)
    {
        var header = context.Request.Headers.Range.ToString();
        if (!header.StartsWith("bytes=", StringComparison.Ordinal)) return (0, length - 1);
        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var from)) return (0, length - 1);
        var to = parts[1].Length > 0 && long.TryParse(parts[1], out var end) ? Math.Min(end, length - 1) : length - 1;
        return (from, to);
    }

    /// <summary>
    /// Serves <paramref name="data"/> with byte ranges at no more than <paramref name="bytesPerSecond"/>
    /// (0 is unlimited). With <paramref name="stallAfter"/>, sends that many bytes and then holds the
    /// connection open, sending nothing, until the client goes away; with <paramref name="dropAfter"/>,
    /// sends that many and drops the connection. While <see cref="Down"/> is set, the connection drops.
    /// </summary>
    async Task ServeAsync(HttpContext context, byte[] data, long bytesPerSecond, long stallAfter = -1, long dropAfter = -1)
    {
        var response = context.Response;
        var aborted = context.RequestAborted;
        var (from, to) = ParseRange(context, data.Length);
        response.ContentType = "application/octet-stream";
        response.Headers.AcceptRanges = "bytes";
        if (context.Request.Headers.Range.Count > 0)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {from}-{to}/{data.Length}";
        }
        long length = to - from + 1, sent = 0;
        response.ContentLength = length;

        var watch = Stopwatch.StartNew();
        try
        {
            while (sent < length)
            {
                if (Down || (dropAfter >= 0 && sent >= dropAfter))
                {
                    context.Abort();
                    return;
                }
                if (stallAfter >= 0 && sent >= stallAfter)
                    await Task.Delay(Timeout.Infinite, aborted);
                var n = (int)Math.Min(16 * 1024, length - sent);
                if (stallAfter >= 0) n = (int)Math.Min(n, stallAfter - sent);
                if (dropAfter >= 0) n = (int)Math.Min(n, dropAfter - sent);
                await response.Body.WriteAsync(data.AsMemory((int)(from + sent), n), aborted);
                await response.Body.FlushAsync(aborted);
                sent += n;
                if (bytesPerSecond > 0)
                {
                    var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond) - watch.Elapsed;
                    if (due > TimeSpan.Zero) await Task.Delay(due, aborted);
                }
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
        }
    }

    static readonly byte[] HlsKey = Enumerable.Range(1, 16).Select(b => (byte)b).ToArray();

    static void MapHls(WebApplication app, TestServer server)
    {
        const string mpegUrl = "application/vnd.apple.mpegurl";
        static string Playlist(IEnumerable<int> parts, Func<int, string> uri, string header = "") =>
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n" + header
            + string.Concat(parts.Select(i => $"#EXTINF:4.0,\n{uri(i)}\n")) + "#EXT-X-ENDLIST\n";
        var all = Enumerable.Range(0, HlsParts);

        app.MapGet("/hls/ts.m3u8", () => Results.Text(Playlist(all, i => $"part/{i}"), mpegUrl));
        app.MapGet("/hls/fmp4.m3u8", () => Results.Text(Playlist(all.Skip(1), i => $"part/{i}", "#EXT-X-MAP:URI=\"part/0\"\n"), mpegUrl));
        app.MapGet("/hls/aes.m3u8", () => Results.Text(Playlist(all, i => $"enc/{i}", "#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n"), mpegUrl));
        app.MapGet("/hls/flaky.m3u8", () => Results.Text(Playlist(all, i => $"flaky/{i}"), mpegUrl));

        app.MapGet("/hls/part/{i:int}", (int i) =>
        {
            if (Interlocked.Decrement(ref server._hlsOutageAfter) == 0)
            {
                server.Down = true;
                _ = Task.Delay(server.HlsOutage).ContinueWith(_ => server.Down = false, TaskScheduler.Default);
            }
            return Results.Bytes(server.HlsPart(i), "video/mp2t");
        });
        app.MapGet("/hls/sync.m3u8", () => Results.Text(Playlist(Enumerable.Range(0, server.HlsSyncParts), i => $"part/{i}"), mpegUrl));
        app.MapGet("/hls/tokens.m3u8", () =>
        {
            var token = Interlocked.Increment(ref server._hlsTokens);
            return Results.Text(Playlist(all, i => $"part/{i}?token={token}"), mpegUrl);
        });
        app.MapGet("/hls/other-site.m3u8", () => Results.Text(Playlist(all, i => $"{server.OtherSite}/hls/cookies/{i}"), mpegUrl));
        app.MapGet("/hls/same-site.m3u8", () => Results.Text(Playlist(all, i => $"cookies/{i}"), mpegUrl));
        app.MapGet("/hls/cookies/{i:int}", (HttpContext context, int i) =>
        {
            server._cookies.Enqueue((context.Request.Host.Host, context.Request.Headers.Cookie.ToString()));
            return Results.Bytes(server.HlsPart(i), "video/mp2t");
        });
        app.MapGet("/hls/other-site-master.m3u8", () => Results.Text(
            $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\n{server.OtherSite}/hls/cookies-playlist.m3u8\n", mpegUrl));
        app.MapGet("/hls/cookies-playlist.m3u8", (HttpContext context) =>
        {
            server._cookies.Enqueue((context.Request.Host.Host, context.Request.Headers.Cookie.ToString()));
            return Results.Text(Playlist(all, i => $"part/{i}"), mpegUrl);
        });
        app.MapGet("/hls/other-site-key.m3u8", () => Results.Text(Playlist(all, i => $"enc/{i}", $"#EXT-X-KEY:METHOD=AES-128,URI=\"{server.OtherSite}/hls/cookies-key\"\n"), mpegUrl));
        app.MapGet("/hls/cookies-key", (HttpContext context) =>
        {
            server._cookies.Enqueue((context.Request.Host.Host, context.Request.Headers.Cookie.ToString()));
            return Results.Bytes(HlsKey, "application/octet-stream");
        });
        app.MapGet("/hls/moved/ts.m3u8", () => Results.Redirect("/hls/ts.m3u8"));
        app.MapGet("/hls/moved/master.m3u8", () => Results.Redirect("/hls/master.m3u8"));
        app.MapGet("/hls/moved/live.m3u8", () => Results.Redirect("/hls/live.m3u8"));
        app.MapGet("/hls/moved-quality.m3u8", () => Results.Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nmoved/ts.m3u8\n", mpegUrl));
        MapByteRanges(app, server);
        MapLiveAudio(app, server);
        app.MapGet("/hls/error.m3u8", () => Results.Text(Playlist(new[] { 0 }, i => $"error/{i}"), mpegUrl));
        app.MapGet("/hls/error/{i:int}", (HttpContext context, int i) =>
        {
            Interlocked.Increment(ref server._serverErrors);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        });
        app.MapGet("/hls/key", () => Results.Bytes(HlsKey, "application/octet-stream"));
        app.MapGet("/hls/enc/{i:int}", (int i) =>
        {
            using var aes = Aes.Create();
            aes.Key = HlsKey;
            // With no IV in the playlist, the IV is the segment's media sequence number, big-endian.
            var iv = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), i);
            return Results.Bytes(aes.EncryptCbc(server.HlsPart(i), iv, PaddingMode.PKCS7), "video/mp2t");
        });

        app.MapGet("/hls/master.m3u8", () => Results.Text("#EXTM3U\n" + string.Concat(HlsBandwidths.Select((b, v) =>
            $"#EXT-X-STREAM-INF:BANDWIDTH={b},RESOLUTION={new[] { "640x360", "1280x720", "1920x1080" }[v]},CODECS=\"avc1.4d401f,mp4a.40.2\"\nvariant/{v}.m3u8\n")), mpegUrl));
        app.MapGet("/hls/variant/{v:int}.m3u8", (int v) => Results.Text(Playlist(all, i => $"{v}/{i}"), mpegUrl));
        app.MapGet("/hls/variant/{v:int}/{i:int}", (int v, int i) => Results.Bytes(Slice(server.VariantPayload(v), i), "video/mp2t"));

        app.MapGet("/hls/av/master.m3u8", () => Results.Text("""
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="subs.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2",AUDIO="aud",SUBTITLES="subs"
            ../ts.m3u8
            """, mpegUrl));
        app.MapGet("/hls/av/audio.m3u8", () => Results.Text(Playlist(all, i => $"audio/{i}.aac"), mpegUrl));
        app.MapGet("/hls/av/audio/{i:int}.aac", (int i) => Results.Bytes(Slice(server.AudioPayload, i), "audio/aac"));
        app.MapGet("/hls/av/subs.m3u8", () => Results.Text(Playlist(new[] { 0, 1 }, i => $"subs/{i}.vtt"), mpegUrl));
        app.MapGet("/hls/av/subs/{i:int}.vtt", (int i) => Results.Text(
            $"WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:0{1 + 4 * i}.000 --> 00:00:0{2 + 4 * i}.000\n{(i == 0 ? "Hello" : "World")}\n",
            "text/vtt"));

        app.MapGet("/hls/live.m3u8", (int? end) =>
        {
            long first = Interlocked.Increment(ref server._liveRequests) - 1, last = first + 2;
            if (end is int n && last >= n - 1) (first, last) = (Math.Max(0, n - 3), n - 1);
            var ended = end is int m && last == m - 1;
            return Results.Text(
                $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:{first}\n"
                + string.Concat(Enumerable.Range((int)first, (int)(last - first + 1)).Select(s => $"#EXTINF:1.0,\nlive/{s}.ts\n"))
                + (ended ? "#EXT-X-ENDLIST\n" : ""), mpegUrl);
        });
        app.MapGet("/hls/live/{s:long}.ts", (long s) => Results.Bytes(LivePart(s), "video/mp2t"));

        app.MapGet("/hls/flaky/{i:int}", async (HttpContext context, int i) =>
        {
            var part = server.HlsPart(i);
            context.Response.ContentType = "video/mp2t";
            context.Response.ContentLength = part.Length;
            if (server._flakyHits.AddOrUpdate(i, 1, (_, n) => n + 1) == 1)
            {
                await context.Response.Body.WriteAsync(part.AsMemory(0, part.Length / 2));
                await context.Response.Body.FlushAsync();
                context.Abort();
                return;
            }
            await context.Response.Body.WriteAsync(part);
        });
    }

    static void MapTools(WebApplication app, TestServer server)
    {
        static string Sum(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        static byte[] Zip(string entry, byte[] program)
        {
            var buffer = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var stream = zip.CreateEntry(entry).Open()) stream.Write(program);
                using var readme = new StreamWriter(zip.CreateEntry("README.txt").Open());
                readme.Write("not a program");
            }
            return buffer.ToArray();
        }
        byte[] Served(byte[] published) => server.ToolsTampered ? published.Concat("!"u8.ToArray()).ToArray() : published;
        var deno = Zip("deno.exe", ToolBytes("deno"));
        var ffmpeg = Zip("ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe", ToolBytes("ffmpeg"));
        const string octets = "application/octet-stream";

        app.MapGet("/site/{name}", async (HttpContext context, string name) =>
        {
            var request = context.Request;
            server._siteRequests.Enqueue((request.Path.Value ?? "", request.QueryString.Value ?? "", request.Headers.UserAgent.ToString(), request.Headers.Cookie.ToString()));
            if (server.SiteHold is { } hold) await hold.Task;
            if (DateTimeOffset.UtcNow < server.SiteAvailableAt)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var data = server.SiteBytes(name);
            if (data.Length > 0)
            {
                await server.ServeAsync(context, data, server.SiteKBps * 1024L);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        app.MapGet("/tools/latest/{name}", (string name) => Results.Redirect(server.BaseUrl.Replace("127.0.0.1", "localhost") + "/tools/" + name));
        app.MapGet("/tools/yt-dlp.exe",() => Results.Bytes(Served(ToolBytes("yt-dlp", server.ToolsVersion)), octets));
        app.MapGet("/tools/SHA2-256SUMS", () => Results.Text(
            $"{Sum(ToolBytes("yt-dlp_linux"))}  yt-dlp_linux\n{Sum(ToolBytes("yt-dlp", server.ToolsVersion))}  yt-dlp.exe\n{Sum(ToolBytes("yt-dlp_x86"))}  yt-dlp_x86.exe\n"));
        app.MapGet("/tools/deno.zip", () => Results.Bytes(Served(deno), octets));
        // As PowerShell's Get-FileHash writes it, in capitals.
        app.MapGet("/tools/deno.zip.sha256sum", () => Results.Text($"Algorithm : SHA256\r\nHash      : {Sum(deno).ToUpperInvariant()}\r\nPath      : deno.zip\r\n"));
        app.MapGet("/tools/ffmpeg-master-latest-win64-lgpl.zip", () => Results.Bytes(Served(ffmpeg), octets));
        app.MapGet("/tools/checksums.sha256", () => Results.Text(
            $"{Sum(ToolBytes("other"))}  ffmpeg-master-latest-win64-gpl.zip\n{Sum(ffmpeg)}  ffmpeg-master-latest-win64-lgpl.zip\n"));
    }

    static void MapDash(WebApplication app, TestServer server)
    {
        const string dashXml = "application/dash+xml";
        var payload = server.Payload;
        (int Offset, int Length) Part(int i) => ((int)((long)payload.Length * i / HlsParts), server.HlsPart(i).Length);
        static string Manifest(string body, string length = "PT14S") =>
            $"""<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="{length}">{body}</MPD>""";
        // A template of parts 1 to 7 of what lies at an address, with part 0 as its initialisation section.
        static string Template(string address, string extension = "") =>
            $"""<SegmentTemplate timescale="1" duration="2" initialization="{address}/0{extension}" media="{address}/$Number${extension}" />""";
        static string Video(string segments, string attributes = "") =>
            $"""<AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1000000" {attributes}>{segments}</Representation></AdaptationSet>""";
        var av = Video(Template("../hls/part"))
            + $"""<AdaptationSet mimeType="audio/mp4" lang="en"><Representation id="a" bandwidth="128000">{Template("../hls/av/audio", ".aac")}</Representation></AdaptationSet>""";
        var manifests = new Dictionary<string, Func<string>>
        {
            ["number"] = () => Manifest($"<Period>{Video(Template("../hls/part"))}</Period>"),
            ["time"] = () => Manifest($"""
                <Period>{Video($"<SegmentTemplate timescale=\"100\" initialization=\"../hls/part/0\" media=\"t/$Time$\"><SegmentTimeline><S t=\"100\" d=\"100\" r=\"{HlsParts - 2}\" /></SegmentTimeline></SegmentTemplate>")}</Period>
                """),
            ["list"] = () => Manifest($"""
                <Period>{Video("<SegmentList><Initialization sourceURL=\"../hls/part/0\" />" + string.Concat(Enumerable.Range(1, HlsParts - 1).Select(i => $"<SegmentURL media=\"../hls/part/{i}\" />")) + "</SegmentList>")}</Period>
                """),
            ["list-ranges"] = () => Manifest($"""
                <Period>{Video($"<BaseURL>../hls/whole.bin</BaseURL><SegmentList><Initialization range=\"0-{Part(0).Length - 1}\" />"
                    + string.Concat(Enumerable.Range(1, HlsParts - 1).Select(i => $"<SegmentURL mediaRange=\"{Part(i).Offset}-{Part(i).Offset + Part(i).Length - 1}\" />")) + "</SegmentList>")}</Period>
                """),
            ["segment-base"] = () => Manifest($"""<Period>{Video("<BaseURL>../hls/whole.bin</BaseURL><SegmentBase indexRange=\"100-199\"><Initialization range=\"0-99\" /></SegmentBase>")}</Period>"""),
            ["sized"] = () => Manifest($"""<Period>{Video("<BaseURL>sized.bin</BaseURL>")}</Period>"""),
            ["whole"] = () => Manifest($"""<Period>{Video("<BaseURL>../hls/whole.bin</BaseURL>")}</Period>"""),
            ["whole-noranges"] = () => Manifest($"""<Period>{Video("<BaseURL>../noranges.bin</BaseURL>")}</Period>"""),
            ["qualities"] = () => Manifest($"""
                <Period><AdaptationSet mimeType="video/mp4">{Template("../hls/variant/$RepresentationID$")}
                {string.Concat(HlsBandwidths.Select((b, v) => $"<Representation id=\"{v}\" bandwidth=\"{b}\" height=\"{360 * (v + 1)}\" />"))}
                </AdaptationSet></Period>
                """),
            ["av"] = () => Manifest($"<Period>{av}</Period>"),
            ["webm"] = () => Manifest($"""
                <Period><AdaptationSet mimeType="video/webm"><Representation id="v" bandwidth="1000000">{Template("../hls/part")}</Representation></AdaptationSet></Period>
                """),
            ["webm-av"] = () => Manifest($"""
                <Period><AdaptationSet mimeType="video/webm"><Representation id="v" bandwidth="1000000">{Template("../hls/part")}</Representation></AdaptationSet>
                <AdaptationSet mimeType="audio/webm"><Representation id="a" bandwidth="96000">{Template("../hls/av/audio", ".aac")}</Representation></AdaptationSet></Period>
                """),
            ["ts"] = () => Manifest($"""
                <Period><AdaptationSet mimeType="video/mp2t"><Representation id="v" bandwidth="1000000">{Template("../hls/part")}</Representation></AdaptationSet></Period>
                """),
            ["periods-same-init"] = () => Manifest($"""
                <Period duration="PT6S">{Video(Template("../hls/part"))}</Period>
                <Period>{Video("<SegmentTemplate timescale=\"1\" duration=\"2\" startNumber=\"4\" initialization=\"../hls/part/0\" media=\"../hls/part/$Number$\" />")}</Period>
                """),
            ["periods-own-init"] = () => Manifest($"""
                <Period duration="PT6S">{Video(Template("../hls/part"))}</Period>
                <Period>{Video("<SegmentTemplate timescale=\"1\" duration=\"2\" startNumber=\"5\" initialization=\"../hls/part/4\" media=\"../hls/part/$Number$\" />")}</Period>
                """, "PT12S"),
            ["periods-too-many"] = () => Manifest($"""
                <Period duration="PT{DashManifest.MaxParts}S">{Video("<SegmentTemplate timescale=\"1\" duration=\"1\" media=\"../hls/part/1\" />")}</Period>
                <Period>{Video("<SegmentTemplate timescale=\"1\" duration=\"1\" media=\"../hls/part/1\" />")}</Period>
                """, $"PT{DashManifest.MaxParts + 1}S"),
            ["changing"] = () => Manifest($"""
                <Period>{Video("<SegmentList><Initialization sourceURL=\"../hls/part/0\" />"
                    + string.Concat((server.DashChanged ? Enumerable.Range(1, HlsParts - 1).Reverse() : Enumerable.Range(1, HlsParts - 1)).Select(i => $"<SegmentURL media=\"../hls/part/{i}\" />")) + "</SegmentList>")}</Period>
                """),
            ["tokens"] = () =>
            {
                var token = Interlocked.Increment(ref server._hlsTokens);
                return Manifest($"""
                    <Period>{Video($"<SegmentTemplate timescale=\"1\" duration=\"2\" initialization=\"../hls/part/0?token={token}\" media=\"../hls/part/$Number$?token={token}\" />")}</Period>
                    """);
            },
            ["other-site"] = () => Manifest($"<Period>{Video(Template(server.OtherSite + "/hls/cookies"))}</Period>"),
            ["same-site"] = () => Manifest($"<Period>{Video(Template("../hls/cookies"))}</Period>"),
            ["drm"] = () => Manifest($"""
                <Period><AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc" /><Representation id="v" bandwidth="1">{Template("../hls/part")}</Representation></AdaptationSet></Period>
                """),
            ["live"] = () => $"""<MPD type="dynamic"><Period>{Video(Template("../hls/part"))}</Period></MPD>""",
            ["not-a-manifest"] = () => "<!DOCTYPE html><html><body>Sign in</body></html>",
            ["too-large"] = () => new string(' ', DashManifest.MaxManifestChars + 1),
        };
        foreach (var (name, text) in manifests)
            app.MapGet($"/dash/{name}.mpd", () => Results.Text(text(), dashXml));
        app.MapGet("/dash/moved/number.mpd", () => Results.Redirect("/dash/number.mpd"));
        app.MapGet("/dash/t/{time:long}", (long time) => Results.Bytes(server.HlsPart((int)(time / 100)), "video/mp4"));
        app.MapGet("/dash/sized.bin", (HttpContext context) => server.ServeAsync(context, server.DashChanged ? payload[..^1] : payload, 0));
    }

    static void MapByteRanges(WebApplication app, TestServer server)
    {
        const string mpegUrl = "application/vnd.apple.mpegurl";
        var payload = server.Payload;
        (long Offset, long Length) Part(int i) =>
            ((long)payload.Length * i / HlsParts, (long)payload.Length * (i + 1) / HlsParts - (long)payload.Length * i / HlsParts);
        string Playlist(string file, Func<int, string> range, string header = "") =>
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n" + header
            + string.Concat(Enumerable.Range(0, HlsParts).Select(i => $"#EXTINF:4.0,\n#EXT-X-BYTERANGE:{range(i)}\n{file}\n")) + "#EXT-X-ENDLIST\n";
        string Full(int i) => $"{Part(i).Length}@{Part(i).Offset}";

        app.MapGet("/hls/whole.bin", (HttpContext context) => server.ServeAsync(context, payload, 0));
        app.MapGet("/hls/byterange.m3u8", () => Results.Text(Playlist("whole.bin", i => $"{Part(i).Length}@{Part(i).Offset + server.HlsByteRangeShift}"), mpegUrl));
        app.MapGet("/hls/byterange-following.m3u8", () => Results.Text(Playlist("whole.bin", i => i == 0 ? Full(0) : $"{Part(i).Length}"), mpegUrl));
        app.MapGet("/hls/byterange-malformed.m3u8", () => Results.Text(Playlist("whole.bin", i => $"{Part(i).Length}"), mpegUrl));
        app.MapGet("/hls/byterange-map.m3u8", () =>
        {
            var header = $"#EXT-X-MAP:URI=\"whole.bin\",BYTERANGE=\"{Part(0).Length}@0\"\n";
            return Results.Text("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n" + header
                + string.Concat(Enumerable.Range(1, HlsParts - 1).Select(i => $"#EXTINF:4.0,\n#EXT-X-BYTERANGE:{Full(i)}\nwhole.bin\n")) + "#EXT-X-ENDLIST\n", mpegUrl);
        });

        // Each part encrypted by itself, with its media sequence number as its IV.
        var encrypted = Enumerable.Range(0, HlsParts).Select(i =>
        {
            using var aes = Aes.Create();
            aes.Key = HlsKey;
            var iv = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), i);
            return aes.EncryptCbc(payload.AsSpan((int)Part(i).Offset, (int)Part(i).Length), iv, PaddingMode.PKCS7);
        }).ToList();
        var encryptedWhole = encrypted.SelectMany(part => part).ToArray();
        app.MapGet("/hls/encwhole.bin", (HttpContext context) => server.ServeAsync(context, encryptedWhole, 0));
        app.MapGet("/hls/byterange-aes.m3u8", () => Results.Text(
            Playlist("encwhole.bin", i => $"{encrypted[i].Length}@{encrypted.Take(i).Sum(part => part.Length)}", "#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n"), mpegUrl));

        app.MapGet("/hls/byterange-noranges.m3u8", () => Results.Text(Playlist("../noranges.bin", Full), mpegUrl));

        app.MapGet("/hls/byterange-wrong.m3u8", () => Results.Text(Playlist("wrongwhole.bin", Full), mpegUrl));
        app.MapGet("/hls/wrongwhole.bin", async (HttpContext context) =>
        {
            // Partial content as asked, and a range one byte along from the one asked for.
            var (from, to) = ParseRange(context, payload.Length);
            var length = (int)(to - from + 1);
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {from + 1}-{to + 1}/{payload.Length + 1}";
            context.Response.ContentLength = length;
            await context.Response.Body.WriteAsync(payload.AsMemory((int)from, length));
        });

        app.MapGet("/hls/byterange-short.m3u8", () => Results.Text(Playlist("shortwhole.bin", Full), mpegUrl));
        app.MapGet("/hls/shortwhole.bin", async (HttpContext context) =>
        {
            var (from, to) = ParseRange(context, payload.Length);
            if (server._shortHits.AddOrUpdate(from, 1, (_, n) => n + 1) > 1)
            {
                await server.ServeAsync(context, payload, 0);
                return;
            }
            // A range as asked for in its headers, and only half of it in the body.
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {from}-{to}/{payload.Length}";
            var half = (int)((to - from + 1) / 2);
            context.Response.ContentLength = half;
            await context.Response.Body.WriteAsync(payload.AsMemory((int)from, half));
        });

        app.MapGet("/hls/byterange-subs/master.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"subs.m3u8\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000,SUBTITLES=\"subs\"\n../ts.m3u8\n", mpegUrl));
        app.MapGet("/hls/byterange-subs/subs.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\n#EXT-X-BYTERANGE:100@0\nsubs.vtt\n#EXT-X-ENDLIST\n", mpegUrl));

        // There to be fetched, so that leaving these subtitles out is a choice and not a failed request.
        app.MapGet("/hls/byterange-subs/subs.vtt", (HttpContext context) =>
            server.ServeAsync(context, Encoding.UTF8.GetBytes("WEBVTT\n\n00:01.000 --> 00:02.000\nHello\n".PadRight(100)), 0));

        // A TS part with one packet saying when it starts: a PES header carrying a presentation time.
        app.MapGet("/hls/pts/master.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"../av/subs.m3u8\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000,SUBTITLES=\"subs\"\nvideo.m3u8\n", mpegUrl));
        app.MapGet("/hls/pts/video.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\npart.ts\n#EXT-X-ENDLIST\n", mpegUrl));
        app.MapGet("/hls/pts/part.ts", () => Results.Bytes(TsPacketWithPts(server.HlsPtsStart), "video/mp2t"));
    }

    /// <summary>One TS packet holding the start of a PES packet with this presentation time.</summary>
    public static byte[] TsPacketWithPts(long pts)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x41; // payload unit start, PID 0x100
        packet[3] = 0x10; // payload only
        packet[6] = 0x01; // PES start code, after 00 00
        packet[7] = 0xE0;
        packet[10] = 0x80;
        packet[11] = 0x80; // a PTS follows
        packet[12] = 5;
        packet[13] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        packet[14] = (byte)(pts >> 22);
        packet[15] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        packet[16] = (byte)(pts >> 7);
        packet[17] = (byte)(0x01 | ((pts << 1) & 0xFE));
        return packet;
    }

    static void MapLiveAudio(WebApplication app, TestServer server)
    {
        const string mpegUrl = "application/vnd.apple.mpegurl";
        static string Live(ref int requests, int? end, string uri)
        {
            long first = Interlocked.Increment(ref requests) - 1, last = first + 2;
            if (end is int n && last >= n - 1) (first, last) = (Math.Max(0, n - 3), n - 1);
            var ended = end is int m && last == m - 1;
            return $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:{first}\n"
                + string.Concat(Enumerable.Range((int)first, (int)(last - first + 1)).Select(s => $"#EXTINF:1.0,\n{uri}/{s}\n"))
                + (ended ? "#EXT-X-ENDLIST\n" : "");
        }

        app.MapGet("/hls/liveav/master.m3u8", (int? end) =>
        {
            var query = end is int n ? $"?end={n}" : "";
            return Results.Text($"#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",DEFAULT=YES,URI=\"audio.m3u8{query}\"\n"
                + $"#EXT-X-STREAM-INF:BANDWIDTH=1000000,AUDIO=\"aud\"\nvideo.m3u8{query}\n", mpegUrl);
        });
        app.MapGet("/hls/liveav/video.m3u8", (int? end) => Results.Text(Live(ref server._liveVideoRequests, end, "v"), mpegUrl));
        app.MapGet("/hls/liveav/audio.m3u8", (int? end) => Results.Text(Live(ref server._liveAudioRequests, end, "a"), mpegUrl));
        app.MapGet("/hls/liveav/v/{s:long}", (long s) => Results.Bytes(LivePart(s), "video/mp2t"));
        app.MapGet("/hls/liveav/a/{s:long}", (HttpContext context, long s) =>
            server.LiveAudioFailFrom >= 0 && s >= server.LiveAudioFailFrom
                ? Results.StatusCode(StatusCodes.Status500InternalServerError)
                : Results.Bytes(LiveAudioPart(s), "audio/aac"));
    }

    static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    public async ValueTask DisposeAsync()
    {
        // A stalled request never ends by itself; past this, Kestrel drops whatever is still open.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }
}
