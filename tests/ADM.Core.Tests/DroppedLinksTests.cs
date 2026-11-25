using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>What a drop on the main window, or Ctrl+V in it, adds: links from text, from a browser's link, or from files.</summary>
public sealed class DroppedLinksTests
{
    [Fact]
    public void A_single_link_comes_back_alone() =>
        Assert.Equal(new[] { "https://site.test/f.zip" }, DroppedLinks.FromText("  https://site.test/f.zip\r\n"));

    [Fact]
    public void Links_in_text_come_back_once_each_in_order() =>
        Assert.Equal(new[] { "https://a.test/1.zip", "ftp://b.test/2.iso" },
            DroppedLinks.FromText("see https://a.test/1.zip and ftp://b.test/2.iso, again https://a.test/1.zip"));

    [Fact]
    public void A_curl_command_comes_back_whole()
    {
        var command = "curl 'https://site.test/f.zip' -H 'Cookie: a=1'";

        Assert.Equal(new[] { command }, DroppedLinks.FromText("\n" + command + "\n"));
    }

    [Fact]
    public void A_browsers_link_format_ends_in_a_null_that_is_not_part_of_the_address() =>
        Assert.Equal(new[] { "https://site.test/f.zip" }, DroppedLinks.FromText("https://site.test/f.zip\0"));

    [Fact]
    public void Text_without_links_gives_none() =>
        Assert.Empty(DroppedLinks.FromText("just some words, and mailto:a@b.test"));

    [Theory]
    [InlineData(@"C:\x\links.txt", DroppedFileKind.Links)]
    [InlineData(@"C:\x\page.HTML", DroppedFileKind.Links)]
    [InlineData(@"C:\x\page.htm", DroppedFileKind.Links)]
    [InlineData(@"C:\x\list.csv", DroppedFileKind.Links)]
    [InlineData(@"C:\x\site.url", DroppedFileKind.Shortcut)]
    [InlineData(@"C:\x\all.ef2", DroppedFileKind.ExportFile)]
    [InlineData(@"C:\x\old.ief", DroppedFileKind.ExportFile)]
    [InlineData(@"C:\x\movie.mp4", DroppedFileKind.Other)]
    [InlineData(@"C:\x\no-extension", DroppedFileKind.Other)]
    public void Files_are_told_apart_by_extension(string path, DroppedFileKind kind) =>
        Assert.Equal(kind, DroppedLinks.KindOf(path));

    [Fact]
    public void An_internet_shortcut_gives_its_address()
    {
        var contents = "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,11\r\n[InternetShortcut]\r\nIDList=\r\nURL=https://site.test/get?id=7&k=x\r\nIconIndex=0\r\n";

        Assert.Equal(new[] { "https://site.test/get?id=7&k=x" }, DroppedLinks.FromFile(@"C:\x\site.url", contents));
    }

    [Fact]
    public void An_internet_shortcut_to_something_not_downloadable_gives_nothing() =>
        Assert.Empty(DroppedLinks.FromFile(@"C:\x\local.url", "[InternetShortcut]\r\nURL=file:///C:/x/f.zip\r\n"));

    [Fact]
    public void A_text_file_gives_the_links_in_it() =>
        Assert.Equal(new[] { "https://a.test/1.zip", "https://b.test/2.zip" },
            DroppedLinks.FromFile(@"C:\x\links.txt", "https://a.test/1.zip\nhttps://b.test/2.zip\n"));
}
