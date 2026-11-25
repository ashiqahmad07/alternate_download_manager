using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ADM.Core;

/// <summary>
/// The mark of the web: the Zone.Identifier stream browsers write on what they download, so Windows asks before
/// running the file and Office opens it in Protected View.
/// </summary>
public static class ZoneMark
{
    /// <summary>
    /// Marks the file as from the internet (zone 3), with the addresses it came from, leaving out any login in them.
    /// A drive without streams (FAT32, exFAT), or a write that fails, leaves the file unmarked.
    /// </summary>
    public static void Write(string path, string? hostUrl, string? referrerUrl)
    {
        var text = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (WithoutLogin(referrerUrl) is { } referrer) text.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
        if (WithoutLogin(hostUrl) is { } host) text.Append("HostUrl=").Append(host).Append("\r\n");
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>The address escaped and without its user name and password; null when it isn't one, or would break a line.</summary>
    static string? WithoutLogin(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var clean = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
        return clean.AsSpan().IndexOfAny('\r', '\n') < 0 ? clean : null;
    }
}

/// <param name="ExitCode">Null when the scanner didn't start.</param>
/// <param name="FileRemoved">The file was gone once the scanner exited, as when it quarantines a threat.</param>
/// <param name="StartError">Why the scanner didn't start.</param>
public sealed record ScanOutcome(int? ExitCode, bool FileRemoved, string? StartError);

/// <summary>Runs the virus scanner the user chose on a finished download.</summary>
public static class VirusScan
{
    /// <summary>Stands for the file in <see cref="Settings.VirusScannerArguments"/>.</summary>
    public const string FilePlaceholder = "{file}";

    /// <summary>What to say of a file the scanner took away.</summary>
    public const string RemovedFile = "The virus scanner removed the file";

    /// <summary>What a scan has to report of a file it left in place; null when it found nothing.</summary>
    public static string? Finding(ScanOutcome outcome) => outcome switch
    {
        { StartError: { } error } => "The virus scanner could not start: " + error,
        { ExitCode: not 0 and int code } => $"The virus scanner reported a problem (exit code {code})",
        _ => null,
    };

    /// <summary>The arguments for a scanner ADM knows, with {file} for the file; null for any other.</summary>
    public static string? KnownArguments(string scannerPath) =>
        Path.GetFileName(scannerPath.Trim().Trim('"')).ToLowerInvariant() switch
        {
            // Microsoft Defender: a custom scan of one file. Exit code 2 means it found a threat.
            "mpcmdrun.exe" => "-Scan -ScanType 3 -File {file}",
            _ => null,
        };

    /// <summary>
    /// Runs the scanner with no window and no shell, and waits for it. Cancelling stops it. The file name comes
    /// from the server, so it reaches the scanner as exactly one argument.
    /// </summary>
    public static async Task<ScanOutcome> RunAsync(string scanner, string arguments, string file, CancellationToken ct)
    {
        scanner = scanner.Trim().Trim('"');
        // Windows runs a batch file through cmd.exe, which expands %...% (and, if turned on, !...!) even inside
        // quotes. A name with either could close the quotes and add a command.
        if (RunsThroughCmd(scanner) && file.AsSpan().IndexOfAny('%', '!') >= 0)
            return new ScanOutcome(null, false, "The file's name has a % or !, which a batch file can't be given safely.");

        var psi = new ProcessStartInfo(scanner, CommandLine(arguments, file))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception e)
        {
            // Without the wrapper's "An error occurred trying to start process…".
            return new ScanOutcome(null, false, new Win32Exception(e.NativeErrorCode).Message);
        }
        if (process == null) return new ScanOutcome(null, false, "No process was started.");

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException) { }
                throw;
            }
            return new ScanOutcome(process.ExitCode, !File.Exists(file), null);
        }
    }

    /// <summary>
    /// The arguments with the file in double quotes in place of {file}, or after them without one. The quotes are
    /// safe because a Windows path can't contain one, so nothing in the name can end them.
    /// </summary>
    internal static string CommandLine(string arguments, string file)
    {
        if (file.Contains('"')) throw new ArgumentException("A file path can't contain a double quote.", nameof(file));
        var quoted = "\"" + file + "\"";
        // A {file} the user already put in quotes isn't quoted twice.
        var template = arguments.Replace("\"" + FilePlaceholder + "\"", FilePlaceholder).Trim();
        if (template.Contains(FilePlaceholder)) return template.Replace(FilePlaceholder, quoted);
        return template.Length == 0 ? quoted : template + " " + quoted;
    }

    /// <summary>A batch file, or cmd.exe itself. Windows ignores trailing dots and spaces, so "scan.bat." is one too.</summary>
    static bool RunsThroughCmd(string scanner)
    {
        var name = Path.GetFileName(scanner.TrimEnd(' ', '.'));
        var extension = Path.GetExtension(name);
        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd", StringComparison.OrdinalIgnoreCase);
    }
}
