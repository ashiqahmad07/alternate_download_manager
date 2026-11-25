using System.Collections.Concurrent;
using System.IO.Compression;
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
/// A web site a test writes and changes. Addresses are kept with their hosts, and it doubles as a proxy, so
/// through <see cref="Proxy"/> a test can use names such as www.site.test and other.test.
/// </summary>
sealed class SiteTestServer : IAsyncDisposable
{
    public sealed record Asked(string Host, string PathAndQuery, string Range, string Cookie, string Authorization, string Referer, string IfNoneMatch, string IfModifiedSince);

    public sealed class Resource
    {
        public byte[] Body = Array.Empty<byte>();
        public string? ContentType;
        public string? ETag;
        public DateTimeOffset? LastModified;
        /// <summary>A 3xx to this address, in place of the body.</summary>
        public string? RedirectTo;
        /// <summary>Answers with this status, in place of the body.</summary>
        public int? Status;
        /// <summary>Answers 500 this many times first.</summary>
        public int FailTimes;
        /// <summary>The body is sent gzipped, whatever the request accepts.</summary>
        public bool Gzip;
        /// <summary>This user:pass is asked for, through HTTP Basic.</summary>
        public string? Login;
        public string? Disposition;
        /// <summary>Each request is sent at no more than this.</summary>
        public int KBps;
        /// <summary>Each answer sends its first 16 KB, then waits for this before the rest, however slow the machine.</summary>
        public Task? RestAfter;
        /// <summary>Answers 404 once it has answered this many times; null never does.</summary>
        public int? GoneAfter;
    }

    readonly WebApplication _app;
    readonly ConcurrentDictionary<string, Resource> _site = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<Asked> _asked = new();
    int _active;
    int _maxActive;
    bool _down;
    bool _alwaysPage;

    SiteTestServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";
    public int Port => new Uri(BaseUrl).Port;
    public ProxySettings Proxy => new() { Mode = ProxyMode.Manual, Host = "127.0.0.1", Port = Port, Bypass = "" };
    public IReadOnlyCollection<Asked> Requests => _asked;
    public int MaxConcurrent => Volatile.Read(ref _maxActive);

    /// <summary>How long each request is held, so concurrent ones overlap.</summary>
    public TimeSpan Hold { get; set; }

    int _holdAfter;
    Task? _holdUntil;

    /// <summary>
    /// Requests after the first <paramref name="answered"/> wait for <paramref name="until"/>, or for the client to go,
    /// so a test can act partway through a crawl however slow the machine.
    /// </summary>
    public void HoldAfter(int answered, Task until)
    {
        _holdAfter = answered;
        Volatile.Write(ref _holdUntil, until);
    }

    /// <summary>While true, every request's connection is dropped without an answer, as in a network outage.</summary>
    public bool Down { get => Volatile.Read(ref _down); set => Volatile.Write(ref _down, value); }

    /// <summary>Any address with no resource gets a page that links to two more, without end.</summary>
    public bool Endless { get => Volatile.Read(ref _alwaysPage); set => Volatile.Write(ref _alwaysPage, value); }

    /// <param name="address">"/path" on any host, or "http://host/path" on that host only.</param>
    public Resource Put(string address, byte[] body, string? contentType)
    {
        var resource = new Resource { Body = body, ContentType = contentType };
        _site[Key(address)] = resource;
        return resource;
    }

    public Resource Page(string address, string html) => Put(address, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");

    public Resource File(string address, int size = 1000, string? contentType = "application/octet-stream")
    {
        var body = new byte[size];
        new Random(size).NextBytes(body);
        return Put(address, body, contentType);
    }

    public Resource Redirect(string address, string target)
    {
        var resource = Put(address, Array.Empty<byte>(), null);
        resource.RedirectTo = target;
        return resource;
    }

    public Resource Status(string address, int status)
    {
        var resource = Put(address, Array.Empty<byte>(), null);
        resource.Status = status;
        return resource;
    }

    public void Remove(string address) => _site.TryRemove(Key(address), out _);

    /// <summary>What an address holds.</summary>
    public byte[] Body(string address) => _site[Key(address)].Body;

    /// <summary>The requests for a path, on any host.</summary>
    public List<Asked> AskedFor(string pathAndQuery) => _asked.Where(a => a.PathAndQuery == pathAndQuery).ToList();

    public void ClearRequests() => _asked.Clear();

    static string Key(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http") ? uri.Host + "|" + uri.PathAndQuery : "|" + address;

    public static async Task<SiteTestServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new SiteTestServer(app);
        app.Run(server.AnswerAsync);
        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return server;
    }

    async Task AnswerAsync(HttpContext context)
    {
        var active = Interlocked.Increment(ref _active);
        var most = Volatile.Read(ref _maxActive);
        while (active > most && Interlocked.CompareExchange(ref _maxActive, active, most) != most) most = Volatile.Read(ref _maxActive);
        try
        {
            var request = context.Request;
            // As it was sent, escapes and all, and without the host a request to a proxy has.
            var target = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
            var path = target.StartsWith('/') ? target : new Uri(target).PathAndQuery;
            _asked.Enqueue(new Asked(request.Host.Host, path, request.Headers.Range.ToString(), request.Headers.Cookie.ToString(),
                request.Headers.Authorization.ToString(), request.Headers.Referer.ToString(), request.Headers.IfNoneMatch.ToString(), request.Headers.IfModifiedSince.ToString()));
            if (Hold > TimeSpan.Zero) await Task.Delay(Hold);
            if (Volatile.Read(ref _holdUntil) is { IsCompleted: false } until && _asked.Count > _holdAfter)
            {
                try
                {
                    await until.WaitAsync(context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            if (Down)
            {
                context.Abort();
                return;
            }

            var resource = _site.GetValueOrDefault(request.Host.Host + "|" + path) ?? _site.GetValueOrDefault("|" + path);
            if (resource == null)
            {
                if (Endless) await WriteEndlessAsync(context, path);
                else context.Response.StatusCode = 404;
                return;
            }
            await WriteAsync(context, resource);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    static async Task WriteEndlessAsync(HttpContext context, string path)
    {
        var name = path.TrimEnd('/').Split('/').Last().Replace(".html", "");
        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync($"<a href='/{name}a.html'>a</a> <a href='/{name}b.html'>b</a>");
    }

    static async Task WriteAsync(HttpContext context, Resource resource)
    {
        var (request, response) = (context.Request, context.Response);
        if (resource.Login != null)
        {
            var wanted = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(resource.Login));
            if (request.Headers.Authorization != wanted)
            {
                response.StatusCode = 401;
                response.Headers.WWWAuthenticate = "Basic realm=\"site\"";
                return;
            }
        }
        if (resource.GoneAfter is int left)
        {
            if (left <= 0)
            {
                response.StatusCode = 404;
                return;
            }
            resource.GoneAfter = left - 1;
        }
        if (resource.FailTimes > 0)
        {
            resource.FailTimes--;
            response.StatusCode = 500;
            return;
        }
        if (resource.Status is int status)
        {
            response.StatusCode = status;
            return;
        }
        if (resource.RedirectTo != null)
        {
            response.StatusCode = 302;
            response.Headers.Location = resource.RedirectTo;
            return;
        }

        if (resource.ContentType != null) response.ContentType = resource.ContentType;
        if (resource.Disposition != null) response.Headers.ContentDisposition = resource.Disposition;
        if (resource.ETag != null) response.Headers.ETag = resource.ETag;
        if (resource.LastModified is DateTimeOffset modified) response.Headers.LastModified = modified.ToString("R");
        if ((resource.ETag != null && request.Headers.IfNoneMatch == resource.ETag)
            || (resource.ETag == null && resource.LastModified is DateTimeOffset at && request.Headers.IfModifiedSince == at.ToString("R")))
        {
            response.StatusCode = 304;
            return;
        }

        if (resource.Gzip)
        {
            response.Headers.ContentEncoding = "gzip";
            using var packed = new MemoryStream();
            using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(resource.Body);
            response.ContentLength = packed.Length;
            await response.Body.WriteAsync(packed.ToArray());
            return;
        }

        var body = resource.Body;
        response.Headers.AcceptRanges = "bytes";
        var range = request.Headers.Range.ToString();
        if (range.StartsWith("bytes=") && body.Length > 0)
        {
            var parts = range["bytes=".Length..].Split('-');
            var start = long.Parse(parts[0]);
            var end = parts[1].Length > 0 ? Math.Min(long.Parse(parts[1]), body.Length - 1) : body.Length - 1;
            response.StatusCode = 206;
            response.Headers.ContentRange = $"bytes {start}-{end}/{body.Length}";
            response.ContentLength = end - start + 1;
            await SendAsync(context, body.AsMemory((int)start, (int)(end - start + 1)), resource.KBps, resource.RestAfter);
            return;
        }
        response.ContentLength = body.Length;
        await SendAsync(context, body, resource.KBps, resource.RestAfter);
    }

    static async Task SendAsync(HttpContext context, ReadOnlyMemory<byte> bytes, int kbps, Task? restAfter)
    {
        if (kbps <= 0 && restAfter == null)
        {
            await context.Response.Body.WriteAsync(bytes);
            return;
        }
        const int chunk = 16 * 1024;
        try
        {
            for (int at = 0; at < bytes.Length; at += chunk)
            {
                await context.Response.Body.WriteAsync(bytes.Slice(at, Math.Min(chunk, bytes.Length - at)), context.RequestAborted);
                if (at == 0 && restAfter != null)
                {
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await restAfter.WaitAsync(context.RequestAborted);
                }
                if (kbps > 0) await Task.Delay(chunk * 1000 / (kbps * 1024), context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The client has gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }
}
