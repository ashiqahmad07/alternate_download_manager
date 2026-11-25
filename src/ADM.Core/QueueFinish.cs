namespace ADM.Core;

/// <summary>The parts of a queue's "when done" actions that don't need the app; see <see cref="QueueSchedule"/>.</summary>
public static class QueueFinish
{
    /// <summary>
    /// Arguments for shutdown.exe. Any delay above 0 makes it force programs closed (/f is implied), so the
    /// app counts down itself and shuts down with no delay, adding /f only when asked.
    /// </summary>
    public static string[] ShutdownArguments(bool force) =>
        force ? new[] { "/s", "/t", "0", "/f" } : new[] { "/s", "/t", "0" };

    /// <summary>Only an existing file named by its full path is opened, never one looked up on PATH.</summary>
    public static bool CanRun(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
}
