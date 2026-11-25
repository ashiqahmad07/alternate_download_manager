using System.Reflection;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>One log file, which rolls over to name.1.log when it reaches its limit, so it never holds much more than twice that.</summary>
public sealed class LogFile(string path, long limit = LogFile.DefaultLimit)
{
    public const long DefaultLimit = 1024 * 1024;

    readonly object _writing = new();

    public void Info(string text) => Write("INFO ", text, null);

    public void Error(string text, Exception? exception = null) => Write("ERROR", text, exception);

    void Write(string level, string text, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {Log.Clean(text).ReplaceLineEndings(" ")}{Environment.NewLine}";
        if (exception != null) line += Log.Clean(exception.ToString()) + Environment.NewLine;
        lock (_writing)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= limit) File.Move(path, Path.ChangeExtension(path, ".1.log"), true);
                // Opened and closed each time, so a line is out of this process before whatever comes next can end it.
                File.AppendAllText(path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A log that can't be written goes unwritten. Nothing depends on it.
            }
        }
    }
}

/// <summary>
/// The log of this run, kept to make a problem reported later possible to look into. It's meant to be pasted into
/// a bug report, so a download goes by its id and host, and <see cref="Clean"/> runs on every text. Before
/// <see cref="Start"/> it writes nothing, and nothing writes to it per read or per piece.
/// </summary>
public static class Log
{
    const string CleanExit = "Exited cleanly";

    // All of an address, which can hold a comma, a semicolon or a quote; one of those at its very end is the sentence's.
    static readonly Regex Address = new(@"\b([a-zA-Z][a-zA-Z0-9+.-]*)://(?:[^\s/@'""<>]*@)?([^\s/?#:;,'""<>]+)(?:[^\s""<>]*[^\s""<>,;'])?", RegexOptions.Compiled);
    static readonly Regex SecretHeader = new(@"(?im)\b((?:Proxy-)?Authorization|(?:Set-)?Cookie):[^\r\n]*", RegexOptions.Compiled);
    // A file path names a file, and the Windows user. In quotes it ends at a closing quote, which a quote inside it isn't.
    static readonly Regex QuotedPath = new(@"(?m)(?<quote>['""])(?:[A-Za-z]:\\|\\\\).*?\k<quote>(?=[\s.,;:)]|$)", RegexOptions.Compiled);
    // Out of quotes nothing says where it ends, so it goes to the end of the line. A stack frame's source path is the build's, and worth keeping.
    static readonly Regex BarePath = new(@"(?<![\w'""])(?:[A-Za-z]:\\|\\\\)[^\r\n]*", RegexOptions.Compiled);
    static readonly Regex SourceLine = new(@":line \d+$", RegexOptions.Compiled);

    static volatile LogFile? _file;

    public static void Start(string path)
    {
        var file = new LogFile(path);
        if (EndedUncleanly(path)) file.Info("The last run did not end cleanly.");
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        file.Info($"ADM {version} starting on {Environment.OSVersion}");
        _file = file;
    }

    public static void Info(string text) => _file?.Info(text);

    public static void Error(string text, Exception? exception = null) => _file?.Error(text, exception);

    /// <summary>Says this run is ending well. If it then goes on after all, as when Windows' shutdown is called off, a later end says so again.</summary>
    public static void ExitingCleanly() => _file?.Info(CleanExit);

    public static void Stop()
    {
        ExitingCleanly();
        _file = null;
    }

    /// <summary>An address's host, the most a log says about where a download comes from: a token can sit anywhere after it.</summary>
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(no host)";

    /// <summary>
    /// Text with nothing private left in it: an address is cut to its scheme and host, a cookie or login header
    /// loses its value, and a file path goes.
    /// </summary>
    public static string Clean(string text)
    {
        var cleaned = SecretHeader.Replace(Address.Replace(text, "$1://$2"), "$1:");
        cleaned = QuotedPath.Replace(cleaned, "${quote}(path)${quote}");
        return BarePath.Replace(cleaned, path => SourceLine.IsMatch(path.Value) ? path.Value : "(path)");
    }

    /// <summary>Why something failed, as a log can say it. An FTP server's reply can hold the path it was asked for, so only its code is kept.</summary>
    public static string Reason(Exception e) => e is FtpException ftp ? $"the FTP server replied {ftp.Code}" : e.Message;

    static bool EndedUncleanly(string path)
    {
        try
        {
            var last = File.Exists(path) ? File.ReadLines(path).LastOrDefault(line => line.Length > 0) : null;
            return last != null && !last.EndsWith(CleanExit, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
