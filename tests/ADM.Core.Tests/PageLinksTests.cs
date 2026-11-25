using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

public class PageLinksTests
{
    static PageLinks Parse(string json) => PageLinks.FromMessage(JsonNode.Parse(json)!);

    const string Message = """
        {
          "type": "links",
          "referrer": "https://a.com/page",
          "userAgent": "UA",
          "cookies": { "https://a.com": "s=1", "https://b.com": "t=2" },
          "proxies": { "https://a.com": "http://p.local:3128" },
          "links": [
            { "url": "https://a.com/f.zip", "text": "Setup", "image": false },
            { "url": "https://b.com/i.png", "text": "", "image": true },
            { "url": "javascript:alert(1)", "text": "menu", "image": false },
            { "url": "https://c.com/g.exe" },
            { "text": "no address" }
          ]
        }
        """;

    [Fact]
    public void Keeps_only_web_links()
    {
        var page = Parse(Message);

        Assert.Equal(new[] { "https://a.com/f.zip", "https://b.com/i.png", "https://c.com/g.exe" }, page.Links.Select(l => l.Url));
        Assert.True(page.Links[1].IsImage);
        Assert.Equal("Setup", page.Links[0].Text);
        Assert.Equal("", page.Links[2].Text);
    }

    [Fact]
    public void Each_request_gets_its_origins_cookies_and_the_page_as_referrer()
    {
        var page = Parse(Message);

        var a = page.ToRequest(page.Links[0]);
        Assert.Equal("https://a.com/f.zip", a.Url);
        Assert.Equal("s=1", a.Cookies);
        Assert.Equal("https://a.com/page", a.Referrer);
        Assert.Equal("UA", a.UserAgent);
        Assert.Equal("t=2", page.ToRequest(page.Links[1]).Cookies);
        Assert.Null(page.ToRequest(page.Links[2]).Cookies);
    }

    [Fact]
    public void Each_request_gets_the_browser_proxy_of_its_origin()
    {
        var page = Parse(Message);

        Assert.Equal("http://p.local:3128", page.ToRequest(page.Links[0]).BrowserProxy);
        Assert.Null(page.ToRequest(page.Links[1]).BrowserProxy);
    }

    [Fact]
    public void Filter_by_types_with_wildcards_and_hide_images()
    {
        var page = Parse(Message);

        Assert.Equal(3, page.Filter("", hideImages: false).Count());
        Assert.Equal(new[] { "https://a.com/f.zip", "https://c.com/g.exe" }, page.Filter("", hideImages: true).Select(l => l.Url));
        Assert.Equal(new[] { "https://c.com/g.exe" }, page.Filter(".EXE", hideImages: false).Select(l => l.Url));
        Assert.Equal(new[] { "https://a.com/f.zip", "https://b.com/i.png" }, page.Filter("z* png", hideImages: false).Select(l => l.Url));
    }

    [Fact]
    public void A_link_has_a_file_name_from_its_address()
    {
        Assert.Equal("f.zip", new PageLink("https://a.com/dl/f.zip?x=1", "", false).FileName);
        Assert.Equal("a.com", new PageLink("https://a.com/", "", false).FileName);
    }
}
