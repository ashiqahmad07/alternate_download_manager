using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>FTP downloads go through the same engine as HTTP ones: several connections, resume, reconnecting, limits.</summary>
public sealed class FtpTests : IDisposable
{
    const int MB = 1024 * 1024;
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    DownloadItem NewItem(string url, int connections = 8, string fileName = "file.bin") =>
        new() { Url = url, Directory = _dir, FileName = fileName, MaxConnections = connections };

    static Task RunAsync(DownloadItem item, Settings? settings = null, CancellationToken ct = default) =>
        new HttpDownloader(new RateLimiter(), settings ?? new Settings(), (_, _) => { })
        {
            Retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short },
        }.RunAsync(item, ct);

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    static Settings ProxiedBy(ProxyKind kind, int port, string bypass = "", string? user = null, string? password = null) => new()
    {
        Proxy = new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Kind = kind,
            Host = "127.0.0.1",
            Port = port,
            Bypass = bypass,
            UserName = user,
            ProtectedPassword = password == null ? null : Secrets.Protect(password),
        },
    };

    [Fact]
    public async Task Ftp_download_matches_the_payload()
    {
        await using var server = await FtpTestServer.StartAsync(2 * MB);
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.Payload.Length, item.TotalBytes);
        Assert.True(item.ResumeSupported);
        Assert.Equal("file.bin", item.FileName);
    }

    [Fact]
    public async Task Ftp_uses_several_connections()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.MaxConcurrentTransfers >= 4, $"at most {server.MaxConcurrentTransfers} transfers at once");
        Assert.True(server.Restarts.Distinct().Count() >= 4, $"RETR from {server.Restarts.Distinct().Count()} different offsets");
    }

    [Fact]
    public async Task Ftp_resumes_to_identical_bytes_after_pause_and_restart()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        server.PerConnectionKBps = 128;
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin", Directory = _dir }, startNow: true));

        await Until(() => item.DownloadedBytes >= MB);
        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });
        ui.Run(m.Save);
        var saved = item.SegmentsSnapshot;
        Assert.InRange(saved.Sum(s => s.Downloaded), 1, 4 * MB - 1);
        var before = server.Restarts.Count;

        var ui2 = new LockedContext();
        var reloaded = new DownloadManager(StatePath, ui2);
        var again = Assert.Single(reloaded.Items);
        Assert.Equal(DownloadStatus.Paused, again.Status);
        Assert.Equal(saved.Sum(s => s.Downloaded), again.SegmentsSnapshot.Sum(s => s.Downloaded));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloaded.DownloadCompleted += _ => done.TrySetResult();
        reloaded.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        server.PerConnectionKBps = 512;
        ui2.Run(() => reloaded.Start(again));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(again.FullPath));
        // Nothing already saved is asked for again, apart from the overlap a continuation reads to check it.
        foreach (var start in server.Restarts.Skip(before))
            Assert.DoesNotContain(saved, s => s.Start <= start && start < s.Start + s.Downloaded - HttpDownloader.OverlapBytes);
    }

    [Fact]
    public async Task Ftp_reconnects_after_a_dropped_data_connection()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        server.DropOnceAfterBytes = 300_000;
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(1, server.Drops);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Ftp_transfer_cut_short_without_a_known_size_is_not_taken_for_complete()
    {
        // With no size to compare, the end of the data is the only sign of the end: a clean close after 300 KB
        // looks like a finished file until the server says 426.
        await using var server = await FtpTestServer.StartAsync(MB);
        server.SizeUnknown = true;
        server.DropGracefully = true;
        server.DropOnceAfterBytes = 300_000;
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(1, server.Drops);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(item.ResumeSupported);
    }

    [Fact]
    public async Task Ftp_without_rest_downloads_with_one_connection()
    {
        await using var server = await FtpTestServer.StartAsync(2 * MB);
        server.RefuseRest = true;
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        // The Resume column reads No.
        Assert.True(item.ResumeKnown);
        Assert.False(item.ResumeSupported);
        Assert.Equal(1, server.MaxConcurrentTransfers);
    }

    [Fact]
    public async Task Ftp_connection_limit_is_learned()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        server.MaxControlConnections = 3;
        await RunAsync(NewItem(server.FileUrl, 8, "first.bin"));
        var refused = server.Refused;
        Assert.True(refused > 0, "the first download was never refused");
        Assert.InRange(Net.LearnedLimit(new Uri(server.FileUrl)) ?? 0, 1, 3);
        await Until(() => server.ActiveControlConnections == 0);

        var second = NewItem(server.FileUrl, 8, "second.bin");
        await RunAsync(second);

        Assert.Equal(server.Payload, File.ReadAllBytes(second.FullPath));
        // Not knowing the limit, the second download would be turned away five times at its start alone (8
        // connections against 3); a server that is slow to notice a closed connection may still refuse one.
        Assert.True(server.Refused - refused < 3, $"turned away {server.Refused - refused} more times");
    }

    [Fact]
    public async Task Ftp_changed_file_starts_over()
    {
        await using var server = await FtpTestServer.StartAsync(2 * MB);
        var item = NewItem(server.FileUrl);
        var probe = await HttpDownloader.ProbeAsync(item, new Settings(), default);
        int saved = 768 * 1024;
        item.ResumeSupported = true;
        item.TotalBytes = probe.Total;
        item.LastModified = probe.LastModified;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = probe.Total - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload[..saved]);
        server.Version = 1;

        await RunAsync(item);

        Assert.Equal(server.PayloadFor(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Ftp_wrong_login_fails_without_retrying()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.RequireLogin = true;
        var item = NewItem(server.FileUrl);
        item.UserName = "user";
        item.ProtectedPassword = Secrets.Protect("wrong");

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item));

        Assert.Equal("The FTP server refused the login.", e.Message);
        Assert.Equal(1, server.LoginAttempts);
    }

    [Fact]
    public async Task Ftp_right_login_downloads()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.RequireLogin = true;
        var item = NewItem(server.FileUrl);
        item.UserName = "user";
        item.ProtectedPassword = Secrets.Protect("pass");

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.All(server.UsersSeen, u => Assert.Equal("user", u));
    }

    [Fact]
    public async Task Ftp_file_that_is_not_there_fails_with_a_clear_message()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        var item = NewItem($"ftp://127.0.0.1:{server.Port}/pub/missing.bin");

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item));

        Assert.Equal("The file was not found on the FTP server.", e.Message);
    }

    [Fact]
    public async Task Line_break_in_the_address_cannot_send_a_second_command()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        var item = NewItem($"ftp://127.0.0.1:{server.Port}/pub/file.bin%0D%0ADELE%20file.bin");

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item));

        Assert.Equal("The address or login has a line break in it, which FTP can't carry.", e.Message);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("DELE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Endless_multiline_reply_is_cut_off()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.Greeting = async (stream, ct) =>
        {
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("220-Welcome\r\n"), ct);
            var line = System.Text.Encoding.ASCII.GetBytes(new string('x', 100) + "\r\n");
            // Never the closing line; this ends when the client hangs up.
            for (int i = 0; i < 1_000_000; i++) await stream.WriteAsync(line, ct);
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(NewItem(server.FileUrl), ct: cts.Token));

        Assert.Equal("The FTP server sent a reply that is too long.", e.Message);
        // A server that does this is not tried again.
        Assert.Equal(1, server.ControlConnections);
    }

    [Fact]
    public async Task Reply_line_without_an_end_is_cut_off()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.Greeting = (stream, ct) => stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("220 " + new string('x', 200_000)), ct).AsTask();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(NewItem(server.FileUrl), ct: cts.Token));

        Assert.Equal("The FTP server sent a reply that is too long.", e.Message);
        Assert.Equal(1, server.ControlConnections);
    }

    [Fact]
    public async Task Site_login_is_offered_to_an_ftp_server()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.RequireLogin = true;
        var settings = new Settings();
        settings.SiteLogins.Add(new SiteLogin { Sites = "127.0.0.1", UserName = "user", ProtectedPassword = Secrets.Protect("pass") });
        var item = NewItem(server.FileUrl);

        await RunAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Passive_reply_naming_another_host_still_uses_the_server()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.NoEpsv = true;
        server.PasvNamesOtherHost = true;
        var item = NewItem(server.FileUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await RunAsync(item, ct: cts.Token);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Ftp_through_socks5()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        await using var relay = new Socks5TestRelay("proxy", "secret");
        var item = NewItem(server.FileUrl);

        await RunAsync(item, ProxiedBy(ProxyKind.Socks5, relay.Port, user: "proxy", password: "secret"));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        // The control connection and the data connections all went through the proxy.
        Assert.Contains($"127.0.0.1:{server.Port}", relay.Targets);
        Assert.True(relay.Targets.Count >= 2, $"only {relay.Targets.Count} connections went through the proxy");
        Assert.Equal(server.ControlConnections + server.Restarts.Count, relay.Targets.Count);
    }

    static Settings WithScript(string url) => new() { Proxy = new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = url } };

    [Fact]
    public async Task Ftp_goes_direct_when_the_proxy_script_says_so()
    {
        await using var scripts = await TestServer.StartAsync(1024, TimeSpan.Zero);

        // Going direct, the name can't be found: no proxy error, and the script itself was fine.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(NewItem("ftp://adm.invalid/f.bin"), WithScript(scripts.ScriptSaying("DIRECT"))));

        Assert.IsNotType<ProxyException>(e);
        Assert.NotEqual(PacProxy.LoadFailed, e.Message);
    }

    [Fact]
    public async Task Ftp_goes_through_the_socks_proxy_a_script_names()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        await using var relay = new Socks5TestRelay { ConnectInstead = "127.0.0.1" };
        await using var scripts = await TestServer.StartAsync(1024, TimeSpan.Zero);
        // Windows never asks a script about a loopback address, so the address needs a name; only the proxy can reach it.
        var item = NewItem($"ftp://adm.invalid:{server.Port}{new Uri(server.FileUrl).AbsolutePath}");

        await RunAsync(item, WithScript(scripts.ScriptSaying($"SOCKS5 127.0.0.1:{relay.Port}")));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains($"adm.invalid:{server.Port}", relay.Targets);
    }

    [Fact]
    public async Task Ftp_fails_when_a_script_names_a_proxy_that_cannot_carry_it()
    {
        await using var scripts = await TestServer.StartAsync(1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(NewItem("ftp://adm.invalid/f.bin"), WithScript(scripts.ScriptSaying("PROXY 127.0.0.1:1"))));

        Assert.Equal("The proxy script sends this server through a proxy that can't carry FTP.", e.Message);
    }

    [Fact]
    public async Task Ftp_never_goes_direct_when_the_script_cannot_be_loaded()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        await using var scripts = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, ReconnectWindow = TimeSpan.FromMilliseconds(200) };
        var downloader = new HttpDownloader(new RateLimiter(), WithScript(scripts.MissingScriptUrl), (_, _) => { }) { Retry = retry };

        var e = await Assert.ThrowsAnyAsync<Exception>(() => downloader.RunAsync(NewItem(server.FileUrl), CancellationToken.None));

        Assert.Equal(PacProxy.LoadFailed, e.Message);
        Assert.Equal(0, server.ControlConnections);
    }

    [Fact]
    public async Task Ftp_with_an_http_proxy_fails_with_a_clear_message()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        var item = NewItem(server.FileUrl);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item, ProxiedBy(ProxyKind.Http, 1)));

        Assert.Equal("FTP can't go through an HTTP proxy. Add the server to the proxy's exceptions, or use a SOCKS proxy.", e.Message);
        // It never quietly goes direct.
        Assert.Equal(0, server.ControlConnections);
    }

    [Fact]
    public async Task Ftp_server_on_the_proxy_bypass_list_goes_direct()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        var item = NewItem(server.FileUrl);

        await RunAsync(item, ProxiedBy(ProxyKind.Http, 1, bypass: "127.0.0.1"));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Login_in_the_address_is_not_saved()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.RequireLogin = true;
        server.User = "ftpuser";
        server.Password = "p4ss-w0rd-xyz";
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.UrlWithLogin("ftpuser", "p4ss-w0rd-xyz"), FileName = "file.bin", Directory = _dir }, startNow: false));

        ui.Run(m.Save);
        var saved = File.ReadAllText(StatePath);
        Assert.DoesNotContain("p4ss-w0rd-xyz", saved);
        Assert.DoesNotContain("ftpuser:", saved);
        Assert.DoesNotContain("@", item.Url);
        Assert.Equal("ftpuser", item.UserName);
        Assert.Equal("p4ss-w0rd-xyz", Secrets.Unprotect(item.ProtectedPassword));

        // The login that left the address is the one that downloads.
        ui.Run(() => m.Start(item));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Ftp_sync_check_sees_a_changed_file()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        var item = NewItem(server.FileUrl);
        await RunAsync(item);

        Assert.False(await HttpDownloader.HasChangedAsync(item, new Settings(), default));
        server.Version = 1;
        Assert.True(await HttpDownloader.HasChangedAsync(item, new Settings(), default));
    }

    [Fact]
    public void Links_find_ftp_addresses()
    {
        var found = Links.Find("Get ftp://files.example.com/pub/a.zip, then (https://example.com/b.zip) and FTP://host:2121/c%20d.iso.");

        Assert.Equal(new[] { "ftp://files.example.com/pub/a.zip", "https://example.com/b.zip", "FTP://host:2121/c%20d.iso" }, found);
    }

    [Fact]
    public void Command_line_accepts_an_ftp_address()
    {
        Assert.Equal("ftp://host/a.zip", CommandLine.Parse(new[] { "ftp://host/a.zip" }).Url);
        Assert.Equal("ftp://host/a.zip", CommandLine.Parse(new[] { "/d", "ftp://host/a.zip" }).Url);
    }

    [Fact]
    public void Address_can_be_changed_to_another_ftp_address()
    {
        var m = new DownloadManager(StatePath, new LockedContext());
        var item = m.Add(new DownloadRequest { Url = "ftp://a.example/f.zip", FileName = "f.zip", Directory = _dir }, startNow: false);

        Assert.True(m.SetAddress(item, "ftp://b.example/f.zip"));
        Assert.Equal("ftp://b.example/f.zip", item.Url);
    }
}

/// <summary>The speed comparison runs alone, with the other timing tests, so the rest of the suite can't slow it.</summary>
[Collection(nameof(SpeedTests))]
public sealed class FtpSpeedTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, int connections, string fileName = "file.bin") =>
        new() { Url = url, Directory = _dir, FileName = fileName, MaxConnections = connections };

    static Task RunAsync(DownloadItem item, CancellationToken ct = default) =>
        new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }).RunAsync(item, ct);

    [Fact]
    public async Task Eight_ftp_connections_are_at_least_four_times_faster_than_one()
    {
        // Each data connection gets 512 KB/s, so one needs 8 seconds for the 4 MB. Its speed is measured over
        // 1.5 seconds instead of waiting for all of it.
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        var one = NewItem(server.FileUrl, 1, "one.bin");
        var watch = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(one, cts.Token));
        var oneSpeed = one.DownloadedBytes / watch.Elapsed.TotalSeconds;

        var eight = NewItem(server.FileUrl, 8);
        watch.Restart();
        await RunAsync(eight);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(eight.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), $"8 connections took {elapsed}");
        var eightSpeed = server.Payload.Length / elapsed.TotalSeconds;
        Assert.True(eightSpeed >= 4 * oneSpeed, $"8 connections: {eightSpeed / 1024:0} KB/s; one: {oneSpeed / 1024:0} KB/s");
    }
}
