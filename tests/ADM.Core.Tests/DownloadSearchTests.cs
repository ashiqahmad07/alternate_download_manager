using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>The Find bar: which downloads match, and where Find next goes.</summary>
public sealed class DownloadSearchTests
{
    static DownloadItem Item(string name, string? description = null) => new()
    {
        FileName = name,
        Description = description,
        Url = "https://site.test/files/" + name,
        PageUrl = "https://blog.test/post/7",
        Referrer = "https://ref.test/player",
    };

    static SearchQuery Query(string text, SearchTargets targets = SearchTargets.Names | SearchTargets.Descriptions,
        bool matchCase = false, bool wholeString = false) => new(text, targets, matchCase, wholeString);

    [Fact]
    public void Search_finds_by_name_description_and_address()
    {
        var item = Item("Report-2026.pdf", "quarterly numbers");

        Assert.True(DownloadSearch.Matches(item, Query("report", SearchTargets.Names)));
        Assert.False(DownloadSearch.Matches(item, Query("report", SearchTargets.Descriptions)));
        Assert.True(DownloadSearch.Matches(item, Query("quarterly", SearchTargets.Descriptions)));
        Assert.False(DownloadSearch.Matches(item, Query("quarterly", SearchTargets.Names)));
        Assert.True(DownloadSearch.Matches(item, Query("/files/", SearchTargets.Addresses)));
        Assert.True(DownloadSearch.Matches(item, Query("blog.test/post", SearchTargets.Addresses)));
        Assert.True(DownloadSearch.Matches(item, Query("ref.test", SearchTargets.Addresses)));
        Assert.False(DownloadSearch.Matches(item, Query("blog.test", SearchTargets.Names | SearchTargets.Descriptions)));
        // The site on its own, as a whole string, matches the address's host.
        Assert.True(DownloadSearch.Matches(item, Query("site.test", SearchTargets.Addresses, wholeString: true)));
    }

    [Fact]
    public void Match_case_and_whole_string_narrow_the_search()
    {
        var item = Item("Report-2026.pdf");

        Assert.False(DownloadSearch.Matches(item, Query("report", matchCase: true)));
        Assert.True(DownloadSearch.Matches(item, Query("Report", matchCase: true)));
        Assert.False(DownloadSearch.Matches(item, Query("Report", wholeString: true)));
        Assert.True(DownloadSearch.Matches(item, Query("report-2026.pdf", wholeString: true)));
        Assert.False(DownloadSearch.Matches(item, Query("report-2026.pdf", matchCase: true, wholeString: true)));
    }

    [Fact]
    public void Empty_text_matches_nothing()
    {
        Assert.False(DownloadSearch.Matches(Item("a.zip"), Query("")));
    }

    [Fact]
    public void Next_wraps_to_the_top_and_says_so()
    {
        var a = Item("match-a.zip");
        var b = Item("other-b.zip");
        var c = Item("match-c.zip");
        var d = Item("other-d.zip");
        var list = new[] { a, b, c, d };
        var query = Query("match");

        Assert.Equal((a, false), DownloadSearch.Next(list, null, query));
        Assert.Equal((c, false), DownloadSearch.Next(list, a, query));
        Assert.Equal((c, false), DownloadSearch.Next(list, b, query));
        Assert.Equal((a, true), DownloadSearch.Next(list, c, query));
        Assert.Equal((a, true), DownloadSearch.Next(list, d, query));
    }

    [Fact]
    public void Next_comes_back_to_the_only_match()
    {
        var a = Item("match-a.zip");
        var list = new[] { Item("x.zip"), a, Item("y.zip") };

        Assert.Equal((a, true), DownloadSearch.Next(list, a, Query("match")));
    }

    [Fact]
    public void Next_finds_nothing_when_nothing_matches()
    {
        var list = new[] { Item("a.zip"), Item("b.zip") };

        Assert.Equal(((DownloadItem?)null, false), DownloadSearch.Next(list, list[0], Query("nothing")));
        Assert.Equal(((DownloadItem?)null, false), DownloadSearch.Next(Array.Empty<DownloadItem>(), null, Query("nothing")));
    }
}
