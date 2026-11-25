using System.Net;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class LoginTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, string? user = null, string? password = null) => new()
    {
        Url = url,
        Directory = _dir,
        FileName = "secure.bin",
        UserName = user,
        ProtectedPassword = Secrets.Protect(password),
    };

    static Task RunAsync(DownloadItem item, Settings? settings = null) =>
        new HttpDownloader(new RateLimiter(), settings ?? new Settings(), (_, _) => { }).RunAsync(item, CancellationToken.None);

    [Fact]
    public async Task Download_without_login_gets_401()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(NewItem(server.SecureUrl)));

        Assert.Contains("refused the login", e.Message);
    }

    [Fact]
    public async Task Per_download_login_is_used()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.SecureUrl, "user", "pass");

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Site_login_is_used_for_matching_sites()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var settings = new Settings
        {
            SiteLogins = { new SiteLogin { Sites = "127.0.0.1", UserName = "user", ProtectedPassword = Secrets.Protect("pass") } },
        };
        var item = NewItem(server.SecureUrl);

        await RunAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Wrong_login_fails_once_with_a_clear_error()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(NewItem(server.SecureUrl, "user", "wrong")));

        Assert.Contains("refused the login", e.Message);
        // The challenge plus one attempt with the login; a refused login is never retried.
        Assert.True(server.SecureRequests <= 2, $"server saw {server.SecureRequests} requests");
    }

    [Fact]
    public async Task Login_is_not_sent_to_a_redirected_host()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);

        var e = await Assert.ThrowsAsync<HttpRequestException>(
            () => RunAsync(NewItem(server.BaseUrl + "/to-other-host", "user", "pass")));

        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
    }

    [Fact]
    public async Task Login_follows_a_redirect_on_the_same_host()
    {
        // Download links often bounce to the real file on the same server, e.g. /get?id=1 -> /files/a.zip.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + "/to-same-host", "user", "pass");

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Accepted_login_is_not_carried_to_a_redirected_host()
    {
        // The site takes the login, then redirects to another host: that host must not receive it.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + "/secure-redirect.bin", "user", "pass");

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.NotEmpty(server.AuthorizationsElsewhere);
        Assert.All(server.AuthorizationsElsewhere, Assert.Empty);
    }

    [Fact]
    public async Task Browser_cookies_are_not_sent_to_a_redirected_host()
    {
        // Cookies come from the browser for the link's own site; another host must never see them.
        await using var server = await TestServer.StartAsync(2 * 1024 * 1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + "/to-other-host/cookies.bin");
        item.Cookies = "session=s3cret";
        item.MaxConnections = 8;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("localhost", ""), seen));
        Assert.True(server.CookiesSeen.Count > 1, "the download should still use several connections");
    }

    [Fact]
    public async Task Browser_cookies_follow_a_redirect_on_the_same_host()
    {
        await using var server = await TestServer.StartAsync(2 * 1024 * 1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + "/to-same-host/cookies.bin");
        item.Cookies = "session=s3cret";
        item.MaxConnections = 8;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("127.0.0.1", "session=s3cret"), seen));
    }

    [Fact]
    public void Add_protects_the_request_password()
    {
        var statePath = Path.Combine(_dir, "state.json");
        var m = new DownloadManager(statePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;

        // Port 9 is never contacted: the download is only queued.
        var item = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f.zip", UserName = "user", Password = "adm-s3cret-42" }, startNow: false);
        m.Save();

        Assert.DoesNotContain("adm-s3cret-42", File.ReadAllText(statePath));
        Assert.Equal("user", item.UserName);
        Assert.Equal("adm-s3cret-42", Secrets.Unprotect(item.ProtectedPassword));
    }
}
