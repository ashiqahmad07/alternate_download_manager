using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>
/// One language's texts: a JSON object of key and text, with the language's own name in "_name" and its code in "_code".
/// Anyone can write one, so a text that would go wrong is left out, and English shows in its place.
/// </summary>
public sealed class LanguageFile
{
    const string NameKey = "_name";
    const string CodeKey = "_code";
    // Names starting with this are about the file, and are not texts.
    const char NotText = '_';

    static readonly JsonDocumentOptions ByHand = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // Doubled braces first, so that "{{{0}}}" reads as a brace, a placeholder and a brace.
    static readonly Regex FormatPart = new(@"\{\{|\}\}|\{([^{}]*)\}", RegexOptions.Compiled);

    readonly Dictionary<string, string> _texts;

    LanguageFile(string name, string code, Dictionary<string, string> texts)
    {
        Name = name;
        Code = code;
        _texts = texts;
    }

    /// <summary>The language's name in that language, as the View menu lists it.</summary>
    public string Name { get; }

    public string Code { get; }

    public IEnumerable<string> Keys => _texts.Keys;

    /// <summary>
    /// Reads a file. Given English, it keeps only the keys English has, and takes a text only if its placeholders
    /// are the same as English's, so that a translation can't make string.Format throw. Without it, this is English itself.
    /// </summary>
    /// <exception cref="JsonException">The file isn't JSON, isn't an object, or has no code.</exception>
    public static LanguageFile Load(string json, LanguageFile? english = null)
    {
        using var document = JsonDocument.Parse(json, ByHand);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A language file is one JSON object.");

        var code = TextOf(root, CodeKey) ?? throw new JsonException($"A language file needs its \"{CodeKey}\".");
        var name = TextOf(root, NameKey) ?? code;

        var texts = english == null ? new Dictionary<string, string>() : new Dictionary<string, string>(english._texts);
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith(NotText) || TextOf(entry.Value) is not { } text) continue;
            if (english == null || (english._texts.TryGetValue(entry.Name, out var original) && SamePlaceholders(original, text)))
                texts[entry.Name] = text;
        }
        return new LanguageFile(name, code, texts);
    }

    /// <summary>The text for a key, or the key itself if no language has it. Without arguments the text comes back as it is, unformatted.</summary>
    public string Text(string key, params object?[] args)
    {
        var text = _texts.GetValueOrDefault(key, key);
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    static string? TextOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? TextOf(value) : null;

    static string? TextOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    /// <summary>Compares placeholders whole, format included: a format string.Format can't use would throw just as a wrong number does.</summary>
    static bool SamePlaceholders(string original, string text) =>
        PlaceholdersOf(original) is { } wanted && PlaceholdersOf(text) is { } found && wanted.SetEquals(found);

    /// <summary>The text's placeholders, or null if string.Format would refuse the text.</summary>
    static HashSet<string>? PlaceholdersOf(string text)
    {
        try
        {
            CompositeFormat.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
        return FormatPart.Matches(text).Where(part => part.Groups[1].Success).Select(part => part.Groups[1].Value).ToHashSet();
    }
}
