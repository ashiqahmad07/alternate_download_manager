using ADM.Core;

namespace ADM.Core.Tests;

public class SiteListTests
{
    [Theory]
    [InlineData("*.tonec.com", "https://www.games.tonec.com/file.zip", true)]
    [InlineData("*.tonec.com", "https://nottonec.com/file.zip", false)]
    [InlineData("example.com", "https://EXAMPLE.com/a", true)]
    [InlineData("example.com", "https://cdn.example.com/a", false)]
    [InlineData("a.com  *.b.org", "http://x.b.org/f", true)]
    [InlineData("", "https://example.com/", false)]
    [InlineData(null, "https://example.com/", false)]
    public void Matches_host_patterns(string? list, string url, bool expected) =>
        Assert.Equal(expected, SiteList.Matches(list, url));

    [Theory]
    [InlineData("http://*.skyrock.com/*.img", "http://pics.skyrock.com/x/y.img", true)]
    [InlineData("http://*.skyrock.com/*.img", "http://pics.skyrock.com/x/y.png", false)]
    [InlineData("https://a.com/dl/*", "https://a.com/dl/f.zip?k=1", true)]
    public void Matches_address_patterns_against_the_whole_url(string list, string url, bool expected) =>
        Assert.Equal(expected, SiteList.Matches(list, url));

    [Fact]
    public void Ignores_urls_that_cannot_be_parsed() =>
        Assert.False(SiteList.Matches("*.com", "not a url"));
}
