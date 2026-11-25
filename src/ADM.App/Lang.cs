using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// The chosen language's texts. XAML names a text with {l:T key}, and code calls T with the key written out, which is
/// how a test finds it. A change of language takes effect at the next start, so nothing on screen has to follow it.
/// </summary>
public static class Lang
{
    const string EnglishResource = "ADM.App.Languages.en.json";

    // Built into the app, so what a missing text falls back to can't itself go missing or be edited.
    static readonly LanguageFile English = ReadEnglish();

    static LanguageFile _current = English;

    /// <summary>Beside the app; a file put here is offered in the View menu.</summary>
    static string Folder => Path.Combine(AppContext.BaseDirectory, "Languages");

    public static string Code => _current.Code;

    public static string T(string key, params object?[] args) => _current.Text(key, args);

    /// <summary>
    /// A file dialog's filter from pairs of a text and its patterns. A dialog throws on a filter it can't split,
    /// so a translation's own "|" can't be left in.
    /// </summary>
    public static string Filter(params string[] textsAndPatterns) =>
        string.Join('|', textsAndPatterns.Select(part => part.Replace('|', ' ')));

    /// <summary>Takes the language with this code, or English if there's none. Goes before the first window, whose texts are read as it loads.</summary>
    public static void Start(string code)
    {
        _current = Available().FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) ?? English;
        StatusWords.Current = new StatusWords
        {
            Queued = Lang.T("status.queued"),
            Downloading = Lang.T("status.downloading"),
            Recording = Lang.T("status.recording"),
            Paused = Lang.T("status.paused"),
            Completed = Lang.T("status.completed"),
            Checking = Lang.T("status.checking"),
            Scanning = Lang.T("status.scanning"),
            ErrorOf = Lang.T("status.error_of"),
            RetryingOf = Lang.T("status.retrying_of"),
            PausedOf = Lang.T("status.paused_of"),
            LastComplete = Lang.T("status.last_complete"),
            LastFailed = Lang.T("status.last_failed"),
            LastWaiting = Lang.T("status.last_waiting"),
            Hashing = Lang.T("status.hashing"),
            HashMatches = Lang.T("status.hash_matches"),
            HashNotChecked = Lang.T("status.hash_not_checked"),
            HashMismatchOf = Lang.T("status.hash_mismatch_of"),
            UnknownSize = Lang.T("status.unknown_size"),
            ResumeYes = Lang.T("status.resume_yes"),
            ResumeNo = Lang.T("status.resume_no"),
            FileChanged = Lang.T("status.file_changed"),
            MovingIntoPlace = Lang.T("status.moving_into_place"),
            UsingBrowserProxy = Lang.T("status.using_browser_proxy"),
            WaitingForNetwork = Lang.T("status.waiting_for_network"),
            WaitingForVideo = Lang.T("status.waiting_for_video"),
            ReadingVideo = Lang.T("status.reading_video"),
            ScriptNotLoaded = Lang.T("status.script_not_loaded"),
            ReconnectingOf = Lang.T("status.reconnecting_of"),
        };
    }

    /// <summary>The text for a key that XAML names.</summary>
    internal static string ForMarkup(string key) => _current.Text(key);

    /// <summary>English, then every file in the folder that can be read. The code picks a language from this list, and is never made into a path.</summary>
    public static List<LanguageFile> Available()
    {
        var languages = new List<LanguageFile> { English };
        if (!Directory.Exists(Folder)) return languages;
        foreach (var file in Directory.GetFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var language = LanguageFile.Load(File.ReadAllText(file), English);
                if (!languages.Exists(known => string.Equals(known.Code, language.Code, StringComparison.OrdinalIgnoreCase)))
                    languages.Add(language);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file nobody can read is no reason to fail; it just isn't offered.
            }
        }
        return languages;
    }

    static LanguageFile ReadEnglish()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EnglishResource)
            ?? throw new InvalidOperationException($"{EnglishResource} isn't built into the app.");
        using var reader = new StreamReader(stream);
        return LanguageFile.Load(reader.ReadToEnd());
    }
}

/// <summary>A language's text in XAML: {l:T key}.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.ForMarkup(Key);
}
