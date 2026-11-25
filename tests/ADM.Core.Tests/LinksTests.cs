using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

public class LinksTests
{
    [Fact]
    public void Finds_links_in_free_text_once_each_in_order()
    {
        var text = "Get it at https://a.com/x.zip, or (http://b.org/y.iso).\nAgain: https://a.com/x.zip";
        Assert.Equal(new[] { "https://a.com/x.zip", "http://b.org/y.iso" }, Links.Find(text));
    }

    [Fact]
    public void Finds_links_inside_html_attributes() =>
        Assert.Equal(new[] { "https://a.com/f.pdf" }, Links.Find("<a href=\"https://a.com/f.pdf\">f</a>"));

    [Fact]
    public void Keeps_query_strings() =>
        Assert.Equal(new[] { "https://a.com/get?id=7&k=x" }, Links.Find("link: https://a.com/get?id=7&k=x"));

    [Fact]
    public void Ignores_other_schemes() =>
        Assert.Empty(Links.Find("mailto:a@b.com javascript:void(0) file:///c:/x"));

    [Theory]
    [InlineData("ftp://user:pw@host.example/pub/f.zip", "ftp://host.example/pub/f.zip", "user", "pw")]
    [InlineData("http://user@host.example:8080/x?y=a@b", "http://host.example:8080/x?y=a@b", "user", null)]
    [InlineData("ftp://a%40b:p%3Aw@host.example/f", "ftp://host.example/f", "a@b", "p:w")]
    [InlineData("https://host.example/no-login@here", "https://host.example/no-login@here", null, null)]
    public void Login_comes_out_of_the_address(string url, string clean, string? user, string? password)
    {
        Assert.Equal((clean, user, password), Links.SplitLogin(url));
        // The host is the same one System.Uri reads before and after.
        Assert.Equal(new Uri(url).IdnHost, new Uri(clean).IdnHost);
    }

    [Theory]
    [InlineData("http://good.example\\@evil.example/f.zip")]
    [InlineData("ftp://good.example\\user:pw@evil.example/f")]
    public void Address_that_System_Uri_rejects_is_not_rewritten_into_one_it_accepts(string url)
    {
        // The "@" after a backslash looks like the end of a login, and taking it out would leave a valid address
        // for evil.example, though every other check of this one (System.Uri, and browsers, which end the host
        // at the backslash) reads it as something else, or as nothing.
        Assert.False(Uri.TryCreate(url, UriKind.Absolute, out _));

        Assert.Equal((url, (string?)null, (string?)null), Links.SplitLogin(url));
    }

    // tests/fixtures/stream-kinds.json is shared with tests/extension/background.test.mjs, whose streamKind follows the same rule.
    public static IEnumerable<object[]> StreamKinds() =>
        JsonNode.Parse(File.ReadAllText(Fixture("stream-kinds.json")))!
            .AsArray().Select(c => new object[] { (string)c!["name"]!, (string)c["url"]!, (string)c["kind"]! });

    static string Fixture(string name, [CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", name);

    [Theory]
    [MemberData(nameof(StreamKinds))]
    public void Stream_kind_goes_by_the_address(string name, string url, string kind) =>
        Assert.True(kind == Links.StreamKind(url).ToString().ToLowerInvariant(), name);
}
