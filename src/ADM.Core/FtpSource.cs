using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ADM.Core;

/// <summary>
/// FTP, in passive mode: each connection of a run logs in on its own control connection and fetches its
/// range with REST and RETR, so the speed and resume work of <see cref="HttpDownloader"/> carries over.
/// </summary>
internal sealed class FtpSource : ISegmentSource
{
    readonly DownloadItem _item;
    readonly Settings _settings;
    readonly ProxySettings _proxy;
    readonly Uri _address;
    bool? _utf8;

    public FtpSource(DownloadItem item, Settings settings, ProxySettings proxy, Uri address)
    {
        _item = item;
        _settings = settings;
        _proxy = proxy;
        _address = address;
    }

    public string Address => _item.Url;

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        return await DescribeAsync(client, ct);
    }

    public async Task<(ProbeResult Probe, SegmentBody First)> OpenAsync(CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        try
        {
            var probe = await DescribeAsync(client, ct);
            // The connection that answered carries on as the first segment, so none is thrown away.
            return (probe, new SegmentBody(await client.RetrieveAsync(0, ct), client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<SegmentBody> OpenRangeAsync(long from, long to, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        try
        {
            return new SegmentBody(await client.RetrieveAsync(from, ct), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    async Task<ProbeResult> DescribeAsync(FtpClient client, CancellationToken ct)
    {
        long total = await client.SizeAsync(ct);
        var modified = await client.ModifiedAsync(ct);
        // REST 0 is how FTP says it can start anywhere; without a size there is no telling where the end is.
        bool ranged = await client.TryRestAsync(0, ct) && total > 0;
        return new ProbeResult(
            total,
            ranged,
            FileNames.FromResponse(null, _item.Url, null),
            ETag: null,
            LastModified: modified?.ToString("R", CultureInfo.InvariantCulture),
            MediaType: null);
    }

    async Task<FtpClient> ConnectAsync(CancellationToken ct)
    {
        var client = await FtpClient.ConnectAsync(_address, Login(), await RouteAsync(ct), _utf8, ct);
        _utf8 = client.Utf8;
        return client;
    }

    /// <summary>The download's own login, else a Site Login for this server, else one in the address; anonymous if none.</summary>
    NetworkCredential? Login()
    {
        if (!string.IsNullOrEmpty(_item.UserName))
            return new NetworkCredential(_item.UserName, Secrets.Unprotect(_item.ProtectedPassword) ?? "");
        var site = _settings.SiteLogins.FirstOrDefault(s => SiteList.Matches(s.Sites, _item.Url));
        if (site != null)
            return new NetworkCredential(site.UserName, Secrets.Unprotect(site.ProtectedPassword) ?? "");
        if (!string.IsNullOrEmpty(_address.UserInfo))
        {
            // Addresses are stripped of their login when added; this one came some other way.
            var parts = _address.UserInfo.Split(':', 2);
            return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return null;
    }

    /// <summary>Direct, or through a SOCKS5 proxy; no other proxy can carry FTP, and the download never goes around one.</summary>
    async Task<FtpRoute?> RouteAsync(CancellationToken ct)
    {
        if (_proxy.Mode is ProxyMode.Script or ProxyMode.AutoDetect)
        {
            if (await Net.ScriptFor(_proxy).ResolveAsync(_address, ct) is not { } named) return null;
            if (named.Scheme != "socks5") throw new ProxyException("The proxy script sends this server through a proxy that can't carry FTP.");
            return new FtpRoute(named.IdnHost, named.Port, null);
        }
        if (_proxy.Mode != ProxyMode.Manual || SiteList.Matches(_proxy.Bypass, _item.Url)) return null;
        if (_proxy.Kind != ProxyKind.Socks5)
            throw new ProxyException("FTP can't go through an HTTP proxy. Add the server to the proxy's exceptions, or use a SOCKS proxy.");
        return new FtpRoute(
            _proxy.Host,
            _proxy.Port,
            string.IsNullOrEmpty(_proxy.UserName) ? null : new NetworkCredential(_proxy.UserName, Secrets.Unprotect(_proxy.ProtectedPassword) ?? ""));
    }

    // Every FTP connection is closed after its piece, and a server counts its session until it has noticed:
    // a transfer in progress is only found out at the server's next write.
    public TimeSpan CloseSettleTime => TimeSpan.FromMilliseconds(150);

    /// <summary>A server with too many connections: 421, or the connection refused.</summary>
    public bool IsRefusal(Exception e) =>
        e is FtpException { Code: 421 }
        || e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
        || e is IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } };

    public bool IsConnectionFailure(Exception e, CancellationToken ct) =>
        Net.IsConnectionFailure(e, ct)
        || e is FtpConnectionException or SocketException
        || e is FtpException { Code: 421 or 425 or 426 };

    public bool IsTransient(Exception e, CancellationToken ct) =>
        Net.IsTransient(e, ct)
        || e is SocketException
        || e is FtpException { Code: 421 or 425 or 426 or 450 or 451 or 452 };

    public void Dispose()
    {
    }
}
