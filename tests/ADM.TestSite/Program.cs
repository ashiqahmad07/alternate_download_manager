using System.IO.Compression;
using System.Net;
using System.Text;

// A local site for trying ADM by hand: a page of links, and files slow enough to act on while they run.
//   dotnet run --project tests\ADM.TestSite [port]
// It listens on this computer only.

const int SlowKBps = 256;

var port = args.Length > 0 ? int.Parse(args[0]) : 8765;
var address = $"http://127.0.0.1:{port}/";
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();

var small = new byte[1024 * 1024];
new Random(1).NextBytes(small);
var archive = BuildArchive(small);
var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

app.MapGet("/", () => Results.Content(Page(), "text/html; charset=utf-8"));
app.MapGet("/other.html", () => Results.Content("<!doctype html><title>Another page</title><p>A web page, not a file.</p>", "text/html"));
app.MapMethods("/small.bin", new[] { "GET", "HEAD" }, () => Results.Bytes(small, "application/octet-stream", lastModified: modified, enableRangeProcessing: true));
app.MapMethods("/archive.zip", new[] { "GET", "HEAD" }, () => Results.Bytes(archive, "application/zip", lastModified: modified, enableRangeProcessing: true));
app.MapMethods("/slow.bin", new[] { "GET", "HEAD" }, ctx => SendSlow(ctx, 128L * 1024 * 1024, resumable: true));
app.MapMethods("/noresume.bin", new[] { "GET", "HEAD" }, ctx => SendSlow(ctx, 32L * 1024 * 1024, resumable: false));

// A small site for the site grabber: pages two deep, a stylesheet with its own picture, pictures, and a file behind a redirect.
var picture = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
var report = Encoding.UTF8.GetBytes("%PDF-1.4\n% A stand-in for a report, for the site grabber's checks.\n");
app.MapGet("/site/", () => Results.Content(SitePage("Home",
    """<link rel="stylesheet" href="style.css"><p><img src="pictures/one.png" alt="one"> <a href="about.html">About</a> · <a href="gallery/">Gallery</a> · <a href="get/report">The report</a></p>"""), "text/html; charset=utf-8"));
app.MapGet("/site/about.html", () => Results.Content(SitePage("About",
    """<link rel="stylesheet" href="style.css"><p><a href="./">Home</a> · <a href="gallery/">Gallery</a></p>"""), "text/html; charset=utf-8"));
app.MapGet("/site/gallery/", () => Results.Content(SitePage("Gallery",
    """<link rel="stylesheet" href="../style.css"><p><img src="../pictures/one.png" alt="one"> <img src="../pictures/two.png" alt="two"> <a href="../">Home</a></p>"""), "text/html; charset=utf-8"));
app.MapGet("/site/style.css", () => Results.Content("body { background: url(pictures/back.png); font: 16px system-ui }", "text/css"));
foreach (var name in new[] { "one", "two", "back" })
    app.MapMethods($"/site/pictures/{name}.png", new[] { "GET", "HEAD" }, () => Results.Bytes(picture, "image/png", lastModified: modified, enableRangeProcessing: true));
app.MapGet("/site/get/report", () => Results.Redirect("/site/files/report.pdf"));
app.MapMethods("/site/files/report.pdf", new[] { "GET", "HEAD" }, () => Results.Bytes(report, "application/pdf", lastModified: modified, enableRangeProcessing: true));

// A DASH stream: two qualities of video and a separate audio track, five parts each. A part's bytes name it,
// so a saved file shows which quality it holds. The page fetches the manifest as a player would, so the
// extension sees it, and has a player for the panel to go over; nothing on it plays.
string[] dashTracks = { "720p", "360p", "audio" };
string[] dashParts = { "init.mp4", "1.m4s", "2.m4s", "3.m4s", "4.m4s", "5.m4s" };
app.MapGet("/dash/", () => Results.Content(DashPage(), "text/html; charset=utf-8"));
app.MapGet("/dash/stream.mpd", () => Results.Text(DashManifest(), "application/dash+xml"));
app.MapGet("/dash/{track}/{part}", (string track, string part) =>
    dashTracks.Contains(track) && dashParts.Contains(part)
        ? Results.Bytes(DashPart(track, part), track == "audio" ? "audio/mp4" : "video/mp4")
        : Results.NotFound());

Console.WriteLine($"Open {address} in the browser. Press Ctrl+C to stop.");
app.Run(address);

static byte[] BuildArchive(byte[] content)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        AddEntry(zip, "readme.txt", Encoding.UTF8.GetBytes("A small archive for ADM's checks."));
        AddEntry(zip, "docs/notes.txt", Encoding.UTF8.GetBytes("A file in a folder."));
        AddEntry(zip, "Größe 日本語.txt", Encoding.UTF8.GetBytes("A file with a non-ASCII name."));
        AddEntry(zip, "big.bin", content);
    }
    return stream.ToArray();
}

static void AddEntry(ZipArchive zip, string name, byte[] bytes)
{
    using var entry = zip.CreateEntry(name).Open();
    entry.Write(bytes);
}

// Each connection gets its own SlowKBps, so eight are eight times as fast as one.
static async Task SendSlow(HttpContext context, long size, bool resumable)
{
    var (request, response) = (context.Request, context.Response);
    response.ContentType = "application/octet-stream";
    var (from, to) = (0L, size - 1);
    if (resumable)
    {
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ETag = "\"adm-check\"";
        if (request.GetTypedHeaders().Range is { Ranges.Count: 1 } range && range.Ranges.First() is var wanted)
        {
            from = wanted.From ?? size - wanted.To!.Value;
            to = wanted.From == null ? size - 1 : Math.Min(wanted.To ?? size - 1, size - 1);
            if (from >= size || from > to)
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{size}";
                return;
            }
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.Headers.ContentRange = $"bytes {from}-{to}/{size}";
        }
        response.ContentLength = to - from + 1;
    }
    if (HttpMethods.IsHead(request.Method)) return;

    var chunk = new byte[SlowKBps * 1024 / 16];
    try
    {
        for (var left = to - from + 1; left > 0; left -= chunk.Length)
        {
            await response.Body.WriteAsync(chunk.AsMemory(0, (int)Math.Min(left, chunk.Length)), context.RequestAborted);
            await Task.Delay(1000 / 16, context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // The download was paused or stopped.
    }
}

static string Page() => """
    <!doctype html>
    <meta charset="utf-8">
    <title>ADM checks</title>
    <style>body { font: 16px system-ui; margin: 2em; max-width: 44em } li { margin: .4em 0 }</style>
    <h1>ADM checks</h1>
    <ul>
      <li><a href="/archive.zip">archive.zip</a>: a small real ZIP, with a folder and a non-ASCII name.</li>
      <li><a href="/small.bin">small.bin</a>: 1 MB, finishes at once.</li>
      <li><a href="/slow.bin">slow.bin</a>: 128 MB at 256 KB/s a connection, and it can resume.</li>
      <li><a href="/noresume.bin">noresume.bin</a>: 32 MB at 256 KB/s, and it can't resume.</li>
      <li><a href="/other.html">other.html</a>: a link that opens a web page, not a file.</li>
      <li><a href="/site/">site</a>: a small site for the site grabber.</li>
      <li><a href="/dash/">dash</a>: a DASH stream in 720p and 360p, with separate audio, for the download panel.
        Its manifest is <a href="/dash/stream.mpd">stream.mpd</a>.</li>
    </ul>
    """;

static string DashPage() => """
    <!doctype html>
    <meta charset="utf-8">
    <title>A DASH stream</title>
    <style>body { font: 16px system-ui; margin: 2em } video { background: #222 }</style>
    <h1>A DASH stream</h1>
    <p>The page has loaded the stream's manifest, as a player does. ADM's panel should offer 720p, 360p and Audio only over the player.</p>
    <video width="640" height="360" controls></video>
    <script>fetch("stream.mpd");</script>
    """;

static string DashManifest() => """
    <?xml version="1.0" encoding="UTF-8"?>
    <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S" minBufferTime="PT2S" profiles="urn:mpeg:dash:profile:isoff-live:2011">
      <Period>
        <AdaptationSet contentType="video" mimeType="video/mp4">
          <SegmentTemplate timescale="1" duration="2" startNumber="1" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" />
          <Representation id="720p" bandwidth="1500000" width="1280" height="720" codecs="avc1.4d401f" />
          <Representation id="360p" bandwidth="500000" width="640" height="360" codecs="avc1.4d401e" />
        </AdaptationSet>
        <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
          <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main" />
          <SegmentTemplate timescale="1" duration="2" startNumber="1" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" />
          <Representation id="audio" bandwidth="128000" codecs="mp4a.40.2" />
        </AdaptationSet>
      </Period>
    </MPD>
    """;

/// <summary>256 KB of the part's own name, over and over: "720p/3.m4s 720p/3.m4s …".</summary>
static byte[] DashPart(string track, string part)
{
    var name = Encoding.ASCII.GetBytes($"{track}/{part} ");
    var bytes = new byte[256 * 1024];
    for (int i = 0; i < bytes.Length; i++) bytes[i] = name[i % name.Length];
    return bytes;
}

static string SitePage(string title, string body) => $"<!doctype html><meta charset=\"utf-8\"><title>{title}</title><h1>{title}</h1>{body}";
