namespace ADM.Core;

/// <summary>
/// Takes over what the app left behind when it was called FluxDM: its state and logs, and the part files of
/// unfinished downloads. WindowsStartup does the same for the startup entry, and install.ps1 for the browser's host.
/// </summary>
public static class OldName
{
    const string StateFile = "state.json";

    static readonly (string Old, string New)[] PartExtensions =
    {
        (".fluxpart", ".admpart"),
        (".fluxparts", ".admparts"),
        (".fluxremux", ".admremux"),
    };

    /// <summary>Moves the state and logs here from the old folder, unless state under the new name is already here.</summary>
    /// <exception cref="IOException">FluxDM is still running, or a file wouldn't move. The next start tries again.</exception>
    /// <exception cref="UnauthorizedAccessException">A file wouldn't move. The next start tries again.</exception>
    public static void TakeOverState() => MoveState(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxDM"),
        Path.GetDirectoryName(Store.DefaultPath)!,
        @"Local\FluxDM.SingleInstance");

    internal static void MoveState(string from, string to, string oldCopy)
    {
        if (!Directory.Exists(from) || File.Exists(Path.Combine(to, StateFile))) return;
        if (Mutex.TryOpenExisting(oldCopy, out var running))
        {
            running.Dispose();
            throw new IOException("FluxDM is still running.");
        }

        var logs = Path.Combine(from, "logs");
        var files = Directory.EnumerateFiles(from, StateFile + "*")
            .Concat(Directory.Exists(logs) ? Directory.EnumerateFiles(logs) : Enumerable.Empty<string>())
            // Last, because its arrival says the move is done: anything before it that fails is tried again.
            .OrderBy(file => Path.GetFileName(file) == StateFile)
            .ToList();
        foreach (var file in files)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // The native host may already have started a log of this name, and that one is in use.
            File.Move(file, FileNames.Unique(target));
        }
        RemoveIfEmpty(logs);
        RemoveIfEmpty(from);
    }

    /// <summary>Gives an unfinished download's part file and parts folder their new names, so it carries on from where it was.</summary>
    internal static void RenameParts(DownloadItem item)
    {
        // A finished one has none, and without a file name the paths would be named after its folder.
        if (item.Status == DownloadStatus.Completed || (item.PartFolder == null && item.FileName.Length == 0)) return;
        Rename(Path.ChangeExtension(item.PartPath, ".fluxpart"), item.PartPath);
        Rename(Path.ChangeExtension(item.PartsFolderPath, ".fluxparts"), item.PartsFolderPath);
        Rename(Path.ChangeExtension(item.RemuxPath, ".fluxremux"), item.RemuxPath);
    }

    /// <summary>Renames what's left in the temporary folder once the downloads have theirs, so it can be offered for deleting like any other leftovers.</summary>
    internal static void RenameOrphans(string? folder)
    {
        if (folder == null || !Directory.Exists(folder)) return;
        foreach (var (old, renamed) in PartExtensions)
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*" + old).ToList())
                Rename(path, Path.ChangeExtension(path, renamed));
    }

    static void Rename(string from, string to)
    {
        try
        {
            if (File.Exists(to) || Directory.Exists(to)) return;
            if (File.Exists(from)) File.Move(from, to);
            else if (Directory.Exists(from)) Directory.Move(from, to);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // The next start tries again. Until then this download starts over if it is started.
            Log.Error($"{from} could not be renamed.", problem);
        }
    }

    /// <summary>Delete refuses a folder with anything left in it, and what's left stays.</summary>
    static void RemoveIfEmpty(string folder)
    {
        try
        {
            Directory.Delete(folder);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
        }
    }
}
