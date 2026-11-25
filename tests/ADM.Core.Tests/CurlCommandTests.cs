using System.Runtime.CompilerServices;
using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

// tests/fixtures/curl/ holds made-up commands in the forms the browsers' Copy as cURL writes:
// devtools/client/shared/curl.js in Firefox, and generateCurlCommand in Chrome's DevTools.
public sealed class CurlCommandTests : IDisposable
{
    const string FixtureAddress = "https://files.example.com/pack/archive.zip?token=abc%20def&part=1";
    const string ChromeAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    const string FirefoxAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0";
    const string Link = "https://a.example/f.zip";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public CurlCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "curl", name));

    static DownloadRequest Read(string command)
    {
        var result = CurlCommand.Parse(command);
        Assert.Null(result.Error);
        return result.Request!;
    }

    static void AssertFixtureRead(string name, string userAgent)
    {
        // A checkout can give the file either line end, and a clipboard can too.
        foreach (var lineEnd in new[] { "\n", "\r\n" })
        {
            var result = CurlCommand.Parse(Fixture(name).ReplaceLineEndings(lineEnd));

            Assert.Null(result.Error);
            var request = result.Request!;
            Assert.Equal(FixtureAddress, request.Url);
            Assert.Equal("session=abc123; theme=dark", request.Cookies);
            Assert.Equal("https://files.example.com/pack/", request.Referrer);
            Assert.Equal(userAgent, request.UserAgent);
            // Each fixture carries five headers a download has no place for.
            Assert.Equal(5, result.HeadersLeftOut);
        }
    }

    // ---- The browsers' forms

    [Fact]
    public void Chrome_bash_command_is_read() =>
        AssertFixtureRead("chrome-bash.txt", ChromeAgent);

    [Fact]
    public void Chrome_cmd_command_is_read() =>
        AssertFixtureRead("chrome-cmd.txt", ChromeAgent);

    [Theory]
    [InlineData("firefox-posix.txt")]
    [InlineData("firefox-windows.txt")]
    public void Firefox_command_is_read(string fixture) =>
        AssertFixtureRead(fixture, FirefoxAgent);

    // ---- Quoting

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Line_ends_are_joined(string lineEnd)
    {
        var posix = Read($"curl '{Link}' \\{lineEnd}  -e 'https://b.example/' \\{lineEnd}  -A 'Agent'");
        var cmd = Read($"curl ^\"{Link}^\" ^{lineEnd}  -e ^\"https://b.example/^\" ^{lineEnd}  -A ^\"Agent^\"");

        foreach (var request in new[] { posix, cmd })
        {
            Assert.Equal(Link, request.Url);
            Assert.Equal("https://b.example/", request.Referrer);
            Assert.Equal("Agent", request.UserAgent);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Line_ends_join_the_middle_of_a_word(string lineEnd)
    {
        Assert.Equal(Link, Read($"curl https://a.example/f\\{lineEnd}.zip").Url);
        Assert.Equal(Link, Read($"curl https://a.example/f^{lineEnd}.zip").Url);
    }

    [Theory]
    // ANSI-C, as both browsers write it for a quote, a "!" or a character outside ASCII.
    [InlineData(@"-A $'it\'s \041\x41\u00e9\\ \"" x'", "it's !A\u00e9\\ \" x")]
    // Double quotes keep a backslash unless it escapes what they would act on.
    [InlineData(@"-A ""say \""hi\"" \$x \\ \q""", "say \"hi\" $x \\ \\q")]
    // Single quotes keep everything.
    [InlineData(@"-A 'plain\n $x'", @"plain\n $x")]
    // Outside quotes.
    [InlineData(@"-A two\ words", "two words")]
    // Cmd: a caret lets the next character through, and the C runtime's rules apply after that.
    [InlineData(@"-A ^""a \""b\"" c\\d ^& e^%^f^""", "a \"b\" c\\d & e%f")]
    public void Escapes_are_undone(string option, string userAgent)
    {
        var request = Read($"curl {option} {Link}");

        Assert.Equal(userAgent, request.UserAgent);
        Assert.Equal(Link, request.Url);
    }

    [Fact]
    public void Doubled_quotes_are_one_quote_in_cmd()
    {
        Assert.Equal("a\"b", Read($"curl ^\"{Link}^\" -A \"a\"\"b\"").UserAgent);
    }

    [Theory]
    [InlineData("curl 'https://a.example/f.zip")]
    [InlineData("curl \"https://a.example/f.zip")]
    [InlineData("curl $'https://a.example/f.zip")]
    [InlineData("curl ^\"https://a.example/f.zip")]
    [InlineData("curl 'https://a.example/f.zip' -H 'Cookie: a=b")]
    public void Open_quote_is_an_error(string command)
    {
        var result = CurlCommand.Parse(command);

        Assert.Null(result.Request);
        Assert.Equal(CurlCommand.OpenQuote, result.Error);
    }

    [Fact]
    public void Shell_text_stays_as_written()
    {
        var request = Read($"curl '{Link}' -A \"$(calc) `calc` $HOME\"");

        Assert.Equal("$(calc) `calc` $HOME", request.UserAgent);
    }

    // ---- What is a command

    [Theory]
    [InlineData("curly.zip")]
    [InlineData("curly fries")]
    [InlineData("curl")]
    [InlineData("https://a.example/curl file.zip")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invoke-WebRequest -UseBasicParsing -Uri \"https://a.example/f.zip\"")]
    public void Other_text_is_not_a_command(string text) => Assert.False(CurlCommand.IsCommand(text));

    [Theory]
    [InlineData("curl https://a.example/f.zip")]
    [InlineData("curl.exe ^\"https://a.example/f.zip^\"")]
    [InlineData("  curl 'https://a.example/f.zip'")]
    public void Curl_and_curl_exe_are_commands(string text) => Assert.True(CurlCommand.IsCommand(text));

    // ---- What is refused

    [Theory]
    [InlineData("-d a=b")]
    [InlineData("-da=b")]
    [InlineData("-sd a=b")]
    [InlineData("--data a=b")]
    [InlineData("--data-raw a=b")]
    [InlineData("--data-binary a=b")]
    [InlineData("--data-urlencode a=b")]
    [InlineData("--json {}")]
    [InlineData("-F a=b")]
    [InlineData("--form a=b")]
    [InlineData("--form-string a=b")]
    [InlineData("-T up.bin")]
    [InlineData("--upload-file up.bin")]
    [InlineData("-X POST")]
    [InlineData("-XPOST")]
    [InlineData("-sX POST")]
    [InlineData("--request POST")]
    [InlineData("-X 'DELETE'")]
    public void Post_is_refused(string option)
    {
        foreach (var command in new[] { $"curl {option} {Link}", $"curl {Link} {option}" })
        {
            var result = CurlCommand.Parse(command);

            Assert.Null(result.Request);
            Assert.Equal(CurlCommand.CantRequest, result.Error);
        }
    }

    [Theory]
    [InlineData("-X GET")]
    [InlineData("-XGET")]
    [InlineData("--request GET")]
    [InlineData("-X 'GET'")]
    public void Get_passes(string option) => Assert.Equal(Link, Read($"curl {option} {Link}").Url);

    [Theory]
    [InlineData("curl -L")]
    [InlineData("curl file:///c:/secret.txt")]
    [InlineData("curl --url file:///c:/secret.txt")]
    [InlineData("curl javascript:alert(1)")]
    [InlineData("curl a.example/f.zip")]
    public void A_command_with_no_usable_address_is_an_error(string command)
    {
        var result = CurlCommand.Parse(command);

        Assert.Null(result.Request);
        Assert.Equal(CurlCommand.NoAddress, result.Error);
    }

    // ---- The file's name

    [Theory]
    [InlineData(@"-o ..\..\evil.exe")]
    [InlineData(@"-o C:\Windows\evil.exe")]
    [InlineData("-o /etc/evil.exe")]
    [InlineData(@"-o ""..\..\evil.exe""")]
    [InlineData(@"-o 'C:\Windows\evil.exe'")]
    [InlineData("--output ../../evil.exe")]
    [InlineData("-o../evil.exe")]
    [InlineData("-so ../evil.exe")]
    [InlineData(@"-o ""\\server\share\evil.exe""")]
    [InlineData("-o $'..\\\\..\\\\evil.exe'")]
    public void Output_name_cannot_leave_the_folder(string option)
    {
        Assert.Equal("evil.exe", Read($"curl {option} {Link}").FileName);
    }

    [Fact]
    public void Output_name_gets_the_usual_clean_up()
    {
        Assert.Equal("a_b_.zip", Read($"curl -o 'a:b?.zip' {Link}").FileName);
    }

    [Theory]
    [InlineData("-o -")]
    [InlineData("-o ''")]
    [InlineData(@"-o 'folder\'")]
    public void Output_with_no_name_leaves_it_to_the_server(string option)
    {
        Assert.Null(Read($"curl {option} {Link}").FileName);
    }

    // ---- The address

    [Fact]
    public void Unknown_options_are_skipped()
    {
        var request = Read($"curl --made-up --also-made-up value -Z {Link} --insecure --compressed -sSL -e https://b.example/");

        Assert.Equal(Link, request.Url);
        Assert.Equal("https://b.example/", request.Referrer);
    }

    [Theory]
    [InlineData("-H \"Referer: https://a.example/\" -e https://b.example/ https://c.example/file.zip")]
    [InlineData("-x http://proxy.example:8080 https://c.example/file.zip")]
    [InlineData("--proxy http://proxy.example:8080 https://c.example/file.zip")]
    [InlineData("-A https://b.example/ https://c.example/file.zip")]
    [InlineData("--doh-url https://dns.example/ https://c.example/file.zip")]
    [InlineData("-w '%{http_code}' https://c.example/file.zip")]
    [InlineData("-w%{url} https://c.example/file.zip")]
    public void Option_values_are_not_taken_for_the_address(string options)
    {
        var request = Read($"curl {options}");

        Assert.Equal("https://c.example/file.zip", request.Url);
        // Nor for other options, as the letters of -w%{url} would be.
        Assert.Null(request.UserName);
        Assert.Null(request.FileName);
    }

    [Fact]
    public void A_short_option_after_others_takes_its_value()
    {
        var request = Read($"curl -sSLA Agent -eBefore {Link}");

        Assert.Equal("Agent", request.UserAgent);
        Assert.Equal("Before", request.Referrer);
        Assert.Equal(Link, request.Url);
    }

    [Fact]
    public void Url_option_wins_over_a_word()
    {
        Assert.Equal(Link, Read($"curl https://other.example/x --url {Link}").Url);
    }

    [Fact]
    public void Words_after_two_dashes_are_not_options()
    {
        var request = Read($"curl -A Agent -- -e {Link}");

        Assert.Equal(Link, request.Url);
        Assert.Null(request.Referrer);
    }

    [Theory]
    [InlineData(@"curl --url 'https://a.example/x\[1\]\{2\}.zip'")]
    [InlineData(@"curl --url ^""https://a.example/x\[1\]\{2\}.zip^""")]
    public void Brackets_in_an_address_lose_their_backslash(string command)
    {
        // Chrome hides them from curl's own patterns; left in, a backslash would become part of the path.
        Assert.Equal("https://a.example/x[1]{2}.zip", Read(command).Url);
    }

    [Theory]
    [InlineData(" ;\n")]
    [InlineData(" &\r\n")]
    [InlineData(" && ")]
    [InlineData(" | ")]
    [InlineData("\n")]
    public void Only_the_first_of_several_commands_is_read(string between)
    {
        // Copy all as cURL. The second command's cookies and login aren't for the first one's site.
        var request = Read($"curl '{Link}' -b 'first=1'{between}curl 'https://b.example/g.zip' -b 'second=2' -u me:secret");

        Assert.Equal(Link, request.Url);
        Assert.Equal("first=1", request.Cookies);
        Assert.Null(request.UserName);
    }

    // ---- Logins and headers

    static string Basic(string login) => Convert.ToBase64String(Encoding.UTF8.GetBytes(login));

    [Fact]
    public void Basic_authorization_becomes_a_login()
    {
        var result = CurlCommand.Parse($"curl '{Link}' -H 'authorization: Basic {Basic("me:pa:ss")}'");

        Assert.Equal("me", result.Request!.UserName);
        Assert.Equal("pa:ss", result.Request.Password);
        Assert.Equal(0, result.HeadersLeftOut);
    }

    [Theory]
    [InlineData("Bearer abc.def")]
    [InlineData("Bearer bWU6c2VjcmV0")]
    [InlineData("Basic not*base64")]
    [InlineData("Basic")]
    [InlineData("Digest username=\"me\"")]
    public void Other_authorization_is_left_out(string value)
    {
        var result = CurlCommand.Parse($"curl '{Link}' -H 'Authorization: {value}'");

        Assert.Null(result.Request!.UserName);
        Assert.Null(result.Request.Password);
        Assert.Equal(1, result.HeadersLeftOut);
    }

    [Theory]
    [InlineData("-u me:secret", "me", "secret")]
    [InlineData("--user me:se:cret", "me", "se:cret")]
    [InlineData("-ume:secret", "me", "secret")]
    [InlineData("-u me", "me", null)]
    public void User_option_is_a_login(string option, string user, string? password)
    {
        var request = Read($"curl {option} {Link}");

        Assert.Equal(user, request.UserName);
        Assert.Equal(password, request.Password);
    }

    [Fact]
    public void Other_headers_are_counted()
    {
        var result = CurlCommand.Parse($"curl '{Link}' -H 'Accept: */*' --header 'X-Token: abc' -H 'x-empty;' -H 'Referer: https://b.example/' -HRange:bytes=0-");

        Assert.Equal(4, result.HeadersLeftOut);
        Assert.Equal("https://b.example/", result.Request!.Referrer);
    }

    [Fact]
    public void Header_names_are_read_in_any_case()
    {
        var request = Read($"curl '{Link}' -H 'COOKIE: a=1' -H 'reFERer: https://b.example/' -H 'user-AGENT: Agent'");

        Assert.Equal("a=1", request.Cookies);
        Assert.Equal("https://b.example/", request.Referrer);
        Assert.Equal("Agent", request.UserAgent);
    }

    [Fact]
    public void Cookie_file_is_not_taken_as_cookies()
    {
        Assert.Null(Read($"curl -b cookies.txt {Link}").Cookies);
        Assert.Null(Read($"curl --cookie cookies.txt {Link}").Cookies);
        Assert.Equal("a=1", Read($"curl -b 'a=1' {Link}").Cookies);
        Assert.Equal("a=1", Read($"curl --cookie 'a=1' {Link}").Cookies);
    }

    [Fact]
    public void Cookies_from_both_places_are_joined()
    {
        Assert.Equal("a=1; b=2", Read($"curl -H 'Cookie: a=1' -b 'b=2' {Link}").Cookies);
    }

    [Theory]
    [InlineData("-H $'Cookie: a=b\\r\\nX-Evil: 1'", 1)]
    [InlineData("-H $'Referer: https://b.example/\\nX-Evil: 1'", 1)]
    [InlineData("-H $'User-Agent: a\\u0000b'", 1)]
    [InlineData("-b $'a=b\\r\\nX-Evil: 1'", 0)]
    [InlineData("-e $'https://b.example/\\r\\nX-Evil: 1'", 0)]
    [InlineData("-A $'a\\nb'", 0)]
    [InlineData("-u $'me\\r\\nX-Evil: 1:pass'", 0)]
    public void A_line_break_in_a_value_goes_no_further(string option, int leftOut)
    {
        var result = CurlCommand.Parse($"curl '{Link}' {option}");

        var request = result.Request!;
        Assert.Null(request.Cookies);
        Assert.Null(request.Referrer);
        Assert.Null(request.UserAgent);
        Assert.Null(request.UserName);
        Assert.Equal(leftOut, result.HeadersLeftOut);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_line_break_as_cmd_writes_it_goes_no_further(string lineEnd)
    {
        // A caret, then an empty line: how both browsers write a line break in a value for cmd.
        var result = CurlCommand.Parse($"curl ^\"{Link}^\" -H ^\"Cookie: a=b^{lineEnd}{lineEnd}X-Evil: 1^\"");

        Assert.Equal(Link, result.Request!.Url);
        Assert.Null(result.Request.Cookies);
        Assert.Equal(1, result.HeadersLeftOut);
    }

    [Fact]
    public void A_line_break_in_a_basic_login_goes_no_further()
    {
        var result = CurlCommand.Parse($"curl '{Link}' -H 'Authorization: Basic {Basic("me\r\nX-Evil: 1:pass")}'");

        Assert.Null(result.Request!.UserName);
        Assert.Equal(1, result.HeadersLeftOut);
    }

    [Fact]
    public void A_line_break_in_an_address_makes_it_no_address()
    {
        var result = CurlCommand.Parse("curl $'https://a.example/f.zip\\r\\nX-Evil: 1'");

        Assert.Equal(CurlCommand.NoAddress, result.Error);
    }

    [Fact]
    public void Reading_opens_no_file()
    {
        // Each of these makes curl read a file. None of it may be read, wherever it points.
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "a=secretvalue");

        var request = Read($"curl -K \"{secret}\" -b \"@{secret}\" -H \"@{secret}\" -A \"@{secret}\" {Link}");

        Assert.DoesNotContain("secretvalue", request.Cookies ?? "");
        Assert.DoesNotContain("secretvalue", request.UserAgent ?? "");
    }

    // ---- Cookies follow their site

    static DownloadRequest WithCookies(string url = "https://a.example/f.zip") =>
        new() { Url = url, Cookies = "a=1", UserName = "me", Password = "secret", Referrer = "https://r.example/" };

    [Theory]
    [InlineData("https://b.example/f.zip")]
    [InlineData("https://sub.a.example/f.zip")]
    [InlineData("http://a.example/f.zip")]
    [InlineData("https://a.example:8443/f.zip")]
    [InlineData("not an address")]
    [InlineData("")]
    public void Another_site_gets_no_cookies(string address)
    {
        var request = WithCookies();

        var copy = request.ForAddress(address);

        Assert.Null(copy.Cookies);
        Assert.Equal(address, copy.Url);
        // A login stays: the dialog shows it, so the choice is the user's. The original is left as it was.
        Assert.Equal("me", copy.UserName);
        Assert.Equal("secret", copy.Password);
        Assert.Equal("https://r.example/", copy.Referrer);
        Assert.Equal("a=1", request.Cookies);
        Assert.Equal("https://a.example/f.zip", request.Url);
    }

    [Theory]
    [InlineData("https://a.example/other/g.zip?x=1")]
    [InlineData("https://A.EXAMPLE/f.zip")]
    [InlineData("https://a.example:443/f.zip")]
    [InlineData("https://me:secret@a.example/f.zip")]
    public void Same_site_keeps_its_cookies(string address)
    {
        var copy = WithCookies().ForAddress(address);

        Assert.Equal("a=1", copy.Cookies);
        Assert.Equal(address, copy.Url);
    }

    [Fact]
    public void A_request_with_no_address_has_no_site_to_keep_cookies_for()
    {
        Assert.Null(new DownloadRequest { Cookies = "a=1" }.ForAddress(Link).Cookies);
    }

    // ---- Through the download manager

    DownloadManager NewManager()
    {
        var m = new DownloadManager(Path.Combine(_dir, "state.json"), new LockedContext());
        m.State.Settings.DownloadRoot = Path.Combine(_dir, "files");
        return m;
    }

    [Fact]
    public async Task Download_sends_what_the_command_carried()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        var command = CurlCommand.Parse($"curl '{server.BaseUrl}/cookies.bin' -b 'sid=fromcommand' -H 'Referer: https://site.example/page' -H 'User-Agent: CommandAgent' -H 'Accept: */*' -o 'named.bin'");

        var item = m.Add(command.Request!, startNow: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, command.HeadersLeftOut);
        Assert.Contains(server.CookiesSeen, seen => seen.Cookie == "sid=fromcommand");
        Assert.Contains(server.HeadersSeen, seen => seen.Referer == "https://site.example/page" && seen.UserAgent == "CommandAgent");
        Assert.Equal("named.bin", Path.GetFileName(item.FullPath));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public void Password_from_a_command_is_stored_encrypted()
    {
        var m = NewManager();
        var command = CurlCommand.Parse($"curl '{Link}' -u me:plainpassword");

        var item = m.Add(command.Request!, startNow: false);
        m.Save();

        Assert.Equal("me", item.UserName);
        Assert.Equal("plainpassword", Secrets.Unprotect(item.ProtectedPassword));
        Assert.DoesNotContain("plainpassword", File.ReadAllText(Path.Combine(_dir, "state.json")));
    }
}
