using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberMaskTests
{
    const string Url = "http://www.site.test/dir/a.jpg";
    const string Start = "http://www.site.test/start.html";

    [Theory]
    [InlineData("*.jpg", "a.jpg", true)]
    [InlineData("*.JPG", "a.jpg", true)]
    [InlineData("*.jpg", "a.jpeg", false)]
    [InlineData("*.jpg", "a.jpg.exe", false)]
    [InlineData("image*.jpg", "image01.jpg", true)]
    [InlineData("image*.jpg", "other01.jpg", false)]
    [InlineData("*.gif,*.jpg", "a.jpg", true)]
    [InlineData("*.gif, *.jpg", "a.jpg", true)]
    [InlineData("*.gif;*.jpg", "a.jpg", true)]
    [InlineData("*", "a.jpg", true)]
    [InlineData("*", "noextension", true)]
    [InlineData("*.*", "a.jpg", true)]
    [InlineData("*.*", "noextension", true)]
    [InlineData("a.jpg", "a.jpg", true)]
    [InlineData("a.jpg", "xa.jpg", false)]
    [InlineData("a?.jpg", "ab.jpg", false)]
    [InlineData("a.jpg", "axjpg", false)]
    public void Mask_matches_a_file_name(string mask, string name, bool expected)
    {
        Assert.Equal(expected, GrabberMask.Parse(mask).Matches(name, Url, Start));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" , ; ")]
    [InlineData(null)]
    public void Empty_mask_is_empty(string? mask)
    {
        var parsed = GrabberMask.Parse(mask);
        Assert.True(parsed.IsEmpty);
        Assert.False(parsed.Matches("a.jpg", Url, Start));
    }

    [Theory]
    [InlineData("<start page>")]
    [InlineData("<START PAGE>")]
    [InlineData("*.png,<start page>")]
    public void Start_page_stands_for_its_address(string mask)
    {
        var parsed = GrabberMask.Parse(mask);
        Assert.True(parsed.Matches("start.html", Start, Start));
        Assert.False(parsed.Matches("other.html", "http://www.site.test/other.html", Start));
    }

    [Theory]
    [InlineData("www.site.test", true)]
    [InlineData("WWW.SITE.TEST", true)]
    [InlineData("*.site.test", true)]
    [InlineData("site.test", false)]
    [InlineData("other.test", false)]
    [InlineData("other.test;www.site.test", true)]
    [InlineData("other.test ; www.site.test", true)]
    [InlineData("www.site.test/dir/*", true)]
    [InlineData("*/dir/*", true)]
    [InlineData("http://www.site.test/dir/a.jpg", true)]
    [InlineData("www.site.test/other/*", false)]
    [InlineData("/dir/*", true)]
    [InlineData("/other/*", false)]
    [InlineData("*logout*", false)]
    [InlineData("*dir*", true)]
    public void List_matches_a_host_or_a_path(string list, bool expected)
    {
        Assert.Equal(expected, GrabberPathList.Parse(list).Matches(new Uri(Url)));
    }

    [Theory]
    [InlineData("*/logout*", "http://www.site.test/logout.php?x=1", true)]
    [InlineData("/account/*;/logout*", "http://www.site.test/logout", true)]
    [InlineData("/a b/*", "http://www.site.test/a%20b/c", true)]
    public void List_matches_as_the_address_reads(string list, string url, bool expected)
    {
        Assert.Equal(expected, GrabberPathList.Parse(list).Matches(new Uri(url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ; ")]
    [InlineData(null)]
    public void Empty_list_is_empty(string? list)
    {
        var parsed = GrabberPathList.Parse(list);
        Assert.True(parsed.IsEmpty);
        Assert.False(parsed.Matches(new Uri(Url)));
    }

    [Fact]
    public void Hostile_address_is_matched_quickly()
    {
        var mask = GrabberMask.Parse("*a*a*a*a*a*a*a*a*a*a*a*a*b");
        var list = GrabberPathList.Parse("*a*a*a*a*a*a*a*a*a*a*a*a*b/*");
        var name = new string('a', 60_000);
        var clock = Stopwatch.StartNew();
        Assert.False(mask.Matches(name, Url, Start));
        Assert.False(list.Matches(new Uri("http://site.test/" + name)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }
}
