using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ADM.Core;

// Browser native-messaging host. The browser starts this process and exchanges messages
// framed as a 32-bit little-endian length followed by UTF-8 JSON on stdin/stdout. Each
// message is relayed to the ADM app over its local pipe, starting the app if needed.

// "Download all links" can carry thousands of links; the extension keeps under 6 MB.
const int MaxMessageBytes = 8 * 1024 * 1024;

// Its own file, as this runs beside the app. Nothing new goes to standard output, which belongs to the browser.
Log.Start(Path.Combine(Store.LogFolder, "host.log"));

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

while (await ReadMessageAsync(input) is { } message)
{
    string reply;
    try
    {
        var node = JsonNode.Parse(message) ?? throw new JsonException("empty message");
        var line = node.ToJsonString();
        reply = (string?)node["type"] switch
        {
            "config" => await ConfigAsync(line),
            // Answered here, as ADM.Core is here too, so a page with a DASH video doesn't open ADM.
            "dashChoices" => DashManifest.ChoicesJson((string?)node["text"] ?? "", new Uri((string?)node["url"] ?? "", UriKind.Absolute)),
            // A site's video, read by yt-dlp here too, so the panel's menu doesn't open ADM. Its setup goes to ADM.
            "videoChoices" => await SiteChoices.ReplyAsync(VideoTools.Current.Reader(), VideoTools.Current.Missing, (string?)node["url"] ?? "", CancellationToken.None),
            _ => await ForwardAsync(line) ?? Error("ADM did not answer"),
        };
    }
    catch (Exception e)
    {
        Log.Error("A message from the browser could not be handled.", e);
        reply = Error(e.Message);
    }
    await WriteMessageAsync(output, reply);
}
Log.Stop();

static string Error(string message) => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();

// The extension refreshes its capture rules regularly; when ADM is closed, answer from
// the saved settings instead of starting it just for that.
static async Task<string> ConfigAsync(string line)
{
    try
    {
        if (await IngestPipe.SendAsync(line, 300, CancellationToken.None) is { } reply) return reply;
    }
    catch (TimeoutException)
    {
    }
    return CaptureRules.ConfigReply(Store.Load(Store.DefaultPath).Settings);
}

static async Task<string?> ForwardAsync(string line)
{
    try
    {
        return await IngestPipe.SendAsync(line, 500, CancellationToken.None);
    }
    catch (TimeoutException)
    {
    }

    var app = Path.Combine(AppContext.BaseDirectory, "ADM.exe");
    if (!File.Exists(app)) throw new FileNotFoundException("ADM.exe was not found next to the native host.", app);
    Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = false });

    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (true)
    {
        try
        {
            return await IngestPipe.SendAsync(line, 1000, CancellationToken.None);
        }
        catch (TimeoutException) when (DateTime.UtcNow < deadline)
        {
        }
    }
}

static async Task<string?> ReadMessageAsync(Stream stream)
{
    var header = new byte[4];
    if (!await ReadExactlyOrEndAsync(stream, header)) return null;
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > MaxMessageBytes) return null;
    var body = new byte[length];
    if (!await ReadExactlyOrEndAsync(stream, body)) return null;
    return Encoding.UTF8.GetString(body);
}

static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer)
{
    try
    {
        await stream.ReadExactlyAsync(buffer);
        return true;
    }
    catch (EndOfStreamException)
    {
        return false;
    }
}

static async Task WriteMessageAsync(Stream stream, string json)
{
    var body = Encoding.UTF8.GetBytes(json);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
    await stream.FlushAsync();
}
