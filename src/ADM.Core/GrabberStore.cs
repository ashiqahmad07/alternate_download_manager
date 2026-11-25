using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADM.Core;

/// <summary>A project's file and the bodies it keeps, with the same swap and backup as <see cref="Store"/>.</summary>
public static class GrabberStore
{
    // Compact, as a big site's list is long.
    static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, GrabberProject project)
    {
        lock (project.SyncRoot) Store.Write(path, project, Options);
    }

    /// <summary>Loads a project, or the copy kept by the save before it when that one is broken; null when there is neither.</summary>
    public static GrabberProject? Load(string path)
    {
        var project = Store.TryRead<GrabberProject>(path, Options, keepBroken: true);
        if (project == null && File.Exists(path + ".bak"))
        {
            Log.Error("A grabber project could not be read, so its backup is being used.");
            project = Store.TryRead<GrabberProject>(path + ".bak", Options, keepBroken: false);
        }
        return project;
    }

    public static string BodyPath(string folder, GrabberEntry entry) =>
        Path.Combine(folder, "bodies", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Url)), 0, 16));

    /// <summary>Writes an entry's body, flushed, so it says Kept only when the body is there.</summary>
    public static void WriteBody(string folder, GrabberEntry entry, byte[] body)
    {
        var path = BodyPath(folder, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(body);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static string HashOf(byte[] body) => Convert.ToHexString(SHA256.HashData(body));

    /// <summary>The body as it came, or null if it isn't kept or has changed.</summary>
    public static byte[]? ReadBody(string folder, GrabberEntry entry)
    {
        if (!entry.Kept) return null;
        try
        {
            var body = File.ReadAllBytes(BodyPath(folder, entry));
            return HashOf(body) == entry.BodyHash ? body : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
