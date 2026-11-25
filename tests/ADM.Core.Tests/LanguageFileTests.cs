using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADM.Core.Tests;

/// <summary>Reading language files, and checks on the app's files: every key it names exists, and every text gets the arguments it needs.</summary>
public class LanguageFileTests
{
    static readonly string AppFolder = Path.Combine(RepoRoot(), "src", "ADM.App");

    static readonly LanguageFile English = LanguageFile.Load("""
        {
            "_name": "English",
            "_code": "en",
            "ok": "OK",
            "cancel": "Cancel",
            "progress": "{0} of {1}",
            "size": "{0:N0} bytes"
        }
        """);

    static LanguageFile Translation(string body) =>
        LanguageFile.Load("{ \"_name\": \"Test\", \"_code\": \"xx\", " + body + " }", English);

    static string Json(string text) => JsonSerializer.Serialize(text);

    // ---- Reading a file

    [Fact]
    public void Reads_its_name_and_code()
    {
        var file = Translation("\"ok\": \"Bien\"");

        Assert.Equal("Test", file.Name);
        Assert.Equal("xx", file.Code);
    }

    [Fact]
    public void A_missing_name_shows_the_code_and_not_the_name_of_english()
    {
        var file = LanguageFile.Load("""{ "_code": "xx" }""", English);

        Assert.Equal("xx", file.Name);
    }

    [Fact]
    public void A_translated_text_replaces_english()
    {
        Assert.Equal("Bien", Translation("\"ok\": \"Bien\"").Text("ok"));
    }

    [Fact]
    public void Missing_keys_fall_back_to_english()
    {
        Assert.Equal("Cancel", Translation("\"ok\": \"Bien\"").Text("cancel"));
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{1}")]
    [InlineData("{0} {1} {2}")]
    [InlineData("{0} de {x}")]
    [InlineData("nothing")]
    public void Mismatched_placeholders_fall_back_to_english(string text)
    {
        Assert.Equal("{0} of {1}", Translation("\"progress\": " + Json(text)).Text("progress"));
    }

    [Fact]
    public void A_changed_format_falls_back_to_english()
    {
        // "Q" is no number format, so string.Format would throw on it.
        Assert.Equal("{0:N0} bytes", Translation("\"size\": " + Json("{0:Q} octets")).Text("size"));
    }

    [Fact]
    public void A_placeholder_added_to_a_text_without_any_falls_back_to_english()
    {
        Assert.Equal("OK", Translation("\"ok\": " + Json("{0}")).Text("ok"));
    }

    [Theory]
    [InlineData("{0} de {1")]
    [InlineData("{0} de {1}}")]
    [InlineData("{{0} de {1}")]
    [InlineData("{0} de {1} {")]
    public void A_text_string_format_would_refuse_falls_back_to_english(string text)
    {
        Assert.Equal("1 of 2", Translation("\"progress\": " + Json(text)).Text("progress", 1, 2));
    }

    [Fact]
    public void Placeholders_can_change_places_and_repeat()
    {
        Assert.Equal("2 sur 1, 2", Translation("\"progress\": " + Json("{1} sur {0}, {1}")).Text("progress", 1, 2));
    }

    [Fact]
    public void Doubled_braces_are_not_placeholders()
    {
        Assert.Equal("{1} de 2", Translation("\"progress\": " + Json("{{{0}}} de {1}")).Text("progress", 1, 2));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"Bien\"]")]
    [InlineData("{ \"text\": \"Bien\" }")]
    public void A_value_that_is_no_text_falls_back_to_english(string value)
    {
        Assert.Equal("OK", Translation("\"ok\": " + value).Text("ok"));
    }

    [Fact]
    public void Keys_english_lacks_are_dropped()
    {
        var file = Translation("\"gone\": \"{9}\", \"ok\": \"Bien\"");

        Assert.Equal("gone", file.Text("gone"));
        Assert.DoesNotContain("gone", file.Keys);
    }

    [Fact]
    public void Names_starting_with_an_underscore_are_not_texts()
    {
        var file = LanguageFile.Load("""{ "_name": "English", "_code": "en", "_note": "by hand", "ok": "OK" }""");

        Assert.Equal(new[] { "ok" }, file.Keys);
    }

    [Fact]
    public void A_file_edited_by_hand_may_have_comments_and_a_trailing_comma()
    {
        var file = LanguageFile.Load("""
            {
                // The buttons.
                "_code": "xx",
                "ok": "Bien",
            }
            """, English);

        Assert.Equal("Bien", file.Text("ok"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "_name": "No code" }""")]
    [InlineData("""{ "_code": 5 }""")]
    [InlineData("""{ "_code": "  " }""")]
    public void A_file_that_cannot_be_used_is_refused(string json)
    {
        Assert.ThrowsAny<JsonException>(() => LanguageFile.Load(json, English));
    }

    [Fact]
    public void An_unknown_key_shows_the_key()
    {
        Assert.Equal("no.such.key", English.Text("no.such.key"));
    }

    [Fact]
    public void A_text_without_arguments_comes_back_untouched()
    {
        Assert.Equal("{0} of {1}", English.Text("progress"));
    }

    [Fact]
    public void Arguments_are_filled_in()
    {
        Assert.Equal("1 of 2", English.Text("progress", 1, 2));
    }

    // ---- The setting

    [Fact]
    public void Older_state_loads_english()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 4 } }""");
        try
        {
            Assert.Equal("en", Store.Load(path).Settings.Language);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void The_language_survives_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var state = new AppState();
            state.Settings.Language = "xx";

            Store.Save(path, state);

            Assert.Equal("xx", Store.Load(path).Settings.Language);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // ---- The engine's status words

    static readonly StatusWords Marked = new()
    {
        Queued = "[queued]", Downloading = "[downloading]", Recording = "[recording]", Paused = "[paused]", Completed = "[completed]",
        Checking = "[checking]", Scanning = "[scanning]", ErrorOf = "[error {0}]", RetryingOf = "[retrying {0}]", PausedOf = "[paused {0}]",
        LastComplete = "[last complete]", LastFailed = "[last failed]", LastWaiting = "[last waiting]", ResumeYes = "[yes]", ResumeNo = "[no]",
        ReconnectingOf = "[reconnecting {0}]",
    };

    [Theory]
    [InlineData(DownloadStatus.Queued, null, "Queued", "[queued]")]
    [InlineData(DownloadStatus.Queued, "lost", "Retrying: lost", "[retrying lost]")]
    [InlineData(DownloadStatus.Downloading, null, "Downloading", "[downloading]")]
    [InlineData(DownloadStatus.Paused, null, "Paused", "[paused]")]
    [InlineData(DownloadStatus.Paused, "lost", "Paused: lost", "[paused lost]")]
    [InlineData(DownloadStatus.Completed, null, "Completed", "[completed]")]
    [InlineData(DownloadStatus.Failed, "lost", "Error: lost", "[error lost]")]
    public void A_status_reads_as_before_and_can_be_translated(DownloadStatus status, string? error, string english, string translated)
    {
        var item = new DownloadItem { Status = status, Error = error };

        Assert.Equal(english, new StatusWords().StatusOf(item));
        Assert.Equal(translated, Marked.StatusOf(item));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, "Complete", "[last complete]")]
    [InlineData(DownloadStatus.Failed, "Failed", "[last failed]")]
    [InlineData(DownloadStatus.Queued, "Waiting in the queue", "[last waiting]")]
    [InlineData(DownloadStatus.Paused, "Paused", "[paused]")]
    public void The_last_result_reads_as_before_and_can_be_translated(DownloadStatus status, string english, string translated)
    {
        var item = new DownloadItem { Status = status };

        Assert.Equal(english, new StatusWords().LastResultOf(item));
        Assert.Equal(translated, Marked.LastResultOf(item));
    }

    [Theory]
    [InlineData(true, true, "Yes", "[yes]")]
    [InlineData(false, true, "No", "[no]")]
    [InlineData(false, false, "", "")]
    public void The_resume_column_reads_as_before_and_can_be_translated(bool supported, bool known, string english, string translated)
    {
        var item = new DownloadItem { ResumeSupported = supported, ResumeKnown = known };

        Assert.Equal(english, new StatusWords().ResumeOf(item));
        Assert.Equal(translated, Marked.ResumeOf(item));
    }

    [Fact]
    public void A_reconnecting_note_takes_its_number()
    {
        Assert.Equal("Reconnecting (3)", new StatusWords().Reconnecting(3));
        Assert.Equal("[reconnecting 3]", Marked.Reconnecting(3));
    }

    // ---- The scans, on made-up files

    [Fact]
    public void The_scan_finds_keys_in_xaml()
    {
        var keys = KeysInXaml("""
            <Button Content="{l:T common.ok}" ToolTip="{l:T Key=common.tip}" />
            <TextBlock Text="{l:T  spaced.key }" />
            <TextBlock Text="plain" Tag="{Binding T}" />
            """);

        Assert.Equal(new[] { "common.ok", "common.tip", "spaced.key" }, keys);
    }

    [Fact]
    public void The_scan_finds_calls_in_code_and_counts_their_arguments()
    {
        var calls = CallsInCode("""
            var a = Lang.T("one");
            var b = Lang.T("two", name);
            var c = Lang.T("three", Math.Max(1, 2), Pick("a,b", ')'), new[] { 1, 2 }[0]);
            var d = Lang.T(
                "four",
                x);
            var e = Lang.T(key);
            var f = Other.T("not this");
            """);

        Assert.Equal(new[] { ("one", 0), ("two", 1), ("three", 3), ("four", 1), ((string?)null, 0) }.Select(c => (c.Item1, c.Item2)), calls);
    }

    // ---- The app's files

    [Fact]
    public void English_file_reads_and_is_called_english()
    {
        var english = AppEnglish();

        Assert.Equal("en", english.Code);
        Assert.Equal("English", english.Name);
    }

    [Fact]
    public void English_texts_are_all_format_strings()
    {
        var english = AppEnglish();
        foreach (var key in english.Keys)
        {
            var refused = Record.Exception(() => CompositeFormat.Parse(english.Text(key)));
            Assert.True(refused == null, $"{key}: {english.Text(key)}");
        }
    }

    [Fact]
    public void Every_key_the_app_uses_is_in_english()
    {
        var english = AppEnglish().Keys.ToHashSet();
        Assert.True(KeysTheAppUses().Count() > 500, "the scan found too few keys to be reading the app");
        foreach (var (file, key) in KeysTheAppUses())
            Assert.True(english.Contains(key), $"{Path.GetFileName(file)} names {key}, which en.json lacks");
    }

    [Fact]
    public void English_has_no_unused_keys()
    {
        var used = KeysTheAppUses().Select(k => k.Key).ToHashSet();
        var unused = AppEnglish().Keys.Where(key => !used.Contains(key)).ToList();
        Assert.True(unused.Count == 0, string.Join(", ", unused));
    }

    [Fact]
    public void Code_writes_its_keys_out()
    {
        // A key worked out while running can't be checked here, nor can a translator find it.
        foreach (var file in AppFiles("*.cs"))
            foreach (var (key, _) in CallsInCode(File.ReadAllText(file)))
                Assert.True(key != null, $"{Path.GetFileName(file)} calls Lang.T with something other than a written-out key");
    }

    [Fact]
    public void Every_call_passes_the_arguments_its_text_needs()
    {
        var english = AppEnglish();
        // Lang.cs hands the status words on unfilled, for the engine to fill in.
        foreach (var file in AppFiles("*.cs").Where(f => Path.GetFileName(f) != "Lang.cs"))
        {
            foreach (var (key, count) in CallsInCode(File.ReadAllText(file)))
            {
                if (key == null || !english.Keys.Contains(key)) continue;
                var needed = CompositeFormat.Parse(english.Text(key)).MinimumArgumentCount;
                // With no arguments a text comes back unformatted, and its placeholders would show.
                Assert.True(needed == count, $"{Path.GetFileName(file)}: {key} needs {needed} arguments and is given {count}");
            }
        }
    }

    [Fact]
    public void Xaml_names_only_texts_without_placeholders()
    {
        var english = AppEnglish();
        foreach (var file in AppFiles("*.xaml"))
        {
            // A binding's StringFormat is the one place that can.
            var source = Regex.Replace(File.ReadAllText(file), @"StringFormat=""\{l:T [^}]*\}""", "");
            foreach (var key in KeysInXaml(source).Where(english.Keys.Contains))
                Assert.True(CompositeFormat.Parse(english.Text(key)).MinimumArgumentCount == 0,
                    $"{Path.GetFileName(file)}: {key} has placeholders, which XAML can't fill in");
        }
    }

    // ---- Helpers

    static LanguageFile AppEnglish() =>
        LanguageFile.Load(File.ReadAllText(Path.Combine(AppFolder, "Languages", "en.json")));

    static IEnumerable<(string File, string Key)> KeysTheAppUses()
    {
        foreach (var file in AppFiles("*.xaml"))
            foreach (var key in KeysInXaml(File.ReadAllText(file)))
                yield return (file, key);
        foreach (var file in AppFiles("*.cs"))
            foreach (var (key, _) in CallsInCode(File.ReadAllText(file)))
                if (key != null) yield return (file, key);
    }

    static IEnumerable<string> AppFiles(string pattern) =>
        Directory.GetFiles(AppFolder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    static List<string> KeysInXaml(string xaml) =>
        Regex.Matches(xaml, @"\{l:T\s+(?:Key=)?([^\s,}]+)\s*\}").Select(m => m.Groups[1].Value).ToList();

    /// <summary>Each Lang.T call: its key when written out as a string, and how many arguments follow it.</summary>
    static List<(string? Key, int Arguments)> CallsInCode(string code)
    {
        var calls = new List<(string?, int)>();
        foreach (Match call in Regex.Matches(code, @"\bLang\.T\("))
        {
            var start = call.Index + call.Length;
            var key = Regex.Match(code[start..], @"^\s*""([^""]+)""\s*[,)]");
            calls.Add((key.Success ? key.Groups[1].Value : null, CommasAtTopLevel(code, start)));
        }
        return calls;
    }

    /// <summary>Walks from just inside a call's opening bracket to its closing one.</summary>
    static int CommasAtTopLevel(string code, int start)
    {
        var (depth, commas) = (0, 0);
        for (var i = start; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"' or '\'':
                    var quote = code[i];
                    for (i++; code[i] != quote; i++)
                        if (code[i] == '\\') i++;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth == 0) return commas;
                    depth--;
                    break;
                case ',':
                    if (depth == 0) commas++;
                    break;
            }
        }
        throw new InvalidOperationException("A Lang.T call never closes");
    }

    static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "ADM.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ADM.sln not found above the test folder");
    }
}
