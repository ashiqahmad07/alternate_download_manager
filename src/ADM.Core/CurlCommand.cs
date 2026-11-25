using System.Text;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>What a command asks for. <see cref="Request"/> is null when <see cref="Error"/> says why it can't be done.</summary>
public sealed record CurlResult(DownloadRequest? Request, string? Error, int HeadersLeftOut);

/// <summary>
/// Reads a browser's Copy as cURL, which holds what a download behind a login needs. It only reads text: it starts
/// no process and opens no file, so what a shell or curl would expand or fetch stays as written.
/// </summary>
public static partial class CurlCommand
{
    public const string OpenQuote = "The command has a quote that is never closed.";
    public const string NoAddress = "The command has no http, https or ftp address.";
    public const string CantRequest = "The command sends data or makes a request other than GET, which ADM can't do. A plain download would bring back something else.";

    // What ends a command, when not in quotes. Copy all as cURL joins commands with " ;" or " &", and what follows is for another site.
    static readonly HashSet<string> CommandEnds = new(StringComparer.OrdinalIgnoreCase) { ";", "&", "&&", "|", "||", "curl", "curl.exe" };

    // Every short option of curl's that takes a value, since a value can hang on to the letters: -w%{url} isn't -o.
    static readonly Dictionary<char, string> ShortOptions = new()
    {
        ['A'] = "--user-agent", ['b'] = "--cookie", ['c'] = "--cookie-jar", ['C'] = "--continue-at", ['d'] = "--data",
        ['D'] = "--dump-header", ['e'] = "--referer", ['E'] = "--cert", ['F'] = "--form", ['H'] = "--header",
        ['K'] = "--config", ['m'] = "--max-time", ['o'] = "--output", ['P'] = "--ftp-port", ['Q'] = "--quote",
        ['r'] = "--range", ['t'] = "--telnet-option", ['T'] = "--upload-file", ['u'] = "--user", ['U'] = "--proxy-user",
        ['w'] = "--write-out", ['x'] = "--proxy", ['X'] = "--request", ['y'] = "--speed-time", ['Y'] = "--speed-limit",
        ['z'] = "--time-cond",
    };

    // Beyond those: the long options whose value matters, or could be taken for the file's address. An unknown
    // option's value is passed over anyway, unless it's an address.
    static readonly HashSet<string> LongOptionsWithValues = new(ShortOptions.Values)
    {
        "--url", "--preproxy", "--proxy1.0", "--socks4", "--socks4a", "--socks5", "--socks5-hostname", "--doh-url",
    };

    // How the first quoted word opens tells which shell it was written for.
    [GeneratedRegex(@"(?:^|\s)(\$?'|\^?"")")]
    private static partial Regex FirstQuote();

    [GeneratedRegex(@"\^\r?\n")]
    private static partial Regex CaretAtLineEnd();

    // Chrome hides these from curl's own patterns.
    [GeneratedRegex(@"\\([\[\]{}])")]
    private static partial Regex HiddenBracket();

    public static bool IsCommand(string text)
    {
        var start = text.TrimStart();
        return start.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) || start.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase);
    }

    public static CurlResult Parse(string text)
    {
        var words = WrittenForCmd(text) ? CmdWords(text) : PosixWords(text);
        if (words == null) return new CurlResult(null, OpenQuote, 0);

        var taken = new Taken();
        bool optionsOver = false;
        int i = 0;
        string? NextWord() => ++i < words.Count ? words[i].Text : null;

        // The first word is curl itself.
        while (++i < words.Count)
        {
            var word = words[i];
            if (word.Bare && CommandEnds.Contains(word.Text)) break;

            if (optionsOver || word.Text.Length < 2 || word.Text[0] != '-')
            {
                taken.Word ??= AddressIn(word.Text);
            }
            else if (word.Text == "--")
            {
                optionsOver = true;
            }
            else if (word.Text.StartsWith("--", StringComparison.Ordinal))
            {
                Take(word.Text, LongOptionsWithValues.Contains(word.Text) ? NextWord() : null, taken);
            }
            else
            {
                // Short options can share a word, and a value can follow the last one's letter: -sSLo name, -XPOST.
                for (int letter = 1; letter < word.Text.Length; letter++)
                {
                    if (!ShortOptions.TryGetValue(word.Text[letter], out var option)) continue;
                    Take(option, letter + 1 < word.Text.Length ? word.Text[(letter + 1)..] : NextWord(), taken);
                    break;
                }
            }
        }

        if (taken.Refused) return new CurlResult(null, CantRequest, 0);
        var address = taken.UrlOption ?? taken.Word;
        if (address == null) return new CurlResult(null, NoAddress, 0);

        taken.Request.Url = address;
        taken.Request.Cookies = taken.Cookies.Count > 0 ? string.Join("; ", taken.Cookies) : null;
        return new CurlResult(taken.Request, null, taken.HeadersLeftOut);
    }

    sealed class Taken
    {
        public DownloadRequest Request { get; } = new();
        public List<string> Cookies { get; } = new();
        public string? UrlOption { get; set; }
        public string? Word { get; set; }
        public int HeadersLeftOut { get; set; }
        public bool Refused { get; set; }
    }

    static void Take(string option, string? value, Taken taken)
    {
        if (option.StartsWith("--data", StringComparison.Ordinal) || option is "--form" or "--form-string" or "--upload-file" or "--json")
        {
            taken.Refused = true;
            return;
        }
        if (value == null) return;
        switch (option)
        {
            case "--url":
                taken.UrlOption ??= AddressIn(value);
                break;
            case "--request":
                if (!value.Equals("GET", StringComparison.OrdinalIgnoreCase)) taken.Refused = true;
                break;
            case "--header":
                if (!TakeHeader(value, taken)) taken.HeadersLeftOut++;
                break;
            case "--cookie":
                // Without a "=", curl takes it for a file to read.
                if (value.Contains('=') && Usable(value)) taken.Cookies.Add(value.Trim());
                break;
            case "--referer":
                if (Usable(value)) taken.Request.Referrer = value.Trim();
                break;
            case "--user-agent":
                if (Usable(value)) taken.Request.UserAgent = value.Trim();
                break;
            case "--user":
                TakeLogin(value, taken);
                break;
            case "--output":
                taken.Request.FileName = OutputName(value);
                break;
        }
    }

    static bool TakeHeader(string header, Taken taken)
    {
        int colon = header.IndexOf(':');
        if (colon <= 0) return false;
        var name = header[..colon].Trim();
        var value = header[(colon + 1)..].Trim();
        if (!Usable(value)) return false;

        if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) taken.Cookies.Add(value);
        else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) taken.Request.Referrer = value;
        else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) taken.Request.UserAgent = value;
        else return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && TakeBasicLogin(value, taken);
        return true;
    }

    static bool TakeBasicLogin(string authorization, Taken taken)
    {
        var parts = authorization.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].Equals("Basic", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return TakeLogin(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])), taken);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static bool TakeLogin(string login, Taken taken)
    {
        int colon = login.IndexOf(':');
        var user = colon < 0 ? login : login[..colon];
        if (user.Length == 0 || !Usable(login)) return false;
        taken.Request.UserName = user;
        taken.Request.Password = colon < 0 ? null : login[(colon + 1)..];
        return true;
    }

    /// <summary>The last part of a path only, so a name can't lead out of the folder the user chose. "-" is curl's standard output.</summary>
    static string? OutputName(string output)
    {
        var name = output.Replace('\\', '/').Split('/')[^1];
        return name.Length == 0 || name == "-" ? null : FileNames.Sanitize(name);
    }

    static string? AddressIn(string word)
    {
        var address = HiddenBracket().Replace(word.Trim(), "$1");
        return Usable(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri) ? address : null;
    }

    /// <summary>A line break in a value would start another header, and no real value has one.</summary>
    static bool Usable(string value) => value.Length > 0 && !value.Any(char.IsControl);

    // ---- Words

    /// <param name="Bare">No quote in it, so a ";" really does end the command.</param>
    readonly record struct Word(string Text, bool Bare);

    sealed class WordList
    {
        readonly StringBuilder _word = new();
        bool _started, _quoted;

        public List<Word> Words { get; } = new();

        public void Add(char c)
        {
            _started = true;
            _word.Append(c);
        }

        public void Add(string text)
        {
            _started = true;
            _word.Append(text);
        }

        public void Quoted() => _started = _quoted = true;

        public void End()
        {
            if (!_started) return;
            Words.Add(new Word(_word.ToString(), !_quoted));
            _word.Clear();
            _started = _quoted = false;
        }
    }

    static bool WrittenForCmd(string text)
    {
        var quote = FirstQuote().Match(text);
        if (quote.Success && quote.Groups[1].Value != "\"") return quote.Groups[1].Value == "^\"";
        // Plain double quotes read the same either way, or nearly.
        return CaretAtLineEnd().IsMatch(text);
    }

    /// <summary>Moves past a line end at <paramref name="i"/>, if there is one.</summary>
    static bool SkipLineEnd(string text, ref int i)
    {
        int next = i < text.Length && text[i] == '\r' ? i + 1 : i;
        if (next >= text.Length || text[next] != '\n') return false;
        i = next + 1;
        return true;
    }

    /// <summary>The words of a command for bash. Null if a quote is left open.</summary>
    static List<Word>? PosixWords(string text)
    {
        var words = new WordList();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i++];
            switch (c)
            {
                case '\'':
                    int end = text.IndexOf('\'', i);
                    if (end < 0) return null;
                    words.Quoted();
                    words.Add(text[i..end]);
                    i = end + 1;
                    break;
                case '"':
                    if (!ReadDoubleQuoted(text, ref i, words)) return null;
                    break;
                case '$' when i < text.Length && text[i] == '\'':
                    i++;
                    if (!ReadAnsiQuoted(text, ref i, words)) return null;
                    break;
                case '\\':
                    if (SkipLineEnd(text, ref i) || i == text.Length) break;
                    // A shell would drop this one too, but only a Windows path has it, and would lose where its folders end.
                    if (char.IsAsciiLetterOrDigit(text[i])) words.Add('\\');
                    else words.Add(text[i++]);
                    break;
                default:
                    if (char.IsWhiteSpace(c)) words.End();
                    else words.Add(c);
                    break;
            }
        }
        words.End();
        return words.Words;
    }

    static bool ReadDoubleQuoted(string text, ref int i, WordList words)
    {
        words.Quoted();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '"') return true;
            if (c != '\\' || i == text.Length) words.Add(c);
            else if (SkipLineEnd(text, ref i)) continue;
            else if (text[i] is '"' or '\\' or '$' or '`') words.Add(text[i++]);
            else words.Add('\\');
        }
        return false;
    }

    /// <summary>$'...', which both browsers write for a quote, a "!" or anything outside plain ASCII.</summary>
    static bool ReadAnsiQuoted(string text, ref int i, WordList words)
    {
        words.Quoted();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '\'') return true;
            if (c != '\\' || i == text.Length)
            {
                words.Add(c);
                continue;
            }
            char escape = text[i++];
            switch (escape)
            {
                case 'a': words.Add('\a'); break;
                case 'b': words.Add('\b'); break;
                case 'e' or 'E': words.Add('\x1b'); break;
                case 'f': words.Add('\f'); break;
                case 'n': words.Add('\n'); break;
                case 'r': words.Add('\r'); break;
                case 't': words.Add('\t'); break;
                case 'v': words.Add('\v'); break;
                case '\\' or '\'' or '"' or '?': words.Add(escape); break;
                case 'x': ReadCharacter(text, ref i, 16, 2, escape, words); break;
                case 'u': ReadCharacter(text, ref i, 16, 4, escape, words); break;
                case 'U': ReadCharacter(text, ref i, 16, 8, escape, words); break;
                case >= '0' and <= '7':
                    i--;
                    ReadCharacter(text, ref i, 8, 3, escape, words);
                    break;
                default:
                    words.Add('\\');
                    words.Add(escape);
                    break;
            }
        }
        return false;
    }

    /// <summary>A character given by its number. Half of a pair comes through as it is, and joins the other half.</summary>
    static void ReadCharacter(string text, ref int i, int numberBase, int maxDigits, char escape, WordList words)
    {
        long code = 0;
        int digits = 0;
        while (digits < maxDigits && i < text.Length && DigitValue(text[i]) is int digit && digit < numberBase)
        {
            code = code * numberBase + digit;
            digits++;
            i++;
        }
        if (digits == 0)
        {
            words.Add('\\');
            words.Add(escape);
        }
        else if (code <= char.MaxValue) words.Add((char)code);
        else if (code <= 0x10FFFF) words.Add(char.ConvertFromUtf32((int)code));
    }

    static int? DigitValue(char c) => char.IsAsciiHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : null;

    /// <summary>
    /// The words of a command for cmd: first cmd's own pass, which takes away the carets, then the C runtime's,
    /// which splits the words. Null if a quote is left open.
    /// </summary>
    static List<Word>? CmdWords(string command)
    {
        var text = WithoutCarets(command);
        var words = new WordList();
        bool quoted = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '\\')
            {
                int run = 1;
                while (i < text.Length && text[i] == '\\')
                {
                    run++;
                    i++;
                }
                bool beforeQuote = i < text.Length && text[i] == '"';
                // The browsers double every backslash, whatever follows it.
                words.Add(new string('\\', beforeQuote ? run / 2 : (run + 1) / 2));
                if (beforeQuote && run % 2 == 1)
                {
                    words.Add('"');
                    i++;
                }
            }
            else if (c == '"')
            {
                if (quoted && i < text.Length && text[i] == '"')
                {
                    words.Add('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                    words.Quoted();
                }
            }
            else if (!quoted && char.IsWhiteSpace(c)) words.End();
            else words.Add(c);
        }
        if (quoted) return null;
        words.End();
        return words.Words;
    }

    /// <summary>A caret lets the next character through. Before a line end it joins the lines, unless the next line is empty: that's a line break in a value.</summary>
    static string WithoutCarets(string text)
    {
        var plain = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i++];
            if (c != '^') plain.Append(c);
            else if (SkipLineEnd(text, ref i))
            {
                if (SkipLineEnd(text, ref i)) plain.Append('\n');
            }
            else if (i < text.Length) plain.Append(text[i++]);
        }
        return plain.ToString();
    }
}
