using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>The programs a site's video needs: yt-dlp reads the page, a JavaScript runtime lets it read YouTube, ffmpeg joins.</summary>
public enum VideoTool { YtDlp, JsRuntime, Ffmpeg }

/// <summary>A JavaScript runtime yt-dlp is told to use: <c>deno</c> or <c>node</c>.</summary>
public sealed record JsRuntime(string Name, string Path);

/// <summary>One file a site offers, as yt-dlp describes it: a picture, a sound, or both.</summary>
/// <param name="AvailableAt">
/// When the site starts to serve it, as YouTube refuses its files with a 403 until the page's preroll ad would have
/// played; null for at once.
/// </param>
public sealed record SiteFormat(string Id, Uri Url, string Protocol, string Extension, string? VideoCodec, string? AudioCodec,
    int Width, int Height, double Fps, long? Size, double Bitrate, IReadOnlyDictionary<string, string> Headers, DateTimeOffset? AvailableAt = null)
{
    static readonly HashSet<string> SoundContainers = new(StringComparer.OrdinalIgnoreCase) { "m4a", "mp3", "opus", "ogg", "oga", "aac", "wav", "flac", "weba" };

    /// <summary>A file yt-dlp names no codec or size for, as Facebook's sd and hd files: its container says whether it is a sound, else it has both.</summary>
    bool Unknown => VideoCodec == null && AudioCodec == null && Width == 0 && Height == 0;

    /// <summary>A codec yt-dlp didn't name counts as there, as a file of one picture with no sound says "none".</summary>
    public bool HasVideo => VideoCodec != "none" && (VideoCodec != null || Height > 0 || Width > 0 || (Unknown && !SoundContainers.Contains(Extension)));
    public bool HasAudio => AudioCodec != "none" && (AudioCodec != null || HasVideo || (Unknown && SoundContainers.Contains(Extension)));

    /// <summary>The resolution as people say it: the shorter side, so a phone video 1080 wide and 1920 high is 1080p.</summary>
    public int Lines => Width > 0 && Height > 0 ? Math.Min(Width, Height) : Height;

    internal bool IsH264 => VideoCodec is { } c && (c.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || c.StartsWith("h264", StringComparison.OrdinalIgnoreCase));
    internal bool IsAac => AudioCodec is { } c && (c.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || c.Equals("aac", StringComparison.OrdinalIgnoreCase));
    internal bool IsStream => Protocol.StartsWith("m3u8", StringComparison.Ordinal);
}

/// <summary>What yt-dlp read from a page: its title, and the formats ADM can fetch.</summary>
public sealed partial record SiteVideoInfo(string PageUrl, string Title, bool IsLive, IReadOnlyList<SiteFormat> Formats)
{
    /// <summary>A format's extension becomes the file's, so a site's word for it is only taken if it is a plain one.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{1,5}$")]
    private static partial Regex PlainExtension();

    static readonly HashSet<string> Protocols = new(StringComparer.Ordinal) { "http", "https", "m3u8", "m3u8_native" };

    /// <summary>yt-dlp's <c>-J</c> answer. Formats ADM can't fetch, or that have DRM, are left out.</summary>
    public static SiteVideoInfo Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException(NotAnAnswer);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(NotAnAnswer);
        }
        if ((string?)root["_type"] == "playlist")
            throw new InvalidDataException("This page is a list of videos. Open one of them and try again.");

        // An extractor that finds a single file answers with it at the top.
        var listed = root["formats"] as JsonArray ?? (root["url"] != null ? new JsonArray(root.DeepClone()) : null);
        if (listed == null) throw new InvalidDataException(NotAnAnswer);
        var formats = listed.OfType<JsonObject>().Select(Format).OfType<SiteFormat>().ToList();
        var live = Bool(root["is_live"]) || Text(root["live_status"]) == "is_live";
        return new SiteVideoInfo(Text(root["webpage_url"]) ?? Text(root["original_url"]) ?? "", Text(root["title"]) ?? "video", live, formats);
    }

    const string NotAnAnswer = "yt-dlp's answer could not be read.";

    static SiteFormat? Format(JsonObject f)
    {
        var protocol = Text(f["protocol"]) ?? "https";
        if (!Protocols.Contains(protocol) || Bool(f["has_drm"])) return null;
        if (!Uri.TryCreate(Text(f["url"]), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (f["http_headers"] is JsonObject h)
            foreach (var (name, value) in h)
                if (Text(value) is { } text) headers[name] = text;
        long? size = Number(f["filesize"]) is > 0 and var exact ? (long)exact : Number(f["filesize_approx"]) is > 0 and var approx ? (long)approx : null;
        var extension = Text(f["ext"]) is { } ext && PlainExtension().IsMatch(ext) ? ext.ToLowerInvariant() : "mp4";
        var format = new SiteFormat(Text(f["format_id"]) ?? "", url, protocol, extension, Text(f["vcodec"]), Text(f["acodec"]),
            (int)Number(f["width"]), (int)Number(f["height"]), Number(f["fps"]), size, Number(f["tbr"]), headers, Time(f["available_at"]));
        return format.HasVideo || format.HasAudio ? format : null;
    }

    static readonly double LastUnixSecond = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>yt-dlp's Unix time in seconds, rounded up to the millisecond; null for none, or for one past any clock.</summary>
    static DateTimeOffset? Time(JsonNode? node) =>
        Number(node) is > 0 and var seconds && seconds < LastUnixSecond ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Ceiling(seconds * 1000)) : null;

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;
}

/// <summary>A choice for the panel's menu: <see cref="Key"/> is what the download keeps.</summary>
public sealed record SiteChoice(string Key, string Label, long? Size);

/// <summary>The files a choice comes to. For Audio only, <see cref="Main"/> is the sound.</summary>
public sealed record SitePick(SiteFormat Main, SiteFormat? Audio, string Extension);

/// <summary>
/// Each resolution as MP4, then Original, then Audio only. A choice is a key (<c>mp4:1080</c>, <c>original</c>,
/// <c>audio</c>) rather than yt-dlp's format number, as a site may renumber between reads.
/// </summary>
public static class SiteChoices
{
    const string NoFormat = "This video can't be downloaded: its site offers no format ADM can fetch.";

    public static IReadOnlyList<SiteChoice> For(SiteVideoInfo info)
    {
        var choices = new List<SiteChoice>();
        if (info.Formats.Count == 0) return choices;
        foreach (var lines in info.Formats.Where(f => f.HasVideo && f.Lines > 0).Select(f => f.Lines).Distinct().OrderByDescending(l => l))
        {
            var pick = Pick(info, $"mp4:{lines}");
            var fps = pick.Main.Fps > 30 ? Math.Round(pick.Main.Fps).ToString(CultureInfo.InvariantCulture) : "";
            var codec = CodecName(pick.Main.VideoCodec) is { } name ? $" ({name})" : "";
            choices.Add(new SiteChoice($"mp4:{lines}", $"{lines}p{fps} MP4{codec}", SizeOf(pick)));
        }
        // A file whose resolution yt-dlp doesn't know is offered by its own name.
        foreach (var file in info.Formats.Where(f => f.HasVideo && f.Lines == 0).OrderByDescending(f => f.Bitrate).ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            var key = "file:" + file.Id;
            choices.Add(new SiteChoice(key, $"{Describe(key)} {file.Extension.ToUpperInvariant()}", SizeOf(Pick(info, key))));
        }
        choices.Add(new SiteChoice("original", Describe("original"), SizeOf(Pick(info, "original"))));
        if (info.Formats.Any(IsSoundAlone)) choices.Add(new SiteChoice("audio", Describe("audio"), SizeOf(Pick(info, "audio"))));
        return choices;
    }

    public static SitePick Pick(SiteVideoInfo info, string key)
    {
        var formats = info.Formats;
        if (formats.Count == 0) throw new InvalidDataException(NoFormat);
        var sounds = formats.Where(IsSoundAlone).ToList();

        if (key == "audio")
        {
            var sound = BestSound(sounds) ?? throw new InvalidDataException(NoFormat);
            return new SitePick(sound, null, sound.IsAac ? ".m4a" : "." + sound.Extension);
        }

        var pictures = formats.Where(f => f.HasVideo).ToList();
        if (key.StartsWith("file:", StringComparison.Ordinal) && pictures.FirstOrDefault(f => "file:" + f.Id == key) is { } file)
        {
            var sound = file.HasAudio ? null : BestSound(sounds);
            return new SitePick(file, sound, sound == null ? "." + file.Extension : Container(file, sound));
        }

        var sized = pictures.Where(f => f.Lines > 0).ToList();
        if (key.StartsWith("mp4:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && sized.Count > 0)
        {
            // The next lower resolution if this one is gone, as for HLS; failing that, the lowest there is.
            var lines = sized.Select(f => f.Lines).Where(l => l <= asked).DefaultIfEmpty(sized.Min(f => f.Lines)).Max();
            var here = sized.Where(f => f.Lines == lines).ToList();
            var alone = Ranked(here.Where(f => !f.HasAudio));
            var both = Ranked(here.Where(f => f.HasAudio));
            // A picture alone is better only with a sound to add; else a file that has both, else the picture alone.
            var sound = BestSound(sounds);
            if (alone.Count > 0 && (sound != null || both.Count == 0)) return new SitePick(alone[0], sound, ".mp4");
            return new SitePick(both[0], null, ".mp4");
        }

        // Original, and any key not known: the best as the site has it, a whole file before a stream as for MP4.
        var best = pictures.OrderByDescending(f => f.Lines).ThenByDescending(f => f.Fps).ThenBy(f => f.IsStream).ThenByDescending(f => f.Bitrate).FirstOrDefault();
        if (best == null)
        {
            var sound = BestSound(sounds) ?? throw new InvalidDataException(NoFormat);
            return new SitePick(sound, null, "." + sound.Extension);
        }
        var audio = best.HasAudio ? null : sounds.OrderByDescending(s => s.Bitrate).FirstOrDefault();
        return new SitePick(best, audio, audio == null ? "." + best.Extension : Container(best, audio));
    }

    /// <summary>
    /// The files a download's parts came from, as <see cref="DownloadItem.SiteFormats"/> keeps them, while the read still
    /// has them all, so a resumed download carries on with them rather than starting over on files ranked higher now;
    /// otherwise, or for a download with no parts yet, the choice's files.
    /// </summary>
    public static SitePick Pick(SiteVideoInfo info, string key, string? kept)
    {
        var pick = Pick(info, key);
        var ids = (kept ?? "").Split('|').Select(f => f[..Math.Max(0, f.LastIndexOf(':'))]).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || !ids.All(id => info.Formats.Any(f => f.Id == id))) return pick;
        var carried = Pick(info with { Formats = info.Formats.Where(f => ids.Contains(f.Id)).ToList() }, key);
        // A site may number its formats afresh on each read: the files kept must still be the quality the choice is.
        return carried.Main.Lines == pick.Main.Lines && carried.Extension == pick.Extension ? carried : pick;
    }

    /// <summary>What the panel's menu says for a key.</summary>
    public static string Describe(string key) => key switch
    {
        "original" => "Original",
        "audio" => "Audio only",
        "file:hd" => "HD",
        "file:sd" => "SD",
        _ when key.StartsWith("file:", StringComparison.Ordinal) => key[5..],
        _ when key.StartsWith("mp4:", StringComparison.Ordinal) => $"{key[4..]}p MP4",
        _ => key,
    };

    public static string Json(SiteVideoInfo info) => Reply(info).ToJsonString();

    /// <summary>
    /// The native host's answer to the panel: the page's choices, or why there are none, and in either case which
    /// tools are missing. yt-dlp is run only for a web address, and not again for a page it read lately; what it
    /// read is kept for the download that may follow.
    /// </summary>
    public static async Task<string> ReplyAsync(YtDlp? reader, IReadOnlyList<VideoTool> missing, string pageUrl, CancellationToken ct)
    {
        JsonArray Tools() => new(missing.Select(t => (JsonNode)ToolName(t)).ToArray());
        string Failed(string error) => new JsonObject { ["ok"] = false, ["error"] = error, ["missingTools"] = Tools() }.ToJsonString();

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            return Failed("Only a web page's video can be read.");
        if (reader == null)
            return Failed("yt-dlp is needed to read this page's video. Set it up in ADM's Options > Video.");
        try
        {
            var reply = Reply(reader.Kept(pageUrl) ?? await reader.ReadAsync(pageUrl, keep: true, ct));
            reply["missingTools"] = Tools();
            return reply.ToJsonString();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failed(e.Message);
        }
    }

    public static string ToolName(VideoTool tool) => tool switch
    {
        VideoTool.YtDlp => "yt-dlp",
        VideoTool.JsRuntime => "js-runtime",
        _ => "ffmpeg",
    };

    static JsonObject Reply(SiteVideoInfo info)
    {
        var choices = new JsonArray();
        foreach (var choice in For(info))
        {
            var item = new JsonObject { ["key"] = choice.Key, ["label"] = choice.Label };
            if (choice.Size is long size) item["size"] = size;
            choices.Add(item);
        }
        return new JsonObject { ["ok"] = true, ["title"] = info.Title, ["choices"] = choices };
    }

    /// <summary>
    /// H.264 before any other codec, then more frames a second, then a whole file before a stream, then the higher
    /// bitrate. A stream's bitrate is its playlist's peak, not a file's average, so it isn't weighed against a file's;
    /// and a file's size is known before it downloads, where a stream's is known only once it has.
    /// </summary>
    static List<SiteFormat> Ranked(IEnumerable<SiteFormat> pictures) =>
        pictures.OrderByDescending(f => f.IsH264).ThenByDescending(f => f.Fps).ThenBy(f => f.IsStream).ThenByDescending(f => f.Bitrate).ToList();

    static bool IsSoundAlone(SiteFormat f) => f.HasAudio && !f.HasVideo;

    /// <summary>AAC before any other sound, as MP4 plays it everywhere, then the higher bitrate.</summary>
    static SiteFormat? BestSound(IEnumerable<SiteFormat> sounds) =>
        sounds.OrderByDescending(s => s.IsAac).ThenByDescending(s => s.Bitrate).FirstOrDefault();

    /// <summary>What a pick's files come to as yt-dlp gives their sizes; null if it doesn't give them all.</summary>
    internal static long? SizeOf(SitePick pick) =>
        pick.Main.Size is long main && (pick.Audio == null || pick.Audio.Size != null) ? main + (pick.Audio?.Size ?? 0) : null;

    /// <summary>MP4 if both are MP4, WebM if both are WebM, else Matroska, which takes anything.</summary>
    static string Container(SiteFormat video, SiteFormat audio)
    {
        static bool Mp4(SiteFormat f) => f.Extension is "mp4" or "m4a";
        if (Mp4(video) && Mp4(audio)) return ".mp4";
        if (video.Extension == "webm" && audio.Extension == "webm") return ".webm";
        return ".mkv";
    }

    static string? CodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        null => null,
        var c when c.StartsWith("vp9") || c.StartsWith("vp09") => "VP9",
        var c when c.StartsWith("av01") => "AV1",
        var c when c.StartsWith("hev1") || c.StartsWith("hvc1") => "HEVC",
        _ => null,
    };
}

/// <summary>
/// Runs yt-dlp to read a page, and only to read it: ADM's engine fetches the files. The browser's cookies are never
/// given to it. It stops after <see cref="Timeout"/>, and its answer stops at <see cref="MaxAnswerBytes"/>.
/// </summary>
public sealed class YtDlp(string program, JsRuntime? runtime, string cacheFolder, string? lockFile = null)
{
    public const int MaxAnswerBytes = 32 * 1024 * 1024;

    public string Program { get; } = program;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    internal int AnswerLimit { get; init; } = MaxAnswerBytes;

    /// <summary>
    /// Where a read asked to be kept is kept, for the downloads that follow the panel's menu; null keeps none. Each
    /// then starts without running yt-dlp again, and the time a site makes its files wait, as YouTube does while its
    /// preroll ad would play, has been running since the read.
    /// </summary>
    public string? KeptFolder { get; init; }

    /// <summary>How long a kept read is used: as long as the extension keeps the menu's choices.</summary>
    public TimeSpan KeptFor { get; init; } = TimeSpan.FromMinutes(30);

    static readonly string[] NetworkSigns = { "Unable to download", "TransportError", "timed out", "getaddrinfo", "Connection reset" };

    /// <summary>
    /// The address comes after <c>--</c>, so it is never read as an option. A page of videos is only listed, as ADM
    /// turns it down either way: one request says so, rather than one for each of its videos.
    /// </summary>
    public IReadOnlyList<string> ArgumentsFor(string pageUrl)
    {
        var args = new List<string> { "-J", "--ignore-config", "--no-playlist", "--flat-playlist", "--no-progress", "--encoding", "utf-8", "--cache-dir", cacheFolder };
        if (runtime != null) args.AddRange(new[] { "--js-runtimes", $"{runtime.Name}:{runtime.Path}" });
        args.AddRange(new[] { "--", pageUrl });
        return args;
    }

    public Task<SiteVideoInfo> ReadAsync(string pageUrl, CancellationToken ct) => ReadAsync(pageUrl, keep: false, ct);

    /// <param name="keep">Keeps what was read, if it could be, in <see cref="KeptFolder"/>.</param>
    public async Task<SiteVideoInfo> ReadAsync(string pageUrl, bool keep, CancellationToken ct)
    {
        using var held = lockFile == null ? null : await VideoTools.ReadLockAsync(lockFile, ct);
        var psi = new ProcessStartInfo(Program)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in ArgumentsFor(pageUrl)) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var errors = ReadErrorsAsync(process.StandardError);
        byte[] answer;
        try
        {
            answer = await ReadAnswerAsync(process.StandardOutput.BaseStream, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidDataException)
        {
            Stop(process);
            ct.ThrowIfCancellationRequested();
            if (e is InvalidDataException) throw;
            throw new TimeoutException($"yt-dlp did not answer within {Timeout.TotalSeconds:0} seconds.");
        }

        var (lastError, lastLine) = await errors;
        if (process.ExitCode != 0)
        {
            var message = lastError ?? lastLine ?? $"yt-dlp failed (exit code {process.ExitCode}).";
            if (NetworkSigns.Any(sign => message.Contains(sign, StringComparison.OrdinalIgnoreCase))) throw new HttpRequestException(message);
            throw new InvalidDataException(message);
        }
        var info = SiteVideoInfo.Parse(Encoding.UTF8.GetString(answer));
        if (keep) Keep(pageUrl, answer);
        return info;
    }

    /// <summary>
    /// The page's kept read if it is recent, which stays kept: the menu asked again answers from it, and each download
    /// picked from the menu starts from it, another quality as much as the first.
    /// </summary>
    public SiteVideoInfo? Kept(string pageUrl)
    {
        if (KeptFolder == null) return null;
        try
        {
            var file = new FileInfo(KeptPath(pageUrl));
            if (!file.Exists) return null;
            if (!IsRecent(file))
            {
                TryDelete(file.FullName);
                return null;
            }
            return SiteVideoInfo.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(file.FullName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    string KeptPath(string pageUrl) =>
        Path.Combine(KeptFolder!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pageUrl))) + ".json");

    bool IsRecent(FileInfo file) => DateTime.UtcNow - file.LastWriteTimeUtc is var age && age >= TimeSpan.Zero && age <= KeptFor;

    /// <summary>Whole or not at all, as the native host keeps a read while the app may be taking one. A read not kept is only read again.</summary>
    void Keep(string pageUrl, byte[] answer)
    {
        if (KeptFolder == null) return;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(KeptFolder);
            foreach (var file in new DirectoryInfo(KeptFolder).EnumerateFiles())
                if (!IsRecent(file)) TryDelete(file.FullName);
            var path = KeptPath(pageUrl);
            temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, answer);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (temp != null) TryDelete(temp);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    async Task<byte[]> ReadAnswerAsync(Stream output, CancellationToken ct)
    {
        var answer = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (await output.ReadAsync(buffer, ct) is var read and > 0)
        {
            if (answer.Length + read > AnswerLimit) throw new InvalidDataException("yt-dlp's answer was too long to read.");
            answer.Write(buffer, 0, read);
        }
        return answer.ToArray();
    }

    /// <summary>yt-dlp's last <c>ERROR:</c> line without its prefix, and its last line of any kind.</summary>
    static async Task<(string? Error, string? Line)> ReadErrorsAsync(StreamReader errors)
    {
        string? error = null, last = null;
        while (await errors.ReadLineAsync() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            last = line;
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) error = line["ERROR:".Length..].Trim();
        }
        return (error, last);
    }

    static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
