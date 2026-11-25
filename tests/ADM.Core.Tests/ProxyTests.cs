using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class ProxyTests : IDisposable
{
    // A name that never resolves: the download can only succeed by going through the proxy.
    const string ViaProxyUrl = "http://adm.invalid/file.bin";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static ProxySettings Manual(ProxyKind kind, string host, int port, string bypass = "localhost 127.0.0.1") =>
        new() { Mode = ProxyMode.Manual, Kind = kind, Host = host, Port = port, Bypass = bypass };

    DownloadItem NewItem(string url) => new() { Url = url, Directory = _dir, FileName = "file.bin" };

    static Task RunAsync(DownloadItem item, Settings settings) =>
        new HttpDownloader(new RateLimiter(), settings, (_, _) => { }).RunAsync(item, CancellationToken.None);

    [Fact]
    public void Manual_proxy_routes_http_and_socks()
    {
        var target = new Uri("https://b.com/f");

        Assert.Equal(new Uri("http://p.local:3128/"), new ProxyRouter(Manual(ProxyKind.Http, "p.local", 3128)).GetProxy(target));
        Assert.Equal(new Uri("socks5://p.local:1080/"), new ProxyRouter(Manual(ProxyKind.Socks5, "p.local", 1080)).GetProxy(target));
    }

    [Fact]
    public void Bypass_list_sends_matching_hosts_direct()
    {
        var router = new ProxyRouter(Manual(ProxyKind.Http, "p.local", 3128, bypass: "*.corp.example"));

        Assert.True(router.IsBypassed(new Uri("https://a.corp.example/f")));
        Assert.False(router.IsBypassed(new Uri("https://b.com/f")));
    }

    [Fact]
    public async Task Downloads_through_a_manual_http_proxy()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var proxy = new Uri(server.FileUrl);
        var item = NewItem(ViaProxyUrl);

        await RunAsync(item, new Settings { Proxy = Manual(ProxyKind.Http, proxy.Host, proxy.Port) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.ProxiedRequests > 0, "no request reached the server through the proxy");
    }

    [Fact]
    public async Task Unreachable_proxy_fails_instead_of_hanging()
    {
        // Port 9 (discard) refuses connections.
        var settings = new Settings { Proxy = Manual(ProxyKind.Http, "127.0.0.1", 9) };

        await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(NewItem(ViaProxyUrl), settings));
    }

    [Fact]
    public async Task Running_download_keeps_the_proxy_it_started_with()
    {
        // A settings change applies to downloads started afterwards; one already running must not
        // switch servers mid-file. DownloadManager.Start creates the downloader as the download starts.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var proxy = new Uri(server.FileUrl);
        var settings = new Settings { Proxy = Manual(ProxyKind.Http, proxy.Host, proxy.Port) };
        var downloader = new HttpDownloader(new RateLimiter(), settings, (_, _) => { });
        var item = NewItem(ViaProxyUrl);

        settings.Proxy = Manual(ProxyKind.Http, "127.0.0.1", 9);
        await downloader.RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public void Clients_are_cached_per_proxy_settings()
    {
        var first = new Settings { Proxy = Manual(ProxyKind.Http, "p.local", 3128) };
        var same = new Settings { Proxy = Manual(ProxyKind.Http, "p.local", 3128) };
        var changed = new Settings { Proxy = Manual(ProxyKind.Http, "p.local", 3129) };

        Assert.Same(Net.ClientFor(first), Net.ClientFor(same));
        Assert.NotSame(Net.ClientFor(first), Net.ClientFor(changed));
    }

    // --- Proxy scripts ---

    static readonly RetryPolicy ShortRetries = new()
    {
        FirstBackoff = TimeSpan.FromMilliseconds(20),
        MaxBackoff = TimeSpan.FromMilliseconds(20),
        ReconnectWindow = TimeSpan.FromMilliseconds(200),
    };

    static Settings WithScript(string url) => new() { Proxy = new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = url } };

    static Task RunShortAsync(DownloadItem item, Settings settings) =>
        new HttpDownloader(new RateLimiter(), settings, (_, _) => { }) { Retry = ShortRetries }
            .RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Script_routes_matching_hosts_through_the_proxy()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(ViaProxyUrl);

        await RunAsync(item, WithScript(server.ProxyScriptUrl));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.ProxiedRequests > 0, "no request reached the server through the proxy");
    }

    [Fact]
    public async Task Script_that_says_direct_goes_direct()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);

        // Going direct, the name can't be found. The script itself was fine.
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(NewItem(ViaProxyUrl), WithScript(server.ScriptSaying("DIRECT"))));

        Assert.NotEqual(PacProxy.LoadFailed, e.Message);
        Assert.Equal(0, server.ProxiedRequests);
    }

    [Fact]
    public async Task Loopback_addresses_go_direct_whatever_the_script_says()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);

        await RunAsync(item, WithScript(server.ScriptSaying("PROXY 127.0.0.1:9")));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Unloadable_script_fails_with_a_clear_message()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunShortAsync(NewItem(server.FileUrl), WithScript(server.MissingScriptUrl)));

        Assert.Equal(PacProxy.LoadFailed, e.Message);
        Assert.Equal(0, server.Hits("/file.bin"));
    }

    [Fact]
    public async Task Unloadable_script_keeps_trying_and_never_goes_direct()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        server.ScriptFailures = 1;
        var item = NewItem(server.FileUrl);
        item.Status = DownloadStatus.Downloading;
        var retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromMilliseconds(200) };
        var run = new HttpDownloader(new RateLimiter(), WithScript(server.LateScriptUrl), (_, _) => { }) { Retry = retry }.RunAsync(item, CancellationToken.None);

        // Windows remembers a script that failed for several seconds, so this takes that long.
        await run.WaitAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.Hits("/late.pac") >= 2, "the script was asked for only once");
        // The server keeps the order, which watching the status note from here can't: the note stays up until the first request is answered.
        Assert.Equal(0, server.FileHitsBeforeLateScript);
    }

    [Fact]
    public async Task Script_that_cannot_run_is_not_waited_on()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(20), ReconnectWindow = TimeSpan.FromHours(1) };

        var e = await Assert.ThrowsAnyAsync<Exception>(() =>
            new HttpDownloader(new RateLimiter(), WithScript(server.BrokenScriptUrl), (_, _) => { }) { Retry = retry }
                .RunAsync(NewItem(ViaProxyUrl), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(PacProxy.LoadFailed, e.Message);
    }

    [Fact]
    public async Task Script_that_cannot_run_fails_and_never_goes_direct()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunShortAsync(NewItem(ViaProxyUrl), WithScript(server.BrokenScriptUrl)));

        // Going direct, this would have said the name can't be found.
        Assert.Equal(PacProxy.LoadFailed, e.Message);
    }

    [Theory]
    [InlineData("NONSENSE 1")]
    [InlineData("PROXY a b c:1")]
    [InlineData("PROXY %%%:1")]
    [InlineData("SOCKS 4 :1")]
    public async Task Script_answer_that_means_nothing_fails_and_never_goes_direct(string says)
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunShortAsync(NewItem(ViaProxyUrl), WithScript(server.ScriptSaying(says))));

        Assert.Equal(PacProxy.LoadFailed, e.Message);
    }

    [Fact]
    public async Task Script_naming_only_a_socks_proxy_goes_through_it()
    {
        // WinHttpGetProxyForUrl answers such a script with "no proxy", as if it had said DIRECT.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        await using var relay = new Socks5TestRelay { ConnectInstead = "127.0.0.1" };
        int port = new Uri(server.BaseUrl).Port;
        var item = NewItem($"http://adm.invalid:{port}/file.bin");

        await RunShortAsync(item, WithScript(server.ScriptSaying($"SOCKS 127.0.0.1:{relay.Port}")));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains($"adm.invalid:{port}", relay.Targets);
    }

    [Fact]
    public async Task Script_that_loads_on_a_later_try_is_used()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        server.ScriptFailures = 1;
        var pac = new PacProxy(WithScript(server.LateScriptUrl).Proxy);
        var address = new Uri(ViaProxyUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => pac.ResolveAsync(address, CancellationToken.None));

        // Windows remembers the failure for some seconds; this is how long a download's retries may have to wait.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (true)
        {
            try
            {
                Assert.Null(await pac.ResolveAsync(address, CancellationToken.None));
                return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    [Theory]
    [InlineData("PROXY a.example:1; SOCKS b.example:2; DIRECT", "http://a.example:1/")]
    [InlineData("SOCKS b.example:2; PROXY a.example:1", "socks5://b.example:2/")]
    [InlineData("SOCKS5 b.example:2", "socks5://b.example:2/")]
    [InlineData("SOCKS4 b.example:2", "socks4://b.example:2/")]
    [InlineData("SOCKS4 10.0.0.1:1080; DIRECT", "socks4://10.0.0.1:1080/")]
    // Windows drops a SOCKS5 entry and would answer with the next.
    [InlineData("SOCKS5 b.example:2; PROXY a.example:1", "socks5://b.example:2/")]
    // Browsers take the keywords in any case, and Windows only in capitals.
    [InlineData("socks b.example:2", "socks5://b.example:2/")]
    [InlineData("proxy a.example:1;", "http://a.example:1/")]
    [InlineData("HTTPS a.example:444", "https://a.example:444/")]
    [InlineData("PROXY a.example", "http://a.example/")]
    [InlineData("PROXY [::1]:3128", "http://[::1]:3128/")]
    [InlineData("DIRECT; PROXY a.example:1", null)]
    [InlineData("DIRECT", null)]
    public async Task Script_answer_uses_its_first_entry(string says, string? expected)
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var pac = new PacProxy(WithScript(server.ScriptSaying(says)).Proxy);

        var answer = await pac.ResolveAsync(new Uri("http://adm.invalid/f.bin"), CancellationToken.None);

        Assert.Equal(expected, answer?.ToString());
    }

    [Fact]
    public void Detection_that_finds_no_script_goes_direct_but_one_that_fails_does_not()
    {
        Assert.Null(PacProxy.AfterError(PacProxy.AutoDetectionFailed, detecting: true));

        Assert.Throws<HttpRequestException>(() => PacProxy.AfterError(PacProxy.AutoDetectionFailed, detecting: false));
        Assert.Throws<HttpRequestException>(() => PacProxy.AfterError(PacProxy.UnableToDownloadScript, detecting: true));
    }

    [Fact]
    public void Script_failure_is_retried_like_a_connection_failure()
    {
        var e = Assert.Throws<HttpRequestException>(() => PacProxy.AfterError(PacProxy.UnableToDownloadScript, detecting: false));

        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None));
        Assert.True(Net.IsTransient(e, CancellationToken.None));
    }

    [Fact]
    public async Task Waiting_for_a_script_ends_when_the_download_is_stopped()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var pac = new PacProxy(WithScript(server.HangingScriptUrl).Proxy);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pac.ResolveAsync(new Uri(ViaProxyUrl), stop.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // --- The browser's proxy ---

    static string Via(TestServer server) => server.BaseUrl;

    // Going direct, ViaProxyUrl's name can't be found: no answer at all, quickly, where a refused connection takes Windows 2 seconds.
    static readonly Settings NoProxy = new() { Proxy = new ProxySettings { Mode = ProxyMode.None } };

    [Fact]
    public async Task Browser_proxy_takes_over_after_a_refused_request()
    {
        await using var server = await TestServer.StartAsync(2 * 1024 * 1024, TimeSpan.Zero);
        var item = NewItem(server.ProxiedOnlyUrl);
        item.BrowserProxy = Via(server);
        string? note = null;
        var downloader = new HttpDownloader(new RateLimiter(), new Settings { Proxy = new ProxySettings { Mode = ProxyMode.None } }, (d, _) => note = d.StatusNote);

        await downloader.RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        // Its later connections asked for ranges, which only a request through the proxy gets.
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal("Using the browser's proxy", note);
        Assert.Null(item.StatusNote);
    }

    [Fact]
    public async Task Browser_proxy_is_tried_before_waiting()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(ViaProxyUrl);
        item.BrowserProxy = Via(server);
        // A window this long would outlast the test, if the wait came first.
        var retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(20), ReconnectWindow = TimeSpan.FromHours(1) };

        await new HttpDownloader(new RateLimiter(), NoProxy, (_, _) => { }) { Retry = retry }.RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.ProxiedRequests > 0);
    }

    [Fact]
    public async Task Browser_proxy_failing_too_goes_back_to_the_own_route()
    {
        // The browser's proxy goes nowhere, and the server comes back: only the download's own route can reach it.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.FileUrl);
        item.BrowserProxy = "http://127.0.0.1:9";
        var retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(20) };
        var run = new HttpDownloader(new RateLimiter(), NoProxy, (_, _) => { }) { Retry = retry }.RunAsync(item, CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (server.Hits("/file.bin") <= 8 && DateTime.UtcNow < deadline) await Task.Delay(10);
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Browser_proxy_takes_over_when_the_own_route_gets_no_answer()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(ViaProxyUrl);
        item.BrowserProxy = Via(server);

        await RunShortAsync(item, NoProxy);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.ProxiedRequests > 0);
    }

    [Fact]
    public async Task Browser_proxy_takes_over_from_a_script_that_cannot_be_loaded()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(ViaProxyUrl);
        item.BrowserProxy = Via(server);

        await RunShortAsync(item, WithScript(server.MissingScriptUrl));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Browser_proxy_can_be_a_script()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(ViaProxyUrl);
        item.BrowserProxy = "pac:" + server.ProxyScriptUrl;

        await RunShortAsync(item, NoProxy);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Browser_proxy_is_not_used_when_turned_off()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.ProxiedOnlyUrl);
        item.BrowserProxy = Via(server);
        var settings = new Settings { Proxy = new ProxySettings { Mode = ProxyMode.None }, UseBrowserProxyOnErrors = false };

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(item, settings));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, e.StatusCode);
        Assert.Equal(0, server.ProxiedRequests);
    }

    [Fact]
    public async Task Browser_proxy_is_not_used_after_other_errors()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.MissingUrl);
        item.BrowserProxy = Via(server);

        await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(item, NoProxy));

        Assert.Equal(0, server.ProxiedRequests);
    }

    [Fact]
    public async Task Browser_proxy_is_tried_once_and_the_first_error_stands()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.ForbiddenUrl);
        item.BrowserProxy = Via(server);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(item, NoProxy));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, e.StatusCode);
        Assert.Equal(2, server.Hits("/forbidden.bin"));
        Assert.Equal(1, server.ProxiedRequests);
        Assert.Null(item.StatusNote);
    }

    [Fact]
    public async Task Failing_browser_proxy_leaves_the_own_routes_error()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        // The proxy answers 403, but what the user is told about is the name that can't be found.
        var item = NewItem("http://adm.invalid/forbidden.bin");
        item.BrowserProxy = Via(server);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunShortAsync(item, NoProxy));

        Assert.Null(e.StatusCode);
        Assert.True(server.ProxiedRequests >= 1, "the browser's proxy was never tried");
    }

    [Fact]
    public async Task Browser_proxy_that_is_the_own_route_is_not_tried_again()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var proxy = new Uri(server.BaseUrl);
        var item = NewItem(server.ForbiddenUrl);
        item.BrowserProxy = Via(server);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RunShortAsync(item, new Settings { Proxy = Manual(ProxyKind.Http, proxy.Host, proxy.Port, bypass: "") }));

        Assert.Equal(1, server.Hits("/forbidden.bin"));
    }

    [Fact]
    public async Task Download_with_no_browser_proxy_fails_as_before()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RunShortAsync(NewItem(server.ForbiddenUrl), new Settings { Proxy = new ProxySettings { Mode = ProxyMode.None } }));

        Assert.Equal(1, server.Hits("/forbidden.bin"));
    }

    [Fact]
    public async Task Resume_takes_the_browser_proxy_too()
    {
        await using var server = await TestServer.StartAsync(2 * 1024 * 1024, TimeSpan.Zero);
        var item = NewItem(server.ProxiedOnlyUrl);
        item.BrowserProxy = Via(server);
        var settings = new Settings { Proxy = new ProxySettings { Mode = ProxyMode.None } };
        using var stop = new CancellationTokenSource();
        var first = new HttpDownloader(new RateLimiter(), settings, (_, _) => { });
        var run = first.RunAsync(item, stop.Token);
        while (item.DownloadedBytes == 0 && !run.IsCompleted) await Task.Delay(5);
        await stop.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        await RunShortAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Hls_takes_the_browser_proxy()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem("http://adm.invalid/hls/ts.m3u8");
        item.Kind = DownloadKind.Hls;
        item.FileName = "video.ts";
        item.BrowserProxy = Via(server);

        await new HlsDownloader(new RateLimiter(), NoProxy, null).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Null(item.StatusNote);
    }

    [Fact]
    public async Task Hls_keeps_to_the_own_route_when_turned_off()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem("http://adm.invalid/hls/ts.m3u8");
        item.Kind = DownloadKind.Hls;
        item.FileName = "video.ts";
        item.BrowserProxy = Via(server);
        var settings = new Settings { Proxy = NoProxy.Proxy, UseBrowserProxyOnErrors = false };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new HlsDownloader(new RateLimiter(), settings, null).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal(0, server.ProxiedRequests);
    }

    // tests/fixtures/browser-proxy.json is shared with tests/extension/proxy.test.mjs, which makes these strings.
    static JsonNode Fixture([CallerFilePath] string here = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "browser-proxy.json")))!;

    [Fact]
    public void Browser_proxy_strings_parse()
    {
        var fixture = Fixture();
        var made = fixture["chrome"]!.AsArray().Concat(fixture["firefox"]!.AsArray())
            .Select(c => (string)c!["proxy"]!).Where(p => p.Length > 0).ToHashSet();
        var routes = fixture["routes"]!.AsObject();
        Assert.Empty(made.Except(routes.Select(r => r.Key)));

        foreach (var (text, node) in routes)
        {
            var route = BrowserProxy.Parse(text);
            Assert.NotNull(route);
            Assert.Equal(Enum.Parse<ProxyMode>((string)node!["mode"]!), route.Mode);
            if (route.Mode == ProxyMode.Script)
            {
                Assert.Equal((string)node["scriptUrl"]!, route.ScriptUrl);
                continue;
            }
            Assert.Equal(Enum.Parse<ProxyKind>((string)node["kind"]!), route.Kind);
            Assert.Equal((string)node["host"]!, route.Host);
            Assert.Equal((int)node["port"]!, route.Port);
            Assert.Equal("", route.Bypass);
            Assert.Equal(new Uri(text), new ProxyRouter(route).GetProxy(new Uri("https://b.com/f")));
        }
    }

    [Fact]
    public void Browser_proxy_strings_that_make_no_sense_are_ignored()
    {
        Assert.Null(BrowserProxy.Parse(null));
        foreach (var text in Fixture()["refused"]!.AsArray())
            Assert.True(BrowserProxy.Parse((string)text!) == null, (string)text!);
    }

    [Fact]
    public void Same_route_compares_where_requests_go()
    {
        var parsed = BrowserProxy.Parse("http://P.local:3128")!;

        Assert.True(BrowserProxy.SameRoute(parsed, Manual(ProxyKind.Http, "p.local", 3128)));
        Assert.False(BrowserProxy.SameRoute(parsed, Manual(ProxyKind.Http, "p.local", 3129)));
        Assert.False(BrowserProxy.SameRoute(parsed, Manual(ProxyKind.Socks5, "p.local", 3128)));
        Assert.False(BrowserProxy.SameRoute(parsed, new ProxySettings { Mode = ProxyMode.None }));
        Assert.False(BrowserProxy.SameRoute(parsed, new ProxySettings { Mode = ProxyMode.System }));
        Assert.True(BrowserProxy.SameRoute(BrowserProxy.Parse("pac:http://a/p.pac")!, new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = "http://a/p.pac" }));
        Assert.False(BrowserProxy.SameRoute(BrowserProxy.Parse("pac:http://a/p.pac")!, new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = "http://b/p.pac" }));
    }

    [Fact]
    public void Older_settings_load_with_the_new_fields_defaulted()
    {
        var path = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "Settings": { "Proxy": { "Mode": "Manual", "Kind": "Socks5", "Host": "p", "Port": 1 } }, "Downloads": [ { "Url": "http://a/f" } ] }""");

        var state = Store.Load(path);

        Assert.Equal(ProxyMode.Manual, state.Settings.Proxy.Mode);
        Assert.Equal(ProxyKind.Socks5, state.Settings.Proxy.Kind);
        Assert.Equal("", state.Settings.Proxy.ScriptUrl);
        Assert.True(state.Settings.UseBrowserProxyOnErrors);
        Assert.Null(state.Downloads[0].BrowserProxy);
    }

    [Fact]
    public void Script_settings_and_the_browser_proxy_survive_a_save()
    {
        var path = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(_dir);
        var state = new AppState();
        state.Settings.Proxy = new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = "http://a/p.pac" };
        state.Settings.UseBrowserProxyOnErrors = false;
        state.Downloads.Add(new DownloadItem { Url = "http://a/f", BrowserProxy = "socks5://p:1" });

        Store.Save(path, state);
        var loaded = Store.Load(path);

        Assert.Equal(ProxyMode.Script, loaded.Settings.Proxy.Mode);
        Assert.Equal("http://a/p.pac", loaded.Settings.Proxy.ScriptUrl);
        Assert.False(loaded.Settings.UseBrowserProxyOnErrors);
        Assert.Equal("socks5://p:1", loaded.Downloads[0].BrowserProxy);
    }

    [Fact]
    public async Task Added_and_refreshed_downloads_keep_the_browser_proxy()
    {
        var manager = new DownloadManager(Path.Combine(_dir, "state.json"), new LockedContext());

        var item = manager.Add(new DownloadRequest { Url = "http://adm.invalid/f.bin", BrowserProxy = "http://p.local:1", Directory = _dir }, startNow: false);
        Assert.Equal("http://p.local:1", item.BrowserProxy);

        manager.ApplyRefresh(item, new DownloadRequest { Url = "http://adm.invalid/f.bin", BrowserProxy = "socks5://p.local:2" });
        Assert.Equal("socks5://p.local:2", item.BrowserProxy);
        await manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_login_in_the_browser_proxy_is_never_kept()
    {
        var manager = new DownloadManager(Path.Combine(_dir, "state.json"), new LockedContext());

        var script = manager.Add(new DownloadRequest { Url = "http://adm.invalid/a.bin", BrowserProxy = "pac:http://user:pass@a/p.pac", Directory = _dir }, startNow: false);
        var server = manager.Add(new DownloadRequest { Url = "http://adm.invalid/b.bin", BrowserProxy = "http://user:pass@p.local:1", Directory = _dir }, startNow: false);

        Assert.Null(script.BrowserProxy);
        Assert.Null(server.BrowserProxy);
        await manager.ShutdownAsync();
    }
}
