using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>Where each tool comes from, and where its publisher posts its SHA-256.</summary>
public sealed record ToolSources(Uri YtDlp, Uri YtDlpSums, Uri Deno, Uri DenoSum, Uri Ffmpeg, Uri FfmpegSums)
{
    /// <summary>yt-dlp's and Deno's latest releases, and BtbN's LGPL build of ffmpeg, which only has to repackage.</summary>
    public static ToolSources Official { get; } = new(
        new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"),
        new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"),
        new Uri("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"),
        new Uri("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum"),
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip"),
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256"));
}

/// <summary>How far a tool's download has got; <see cref="Total"/> is null when the server doesn't say.</summary>
public sealed record ToolProgress(VideoTool Tool, long Done, long? Total);

/// <summary>
/// The programs a site's video needs, kept in ADM's tools folder, else found on <c>PATH</c>. They are fetched only when
/// the user says so, each checked against the SHA-256 its publisher posts, and nothing is replaced unless it matches.
/// A lock file in the folder, held shared by every run of yt-dlp and alone by a swap, keeps yt-dlp from being replaced
/// while the native host or the app runs it. A file rather than a mutex, as a mutex belongs to the thread that took it,
/// and these runs are awaited.
/// </summary>
public sealed partial class VideoTools(string folder, ToolSources sources, Func<string> path)
{
    public const long MaxToolBytes = 400L * 1024 * 1024;
    const int MaxSumsBytes = 1024 * 1024;
    static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(1);

    /// <summary>Beside <c>state.json</c>, in <c>%LOCALAPPDATA%\ADM\tools</c>.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "tools");

    public static VideoTools Current { get; } = new(DefaultFolder, ToolSources.Official, () => Environment.GetEnvironmentVariable("PATH") ?? "");

    public string Folder { get; } = folder;
    internal string LockFile => Path.Combine(Folder, "yt-dlp.lock");
    internal long ToolLimit { get; init; } = MaxToolBytes;
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    readonly ConcurrentDictionary<string, bool> _nodeUsable = new(StringComparer.OrdinalIgnoreCase);

    public string? YtDlpPath => Find("yt-dlp.exe");
    public string? FfmpegPath => Find("ffmpeg.exe");

    /// <summary>Deno, which yt-dlp uses unasked; failing that, a Node of version 20 or later on <c>PATH</c>.</summary>
    public JsRuntime? Runtime
    {
        get
        {
            if (Find("deno.exe") is { } deno) return new JsRuntime("deno", deno);
            if (OnPath("node.exe") is not { } node) return null;
            return _nodeUsable.GetOrAdd(node, n => NodeMajor(VersionOf(n)) >= 20) ? new JsRuntime("node", node) : null;
        }
    }

    public IReadOnlyList<VideoTool> Missing
    {
        get
        {
            var missing = new List<VideoTool>();
            if (YtDlpPath == null) missing.Add(VideoTool.YtDlp);
            if (Runtime == null) missing.Add(VideoTool.JsRuntime);
            if (FfmpegPath == null) missing.Add(VideoTool.Ffmpeg);
            return missing;
        }
    }

    /// <summary>
    /// yt-dlp, set up to read with the runtime found and to hold the lock while it runs, keeping the panel's reads
    /// where the app finds them; null without yt-dlp.
    /// </summary>
    public YtDlp? Reader() => YtDlpPath is { } program
        ? new YtDlp(program, Runtime, Path.Combine(Folder, "cache"), LockFile) { KeptFolder = Path.Combine(Folder, "reads") }
        : null;

    /// <summary>Fetches through the route <paramref name="settings"/> sets, its proxy included.</summary>
    public Task InstallAsync(Settings settings, IReadOnlyCollection<VideoTool> tools, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        InstallAsync(Net.ClientFor(settings), tools, progress, ct);

    /// <summary>Asks through the route <paramref name="settings"/> sets, its proxy included.</summary>
    public Task<bool> UpdateYtDlpAsync(Settings settings, CancellationToken ct) => UpdateYtDlpAsync(Net.ClientFor(settings), ct);

    /// <summary>Fetches the tools asked for into the folder, in order, stopping at the first that fails.</summary>
    public async Task InstallAsync(HttpClient client, IReadOnlyCollection<VideoTool> tools, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        if (tools.Contains(VideoTool.YtDlp))
            await ReplaceYtDlpAsync(client, await SumAsync(client, sources.YtDlpSums, "yt-dlp.exe", "yt-dlp", ct), progress, ct);
        if (tools.Contains(VideoTool.JsRuntime))
            await InstallFromZipAsync(client, VideoTool.JsRuntime, "Deno", sources.Deno, await SumAsync(client, sources.DenoSum, null, "Deno", ct), "deno.exe", progress, ct);
        if (tools.Contains(VideoTool.Ffmpeg))
            await InstallFromZipAsync(client, VideoTool.Ffmpeg, "ffmpeg", sources.Ffmpeg,
                await SumAsync(client, sources.FfmpegSums, NameOf(sources.Ffmpeg), "ffmpeg", ct), "ffmpeg.exe", progress, ct);
    }

    /// <summary>
    /// At most once a day, compares the folder's own yt-dlp with the latest release's SHA-256 and swaps the new one in,
    /// once every run of the old one has ended. A yt-dlp on <c>PATH</c> is the user's own, and left alone.
    /// </summary>
    /// <returns>Whether a new yt-dlp was swapped in.</returns>
    public async Task<bool> UpdateYtDlpAsync(HttpClient client, CancellationToken ct)
    {
        var own = Path.Combine(Folder, "yt-dlp.exe");
        if (!File.Exists(own)) return false;
        var stamp = Path.Combine(Folder, "yt-dlp.checked");
        if (File.Exists(stamp) && Now() - File.GetLastWriteTimeUtc(stamp) < UpdateEvery) return false;

        var expected = await SumAsync(client, sources.YtDlpSums, "yt-dlp.exe", "yt-dlp", ct);
        var swapped = !string.Equals(await FileHash.Sha256Async(own, ct), expected, StringComparison.OrdinalIgnoreCase);
        if (swapped) await ReplaceYtDlpAsync(client, expected, null, ct);
        File.WriteAllText(stamp, "");
        File.SetLastWriteTimeUtc(stamp, Now());
        return swapped;
    }

    /// <summary>Held while yt-dlp runs; any number may be held at once, in any process.</summary>
    public static Task<IDisposable> ReadLockAsync(string lockFile, CancellationToken ct) => LockAsync(lockFile, FileShare.ReadWrite, ct);

    /// <summary>Held while yt-dlp is replaced; waits for every run to end.</summary>
    internal static Task<IDisposable> SwapLockAsync(string lockFile, CancellationToken ct) => LockAsync(lockFile, FileShare.None, ct);

    internal static int? NodeMajor(string versionText) =>
        NodeVersion().Match(versionText) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var major) ? major : null;

    [GeneratedRegex(@"^\s*v(\d+)\.")]
    private static partial Regex NodeVersion();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Text();

    async Task ReplaceYtDlpAsync(HttpClient client, string expected, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var download = Path.Combine(Folder, "yt-dlp.exe.download");
        try
        {
            await FetchAsync(client, sources.YtDlp, download, VideoTool.YtDlp, "yt-dlp", progress, ct);
            await CheckAsync(download, expected, "yt-dlp", ct);
            using (await SwapLockAsync(LockFile, ct))
                File.Move(download, Path.Combine(Folder, "yt-dlp.exe"), true);
        }
        finally
        {
            Delete(download);
        }
    }

    async Task InstallFromZipAsync(HttpClient client, VideoTool tool, string display, Uri zip, string expected, string program,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var download = Path.Combine(Folder, program + ".download");
        var fresh = Path.Combine(Folder, program + ".new");
        try
        {
            await FetchAsync(client, zip, download, tool, display, progress, ct);
            await CheckAsync(download, expected, display, ct);
            using (var archive = ZipFile.OpenRead(download))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(program, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The {display} download had no {program} in it.");
                if (entry.Length > ToolLimit) throw TooBig(display);
                entry.ExtractToFile(fresh, true);
            }
            File.Move(fresh, Path.Combine(Folder, program), true);
        }
        finally
        {
            Delete(download);
            Delete(fresh);
        }
    }

    /// <summary>
    /// Follows redirects itself, as the app's client leaves them to whoever asks, and GitHub's release links answer
    /// with one to the file on another host.
    /// </summary>
    static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri address, CancellationToken ct)
    {
        for (int redirects = 0; ; redirects++)
        {
            var resp = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            if (redirects == Net.MaxRedirects || Net.RedirectTarget(address, resp) is not { } target) return resp;
            resp.Dispose();
            address = target;
        }
    }

    async Task FetchAsync(HttpClient client, Uri address, string file, VideoTool tool, string display, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        using var resp = await GetAsync(client, address, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        if (total > ToolLimit) throw TooBig(display);
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16];
        long done = 0;
        progress?.Report(new ToolProgress(tool, 0, total));
        while (await body.ReadAsync(buffer, ct) is var read and > 0)
        {
            done += read;
            if (done > ToolLimit) throw TooBig(display);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            progress?.Report(new ToolProgress(tool, done, total));
        }
    }

    InvalidDataException TooBig(string display) =>
        new($"The {display} download was larger than {Format.Bytes(ToolLimit)}, so it was stopped.");

    static async Task CheckAsync(string file, string expected, string display, CancellationToken ct)
    {
        if (!string.Equals(await FileHash.Sha256Async(file, ct), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {display} download did not match its published SHA-256, so it was not used.");
    }

    /// <summary>The SHA-256 a sums file gives for <paramref name="name"/>, or, with no name, the first one in it.</summary>
    static async Task<string> SumAsync(HttpClient client, Uri sums, string? name, string display, CancellationToken ct)
    {
        using var resp = await GetAsync(client, sums, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxSumsBytes) throw new InvalidDataException($"The published SHA-256 of {display} could not be read.");
        var text = await resp.Content.ReadAsStringAsync(ct);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (name != null && (fields.Length < 2 || !fields[^1].TrimStart('*').Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            if (Sha256Text().Match(line) is { Success: true } hash) return hash.Value.ToLowerInvariant();
        }
        throw new InvalidDataException($"The published SHA-256 of {display} could not be found.");
    }

    static string NameOf(Uri address) => address.Segments[^1];

    string? Find(string name)
    {
        var own = Path.Combine(Folder, name);
        return File.Exists(own) ? own : OnPath(name);
    }

    string? OnPath(string name)
    {
        foreach (var folder in path().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A folder on PATH that isn't a path at all.
            }
        }
        return null;
    }

    static string VersionOf(string program)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, "--version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return output.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    static void Delete(string file)
    {
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static async Task<IDisposable> LockAsync(string lockFile, FileShare share, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockFile))!);
        while (true)
        {
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
            }
            catch (IOException)
            {
                // Held the other way: by a swap for a read, by a read for a swap.
                await Task.Delay(LockRetry, ct);
            }
        }
    }
}
