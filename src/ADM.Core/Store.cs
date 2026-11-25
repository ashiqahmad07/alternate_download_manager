using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADM.Core;

public static class Store
{
    /// <summary>Where the app's and the native host's logs go, beside the state.</summary>
    public static string LogFolder => Path.Combine(Path.GetDirectoryName(DefaultPath)!, "logs");

    /// <summary>Where the app keeps its state; the native host reads settings from here when the app is closed.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADM", "state.json");

    // Not indented: the file is about a fifth smaller, and it is written every few seconds while downloads run.
    static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the state, or the copy kept by the save before when the state file is missing, empty or broken.</summary>
    public static AppState Load(string path)
    {
        var state = TryLoad(path, keepBroken: true);
        if (state == null && File.Exists(path + ".bak"))
        {
            Log.Error("The saved state could not be read, so its backup is being used.");
            state = TryLoad(path + ".bak", keepBroken: false);
        }
        state ??= new AppState();
        if (state.Categories.Count == 0) state.Categories = Categories.Defaults();
        if (!state.Queues.Any(q => q.Id == DownloadQueue.MainId))
            state.Queues.Insert(0, new DownloadQueue { Id = DownloadQueue.MainId, Name = "Main queue" });
        if (!state.Queues.Any(q => q.Id == DownloadQueue.SyncId))
            state.Queues.Insert(1, new DownloadQueue { Id = DownloadQueue.SyncId, Name = "Synchronization queue", Kind = QueueKind.Sync });
        foreach (var queue in state.Queues)
            if (queue.IsMain) queue.Kind = queue.Id == DownloadQueue.SyncId ? QueueKind.Sync : QueueKind.Download;
        // Downloads saved before the parent page was kept: the referer is the best guess.
        foreach (var item in state.Downloads) item.PageUrl ??= item.Referrer;
        return state;
    }

    static AppState? TryLoad(string path, bool keepBroken) => TryRead<AppState>(path, Options, keepBroken);

    internal static T? TryRead<T>(string path, JsonSerializerOptions options, bool keepBroken) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (JsonException)
        {
            // After a power cut a file can have its full length but nothing in it; keep it to look at.
            if (keepBroken) File.Copy(path, path + ".corrupt", true);
            return null;
        }
    }

    public static void Save(string path, AppState state)
    {
        try
        {
            Write(path, state, Options);
        }
        catch (Exception e)
        {
            Log.Error("The state could not be saved.", e);
            throw;
        }
    }

    /// <summary>The state as the bytes <see cref="Save"/> writes, made where the state can be read safely, for <see cref="StateWriter"/> to write elsewhere.</summary>
    public static byte[] Serialize(AppState state) => JsonSerializer.SerializeToUtf8Bytes(state, Options);

    internal static void Write<T>(string path, T value, JsonSerializerOptions options) =>
        WriteFile(path, stream => JsonSerializer.Serialize(stream, value, options));

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, and only then swaps it in, keeping what was there as
    /// path.bak. A crash or power cut mid-save leaves one of the two intact.
    /// </summary>
    internal static void WriteFile(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            write(stream);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }
}
