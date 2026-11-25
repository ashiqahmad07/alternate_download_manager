using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ADM.Core;

/// <summary>
/// Hands WinHTTP a proxy script from a loopback address, as the script's own server sends it with a few lines
/// added at the end. Those lines pass each answer of the script's through <c>fix</c>, which writes it in a form
/// Windows reads as the script meant it. Windows 11 build 26300 and Windows Server 2022 leave out a "SOCKS4" or
/// "SOCKS5" entry, so "SOCKS5 a:1; PROXY b:2" came back as the second entry, and "SOCKS5 a:1" as none. Nor do
/// they read a keyword that isn't in capitals, which browsers do. A SOCKS4 proxy's host comes back with
/// <see cref="Socks4Mark"/> after it, for <see cref="PacProxy"/> to take off again.
/// </summary>
/// <remarks>
/// The script is fetched again each time WinHTTP asks for it, directly, as WinHTTP fetches one. Its server's
/// error comes back as the same status, and no answer as 502, so WinHTTP fails the script as it did before. Each
/// script has an address of its own under one listener, and a path no other program can guess.
/// </remarks>
internal static class ScriptRelay
{
    /// <summary>After a SOCKS4 proxy's host. A name under <c>.invalid</c> never exists, so it is never a real host.</summary>
    public const string Socks4Mark = ".socks4.adm.invalid";

    const int MaxScriptBytes = 1024 * 1024;
    const int MaxRequestBytes = 16 * 1024;
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    static readonly ConcurrentDictionary<string, Uri> Scripts = new();
    static readonly Lazy<int> Port = new(Listen);
    static readonly HttpClient Fetcher = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
    {
        Timeout = FetchTimeout,
        MaxResponseContentBufferSize = MaxScriptBytes,
    };

    /// <summary>
    /// Added after the script. ES3, as WinHTTP's engine may be older than ES5. The function keeps its name, so
    /// a script that calls FindProxyForURL itself gets the rewritten answer too, which reads the same.
    /// </summary>
    internal const string Wrapper = """

        ;(function () {
          function fix(said) {
            if (typeof said != "string") return said;
            var entries = said.split(";"), out = [];
            for (var i = 0; i < entries.length; i++) {
              var m = /^\s*(\S+)(?:\s+(.*?))?\s*$/.exec(entries[i]);
              if (!m) continue;
              var kind = m[1].toUpperCase(), where = m[2] || "";
              if (kind == "SOCKS5") kind = "SOCKS";
              else if (kind == "SOCKS4") { kind = "SOCKS"; where = where.replace(/^([^:\[\]\s]+)/, "$1.socks4.adm.invalid"); }
              out.push(where ? kind + " " + where : kind);
            }
            return out.join("; ");
          }
          if (typeof FindProxyForURL == "function") {
            var find = FindProxyForURL;
            FindProxyForURL = function (url, host) { return fix(find(url, host)); };
          }
          if (typeof FindProxyForURLEx == "function") {
            var findEx = FindProxyForURLEx;
            FindProxyForURLEx = function (url, host) { return fix(findEx(url, host)); };
          }
        })();

        """;

    /// <summary>
    /// The address WinHTTP is to fetch <paramref name="script"/> from. One that isn't an http or https address
    /// is given back as it is, for WinHTTP to fail as before.
    /// </summary>
    public static string AddressFor(string script)
    {
        if (!Uri.TryCreate(script, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return script;
        int port;
        try
        {
            port = Port.Value;
        }
        catch (SocketException)
        {
            // No loopback to listen on: WinHTTP fetches the script itself, and reads what it can of it.
            return script;
        }
        var path = $"/{Guid.NewGuid():N}.pac";
        Scripts[path] = uri;
        return $"http://127.0.0.1:{port}{path}";
    }

    static int Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptAsync(listener);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Runs as long as the program does.</summary>
    static async Task AcceptAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (SocketException)
            {
                // A caller that went away before it was accepted.
                continue;
            }
            _ = ServeAsync(client);
        }
    }

    static async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var limit = new CancellationTokenSource(FetchTimeout);
                var stream = client.GetStream();
                var path = await ReadPathAsync(stream, limit.Token);
                var (status, body) = path != null && Scripts.TryGetValue(path, out var script)
                    ? await FetchAsync(script, limit.Token)
                    : (404, Array.Empty<byte>());
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                           "Content-Type: application/x-ns-proxy-autoconfig\r\n" +
                           $"Content-Length: {body.Length}\r\n" +
                           "Cache-Control: no-store\r\n" +
                           "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), limit.Token);
                await stream.WriteAsync(body, limit.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // WinHTTP went away, or took too long; it fails the script itself.
            }
        }
    }

    /// <summary>The path a GET asks for, without its query; null for anything else.</summary>
    static async Task<string?> ReadPathAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        int length = 0;
        while (length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
        }
        int lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
        if (lineEnd < 0) return null;
        var parts = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');
        return parts is ["GET", var target, _] ? target.Split('?')[0] : null;
    }

    static async Task<(int Status, byte[] Body)> FetchAsync(Uri script, CancellationToken ct)
    {
        try
        {
            using var response = await Fetcher.GetAsync(script, ct);
            if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, Array.Empty<byte>());
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return (200, [.. bytes, .. Encoding.ASCII.GetBytes(Wrapper)]);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            return (502, Array.Empty<byte>());
        }
    }
}
