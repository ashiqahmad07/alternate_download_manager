using System.Runtime.InteropServices;
using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>IDM's export file, both ways, and the connection types.</summary>
public sealed class ExportFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public ExportFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(_dir, "list.ef2");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [DllImport("kernel32.dll")]
    static extern int GetACP();

    [Fact]
    public void Parses_entries_with_their_headers()
    {
        var parsed = ExportFile.Parse(
            "<\r\nhttps://a.example/f.zip\r\nreferer: https://a.example/page\r\ncookie: id=7; k=a:b\r\nUser-Agent: Agent/1.0\r\n>\r\n" +
            "<\r\nftp://b.example/pub/g.iso\r\n>\r\n");

        Assert.Equal(0, parsed.Skipped);
        Assert.Equal(2, parsed.Requests.Count);
        var first = parsed.Requests[0];
        Assert.Equal("https://a.example/f.zip", first.Url);
        Assert.Equal("https://a.example/page", first.Referrer);
        Assert.Equal("id=7; k=a:b", first.Cookies);
        Assert.Equal("Agent/1.0", first.UserAgent);
        var second = parsed.Requests[1];
        Assert.Equal("ftp://b.example/pub/g.iso", second.Url);
        Assert.Null(second.Referrer);
        Assert.Null(second.Cookies);
        Assert.Null(second.UserAgent);
    }

    [Theory]
    [InlineData("<\r\nhttps://a.example/form\r\npd: q=1&r=2\r\n>\r\n")]
    [InlineData("<\r\nhttps://a.example/form\r\nPD: q=1\r\nreferer: https://a.example/\r\n>\r\n")]
    [InlineData("<\r\nmailto:a@b.example\r\n>\r\n")]
    [InlineData("<\r\nfile:///C:/Windows/win.ini\r\n>\r\n")]
    [InlineData("<\r\nnot an address\r\n>\r\n")]
    [InlineData("<\r\n>\r\n")]
    [InlineData("<\r\nhttp://good.example\\@evil.example/f.zip\r\n>\r\n")]
    public void Skips_and_counts_posted_and_unsupported_entries(string entry)
    {
        var parsed = ExportFile.Parse("<\r\nhttps://a.example/kept.zip\r\n>\r\n" + entry);

        Assert.Equal(1, parsed.Skipped);
        Assert.Equal("https://a.example/kept.zip", Assert.Single(parsed.Requests).Url);
    }

    [Fact]
    public void A_file_of_only_skipped_entries_is_still_an_export_file()
    {
        var parsed = ExportFile.Parse("<\nhttps://a.example/form\npd: q=1\n>\n");

        Assert.Empty(parsed.Requests);
        Assert.Equal(1, parsed.Skipped);
    }

    [Fact]
    public void Accepts_lf_and_unknown_headers()
    {
        var parsed = ExportFile.Parse("<\nhttps://a.example/f.zip\nX-Other: 1\nnot a header\nREFERER:https://a.example/\n>");

        var request = Assert.Single(parsed.Requests);
        Assert.Equal("https://a.example/f.zip", request.Url);
        Assert.Equal("https://a.example/", request.Referrer);
        Assert.Null(request.Cookies);
    }

    [Fact]
    public void Empty_headers_count_as_absent()
    {
        var request = Assert.Single(ExportFile.Parse("<\nhttps://a.example/f\nreferer: \ncookie:\nUser-Agent:   \n>\n").Requests);

        Assert.Null(request.Referrer);
        Assert.Null(request.Cookies);
        Assert.Null(request.UserAgent);
    }

    [Fact]
    public void A_bare_carriage_return_ends_a_line_so_it_cannot_reach_a_header()
    {
        var request = Assert.Single(ExportFile.Parse("<\rhttps://a.example/f\rcookie: a=1\rX-Injected: 1\r>\r").Requests);

        Assert.Equal("a=1", request.Cookies);
    }

    [Fact]
    public void An_entry_with_no_end_is_skipped_and_the_next_still_reads()
    {
        var parsed = ExportFile.Parse("<\nhttps://a.example/cut.zip\nreferer: x\n<\nhttps://a.example/whole.zip\n>\n<\nhttps://a.example/end.zip\n");

        Assert.Equal("https://a.example/whole.zip", Assert.Single(parsed.Requests).Url);
        Assert.Equal(2, parsed.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some text\r\nhttps://a.example/f.zip\r\n")]
    [InlineData("<\r\nhttps://a.example/cut-off.zip\r\n")]
    [InlineData(">\r\n")]
    public void Rejects_text_that_is_not_an_export_file(string text)
    {
        var e = Assert.Throws<FormatException>(() => ExportFile.Parse(text));
        Assert.Equal("This isn't an IDM export file, or the file is damaged.", e.Message);
    }

    [Fact]
    public void Reads_ansi_and_utf8()
    {
        const string Utf8Text = "<\r\nhttps://a.example/caf\u00e9.zip\r\n>\r\n";
        Assert.Equal(Utf8Text, ExportFile.ReadText(WriteBytes(new UTF8Encoding(false).GetBytes(Utf8Text))));
        // A byte order mark is not text.
        Assert.Equal(Utf8Text, ExportFile.ReadText(WriteBytes(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(Utf8Text)).ToArray())));

        // 0xE9 alone isn't valid UTF-8, so the file is the Windows code page's: an accented e on Western Windows.
        var ansiBytes = "<\r\nhttps://a.example/caf"u8.ToArray().Append((byte)0xE9).Concat(".zip\r\n>\r\n"u8.ToArray()).ToArray();
        var ansi = CodePagesEncodingProvider.Instance.GetEncoding(GetACP()) ?? Encoding.Latin1;
        var text = ExportFile.ReadText(WriteBytes(ansiBytes));
        Assert.Equal(ansi.GetString(ansiBytes), text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Single(ExportFile.Parse(text).Requests);
    }

    [Fact]
    public void Written_file_reads_back()
    {
        var text = ExportFile.Write(new[]
        {
            new DownloadItem { Url = "https://a.example/f.zip", Referrer = "https://a.example/page", UserAgent = "Agent/1.0" },
            new DownloadItem { Url = "ftp://b.example/pub/g.iso" },
        });

        Assert.Equal(
            "<\r\nhttps://a.example/f.zip\r\nreferer: https://a.example/page\r\nUser-Agent: Agent/1.0\r\n>\r\n" +
            "<\r\nftp://b.example/pub/g.iso\r\n>\r\n",
            text);
        var parsed = ExportFile.Parse(text);
        Assert.Equal(0, parsed.Skipped);
        Assert.Equal(new[] { "https://a.example/f.zip", "ftp://b.example/pub/g.iso" }, parsed.Requests.Select(r => r.Url));
        Assert.Equal("https://a.example/page", parsed.Requests[0].Referrer);
        Assert.Equal("Agent/1.0", parsed.Requests[0].UserAgent);
    }

    [Fact]
    public void The_default_user_agent_is_left_out()
    {
        var text = ExportFile.Write(new[] { new DownloadItem { Url = "https://a.example/f", UserAgent = Settings.DefaultUserAgent } });

        Assert.DoesNotContain("User-Agent", text);
    }

    [Fact]
    public void Export_leaves_out_cookies_and_logins()
    {
        var text = ExportFile.Write(new[]
        {
            new DownloadItem
            {
                Url = "ftp://secretuser:secretword@a.example/f.zip",
                Referrer = "https://pageuser:pageword@a.example/page",
                Cookies = "session=secretcookie",
                UserName = "secretname",
                ProtectedPassword = Secrets.Protect("secretpassword"),
            },
        });

        foreach (var secret in new[] { "secret", "pageuser", "pageword", "cookie" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(ExportFile.Parse(text).Requests);
        Assert.Equal("ftp://a.example/f.zip", request.Url);
        Assert.Equal("https://a.example/page", request.Referrer);
        Assert.Null(request.Cookies);
    }

    [Fact]
    public void Written_address_stays_as_given_so_importing_it_finds_the_same_download()
    {
        // System.Uri would add the slash and escape the rest.
        const string Url = "https://a.example?q=caf\u00e9 x";

        Assert.Equal(Url, Assert.Single(ExportFile.Parse(ExportFile.Write(new[] { new DownloadItem { Url = Url } })).Requests).Url);
    }

    [Fact]
    public void A_referer_that_is_not_an_address_is_left_out()
    {
        var text = ExportFile.Write(new[] { new DownloadItem { Url = "https://a.example/f", Referrer = "user:p@ss@[broken" } });

        Assert.DoesNotContain("referer", text);
    }

    [Theory]
    [InlineData("https://a.example/page\r\ncookie: planted=1")]
    [InlineData("https://a.example/page\ncookie: planted=1")]
    [InlineData("https://a.example/page\r>\r<\rhttps://evil.example/planted.exe")]
    public void A_line_break_in_a_value_cannot_add_lines_to_the_file(string value)
    {
        var text = ExportFile.Write(new[]
        {
            new DownloadItem { Url = "https://a.example/f.zip", Referrer = value, UserAgent = value },
            new DownloadItem { Url = value },
            new DownloadItem { Url = "https://a.example/g.zip" },
        });

        var parsed = ExportFile.Parse(text);
        Assert.Equal(new[] { "https://a.example/f.zip", "https://a.example/g.zip" }, parsed.Requests.Select(r => r.Url));
        Assert.All(parsed.Requests, r => Assert.Null(r.Cookies));
        Assert.DoesNotContain("planted", text);
    }

    [Theory]
    [InlineData(ConnectionType.Low, 2)]
    [InlineData(ConnectionType.Medium, 8)]
    [InlineData(ConnectionType.High, 16)]
    [InlineData(ConnectionType.Custom, null)]
    public void Connection_types_set_connections(ConnectionType type, int? connections) =>
        Assert.Equal(connections, ConnectionPresets.Connections(type));

    [Fact]
    public void Older_settings_stay_custom_and_keep_their_number()
    {
        Assert.Equal(ConnectionType.Custom, new Settings().ConnectionType);

        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 5 } }""");
        var loaded = Store.Load(path).Settings;
        Assert.Equal(ConnectionType.Custom, loaded.ConnectionType);
        Assert.Equal(5, loaded.DefaultConnections);

        Store.Save(path, new AppState { Settings = new Settings { ConnectionType = ConnectionType.High, DefaultConnections = 16 } });
        Assert.Equal(ConnectionType.High, Store.Load(path).Settings.ConnectionType);
    }
}
