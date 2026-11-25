using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace ADM.Core;

/// <summary>The bytes of one range, as they arrive; disposing it ends the request and its connection.</summary>
internal sealed class SegmentBody(Stream stream, IDisposable owner) : IDisposable
{
    public Stream Stream => stream;

    public void Dispose()
    {
        stream.Dispose();
        owner.Dispose();
    }
}

/// <summary>A server that said it could send byte ranges has stopped doing so.</summary>
internal sealed class RangesRefusedException() : InvalidOperationException("The server stopped honouring byte ranges. Delete and re-add the download.");

/// <summary>
/// Where a download's bytes come from, one source per run. <see cref="HttpDownloader"/> keeps what both
/// protocols share (segments, splitting by speed, takeovers, checkpoints, reconnecting); a source does the
/// asking, and says which of its errors are worth another try.
/// </summary>
internal interface ISegmentSource : IDisposable
{
    /// <summary>Where this run's connections ask now; for HTTP, the address the redirects ended on.</summary>
    string Address { get; }

    /// <summary>Asks about the file without downloading it.</summary>
    Task<ProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// A fresh download's first request. It asks for everything, so the connection that answers goes on to
    /// download the first segment instead of being thrown away.
    /// </summary>
    Task<(ProbeResult Probe, SegmentBody First)> OpenAsync(CancellationToken ct);

    /// <summary>The bytes from <paramref name="from"/> to <paramref name="to"/> (<see cref="long.MaxValue"/>: the end of the file).</summary>
    Task<SegmentBody> OpenRangeAsync(long from, long to, CancellationToken ct);

    /// <summary>
    /// How long a server goes on counting a connection after it is closed, which a worker waits out before
    /// opening its next one so that it isn't turned away by a limit it is not over. Zero where closing is
    /// seen at once.
    /// </summary>
    TimeSpan CloseSettleTime { get; }

    /// <summary>A server turning a connection away because it has too many.</summary>
    bool IsRefusal(Exception e);

    /// <summary>A try that got no answer at all, or lost its connection partway through one.</summary>
    bool IsConnectionFailure(Exception e, CancellationToken ct);

    /// <summary>Worth another try.</summary>
    bool IsTransient(Exception e, CancellationToken ct);
}

internal static class SegmentSources
{
    /// <summary>
    /// The source for <paramref name="item"/>'s address, using the HTTP client and the proxy the download
    /// started with: a proxy change reaches only the downloads that start after it.
    /// </summary>
    public static ISegmentSource For(DownloadItem item, Settings settings, HttpClient client, ProxySettings proxy) =>
        Uri.TryCreate(item.Url, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeFtp
            ? new FtpSource(item, settings, proxy, address)
            : new HttpSource(item, settings, client);
}

/// <summary>HTTP and HTTPS: requests, redirects, the refresh of an expired address, and the 206 check.</summary>
internal sealed class HttpSource : ISegmentSource
{
    readonly DownloadItem _item;
    readonly Settings _settings;
    readonly HttpClient _client;
    readonly SemaphoreSlim _refresh = new(1, 1);
    string _url;

    public HttpSource(DownloadItem item, Settings settings, HttpClient client)
    {
        _item = item;
        _settings = settings;
        _client = client;
        _url = item.Url;
    }

    /// <summary>Where the probe's redirects ended; segments ask it directly. Not saved, because signed addresses expire.</summary>
    public string Address => Volatile.Read(ref _url);

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        var probe = await DescribeAsync(ct);
        Volatile.Write(ref _url, probe.FinalUrl ?? _item.Url);
        return probe;
    }

    async Task<ProbeResult> DescribeAsync(CancellationToken ct)
    {
        using var req = Net.NewRequest(_item, _item.Url, _settings.UserAgent);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await Net.SendAsync(_client, _settings, _item, req, ct);
        Net.EnsureSuccess(resp);
        return Describe(resp);
    }

    public async Task<(ProbeResult Probe, SegmentBody First)> OpenAsync(CancellationToken ct)
    {
        var req = Net.NewRequest(_item, _item.Url, _settings.UserAgent);
        req.Headers.Range = new RangeHeaderValue(0, null);
        var resp = await Net.SendAsync(_client, _settings, _item, req, ct);
        try
        {
            Net.EnsureSuccess(resp);
            var probe = Describe(resp);
            var first = new SegmentBody(await resp.Content.ReadAsStreamAsync(ct), resp);
            Volatile.Write(ref _url, probe.FinalUrl ?? _item.Url);
            return (probe, first);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    ProbeResult Describe(HttpResponseMessage resp)
    {
        long total = -1;
        bool ranged = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is long length)
        {
            total = length;
            ranged = true;
        }
        else if (resp.Content.Headers.ContentLength is long contentLength)
        {
            total = contentLength;
        }

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? _item.Url;
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        return new ProbeResult(
            total,
            ranged,
            FileNames.FromResponse(resp.Content.Headers.ContentDisposition, finalUrl, mediaType),
            resp.Headers.ETag?.Tag,
            resp.Content.Headers.LastModified?.ToString("R"),
            mediaType,
            finalUrl);
    }

    public async Task<SegmentBody> OpenRangeAsync(long from, long to, CancellationToken ct)
    {
        var resp = await RequestRangeAsync(from, to, ct);
        try
        {
            Net.EnsureSuccess(resp);
            if (_item.ResumeSupported && resp.StatusCode != HttpStatusCode.PartialContent)
                throw new RangesRefusedException();
            return new SegmentBody(await resp.Content.ReadAsStreamAsync(ct), resp);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the address this run's redirects ended on for a byte range. When that address has expired, as
    /// signed addresses do, the original address is asked once where to go now.
    /// </summary>
    async Task<HttpResponseMessage> RequestRangeAsync(long from, long to, CancellationToken ct)
    {
        var url = Address;
        var resp = await SendRangeAsync(url, from, to, ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone && url != _item.Url)
        {
            resp.Dispose();
            await RefreshUrlAsync(url, async () => (await DescribeAsync(ct)).FinalUrl ?? _item.Url, ct);
            resp = await SendRangeAsync(Address, from, to, ct);
        }
        return resp;
    }

    Task<HttpResponseMessage> SendRangeAsync(string url, long from, long to, CancellationToken ct)
    {
        // The address the redirects ended on may be another site, which gets none of the browser's cookies.
        var req = Net.NewRequest(_item, url, _settings.UserAgent, cookies: Uri.TryCreate(url, UriKind.Absolute, out var target) && Net.SameOrigin(target, _item.Url));
        if (_item.ResumeSupported) req.Headers.Range = new RangeHeaderValue(from, to == long.MaxValue ? null : to);
        return Net.SendAsync(_client, _settings, _item, req, ct);
    }

    /// <summary>Learns a fresh address, unless another connection has already replaced <paramref name="expired"/>.</summary>
    async Task RefreshUrlAsync(string expired, Func<Task<string>> probe, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            if (Address == expired) Volatile.Write(ref _url, await probe());
        }
        finally
        {
            _refresh.Release();
        }
    }

    // A response that ran to its end leaves its connection to the pool; only a cut-short one is closed, and the downloader waits for that.
    public TimeSpan CloseSettleTime => TimeSpan.Zero;

    /// <summary>A server turning a connection away: 503 or 429, or refused at connect.</summary>
    public bool IsRefusal(Exception e) => e is HttpRequestException h
        && (h.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
            || h.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused });

    public bool IsConnectionFailure(Exception e, CancellationToken ct) => Net.IsConnectionFailure(e, ct);

    public bool IsTransient(Exception e, CancellationToken ct) => Net.IsTransient(e, ct);

    public void Dispose() => _refresh.Dispose();
}
