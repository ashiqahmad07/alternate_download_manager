using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberRewriterTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "adm-rewrite");
    static readonly Uri Page = new("http://h.test/dir/page.html");
    static readonly string PagePath = Path.Combine(Root, "h.test", "dir", "page.html");

    static string At(params string[] parts) => Path.Combine(new[] { Root, "h.test" }.Concat(parts).ToArray());

    // Saved: /dir/a.png, /dir/sub/b.png, /c.css, /a b.png and /x&y.png, as themselves.
    static string? Saved(Uri address) => address.AbsolutePath switch
    {
        "/dir/a.png" => At("dir", "a.png"),
        "/dir/sub/b.png" => At("dir", "sub", "b.png"),
        "/c.css" => At("c.css"),
        "/a%20b.png" => At("a b.png"),
        "/x&y.png" => At("x&y.png"),
        "/other.html" when address.Query == "?id=1" => At("other_id=1.html"),
        "/dir/page.html" => PagePath,
        _ => null,
    };

    static string Rewrite(string text, GrabberKind kind = GrabberKind.Page) =>
        Encoding.UTF8.GetString(GrabberRewriter.Rewrite(Encoding.UTF8.GetBytes(text), "utf-8", kind, Page, PagePath, Saved));

    [Theory]
    [InlineData("<a href=\"a.png\">x</a>", "<a href=\"a.png\">x</a>")]
    [InlineData("<a href=\"/dir/a.png\">x</a>", "<a href=\"a.png\">x</a>")]
    [InlineData("<a href=\"http://h.test/dir/a.png\">x</a>", "<a href=\"a.png\">x</a>")]
    [InlineData("<a href='http://h.test/dir/sub/b.png'>x</a>", "<a href='sub/b.png'>x</a>")]
    [InlineData("<img src=/dir/a.png>", "<img src=a.png>")]
    public void Saved_file_becomes_relative(string before, string after) => Assert.Equal(after, Rewrite(before));

    [Theory]
    [InlineData("<a href=\"/gone.png\">x</a>", "<a href=\"http://h.test/gone.png\">x</a>")]
    [InlineData("<a href=\"elsewhere.png\">x</a>", "<a href=\"http://h.test/dir/elsewhere.png\">x</a>")]
    [InlineData("<a href=\"http://other.test/f?a=1\">x</a>", "<a href=\"http://other.test/f?a=1\">x</a>")]
    [InlineData("<a href=\"//other.test/f\">x</a>", "<a href=\"http://other.test/f\">x</a>")]
    public void Other_link_becomes_absolute(string before, string after) => Assert.Equal(after, Rewrite(before));

    [Fact]
    public void Relative_paths_climb_folders()
    {
        Assert.Equal("<link rel=stylesheet href=\"../c.css\">", Rewrite("<link rel=stylesheet href=\"/c.css\">"));
    }

    [Theory]
    [InlineData("<a href=\"a.png#top\">x</a>", "<a href=\"a.png#top\">x</a>")]
    [InlineData("<a href=\"/gone.html#top\">x</a>", "<a href=\"http://h.test/gone.html#top\">x</a>")]
    [InlineData("<a href=\"#top\">x</a>", "<a href=\"#top\">x</a>")]
    public void Fragment_is_kept(string before, string after) => Assert.Equal(after, Rewrite(before));

    [Theory]
    [InlineData("<a href=\"mailto:a@b.test\">x</a>")]
    [InlineData("<a href=\"javascript:go()\">x</a>")]
    [InlineData("<a href=\"data:text/html,x\">x</a>")]
    [InlineData("<a name=x>y</a>")]
    [InlineData("<p>nothing here</p>")]
    public void Other_schemes_are_left_alone(string text) => Assert.Equal(text, Rewrite(text));

    [Fact]
    public void Special_characters_are_escaped()
    {
        Assert.Equal("<a href=\"../a%20b.png\">x</a>", Rewrite("<a href=\"/a%20b.png\">x</a>"));
        Assert.Equal("<a href=../x%26y.png>x</a>", Rewrite("<a href=/x&amp;y.png>x</a>"));
    }

    [Fact]
    public void Query_in_an_absolute_link_is_written_as_an_entity()
    {
        Assert.Equal("<a href=\"http://other.test/f?a=1&amp;b=2\">x</a>", Rewrite("<a href=\"http://other.test/f?a=1&amp;b=2\">x</a>"));
        Assert.Equal("<a href=\"http://other.test/f?a=1&amp;b=2\">x</a>", Rewrite("<a href=\"http://other.test/f?a=1&b=2\">x</a>"));
    }

    [Theory]
    [InlineData("<a href=/gone?x='y>x</a>")]
    [InlineData("<a href=\"/gone?x=)(\">x</a>")]
    public void A_link_cannot_end_its_attribute(string text)
    {
        var after = Rewrite(text);
        Assert.DoesNotContain("'y", after);
        Assert.DoesNotContain(")(", after);
    }

    [Fact]
    public void A_link_to_a_saved_page_with_a_query_goes_to_its_file()
    {
        Assert.Equal("<a href=../other_id%3D1.html>x</a>", Rewrite("<a href=/other.html?id=1>x</a>"));
    }

    [Fact]
    public void Srcset_candidates_are_each_converted()
    {
        Assert.Equal("<img srcset=\"a.png 1x, sub/b.png 2x, http://h.test/gone.png 3x\">", Rewrite("<img srcset=\"/dir/a.png 1x, /dir/sub/b.png 2x, /gone.png 3x\">"));
    }

    [Fact]
    public void Style_attributes_and_blocks_are_converted()
    {
        Assert.Equal("<div style='background:url(a.png)'></div><style>b{background:url(\"http://h.test/gone.png\")}</style>",
            Rewrite("<div style='background:url(/dir/a.png)'></div><style>b{background:url(\"/gone.png\")}</style>"));
    }

    [Fact]
    public void Query_where_entities_mean_nothing_is_written_as_it_is()
    {
        const string css = "a{background:url(http://other.test/f?a=1&b=2)}";
        Assert.Equal(css, Rewrite(css, GrabberKind.Style));
        Assert.Equal($"<style>{css}</style>", Rewrite($"<style>{css}</style>"));
        Assert.Equal("<div style='background:url(http://other.test/f?a=1&amp;b=2)'></div>", Rewrite("<div style='background:url(http://other.test/f?a=1&b=2)'></div>"));
    }

    [Fact]
    public void An_entity_in_a_stylesheet_is_only_text()
    {
        Assert.Equal("a{background:url(http://h.test/f?a=1&amp;b=2)}", Rewrite("a{background:url(/f?a=1&amp;b=2)}", GrabberKind.Style));
        Assert.Equal("<style>a{background:url(http://h.test/f?a=1&amp;b=2)}</style>", Rewrite("<style>a{background:url(/f?a=1&amp;b=2)}</style>"));
    }

    [Fact]
    public void A_non_ascii_link_in_a_stylesheet_is_read_by_its_encoding()
    {
        var after = GrabberRewriter.Rewrite(Encoding.UTF8.GetBytes("a{background:url(/café.png)}"), "utf-8", GrabberKind.Style, Page, PagePath,
            uri => uri.AbsolutePath == "/caf%C3%A9.png" ? At("café.png") : null);
        Assert.Equal("a{background:url(../caf%C3%A9.png)}", Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void Css_urls_are_converted()
    {
        Assert.Equal("@import 'http://h.test/more.css'; a{background:url(a.png)}", Rewrite("@import '/more.css'; a{background:url(/dir/a.png)}", GrabberKind.Style));
    }

    [Fact]
    public void Base_is_emptied()
    {
        Assert.Equal("<base href=\"\"><a href=\"sub/b.png\">x</a>", Rewrite("<base href=\"/dir/sub/\"><a href=\"b.png\">x</a>"));
    }

    [Fact]
    public void Links_are_read_from_the_base()
    {
        Assert.Equal("<a href=\"http://h.test/other/gone.png\">x</a><base href=\"\">", Rewrite("<a href=\"gone.png\">x</a><base href=\"/other/\">"));
    }

    [Theory]
    [InlineData("<script src=/dir/a.png integrity=sha384-abc crossorigin=anonymous></script>", "<script src=a.png  ></script>")]
    [InlineData("<link rel=stylesheet href=/c.css integrity='sha384-abc' crossorigin>", "<link rel=stylesheet href=../c.css  >")]
    public void Integrity_goes_when_local(string before, string after) => Assert.Equal(after, Rewrite(before));

    [Fact]
    public void Integrity_goes_when_it_comes_first()
    {
        Assert.Equal("<script  src=a.png></script>", Rewrite("<script integrity=sha384-abc src=/dir/a.png></script>"));
    }

    [Fact]
    public void Brackets_in_a_css_url_are_escaped()
    {
        Assert.Equal("a{background:url('http://h.test/a%281%29.png')}", Rewrite("a{background:url('/a(1).png')}", GrabberKind.Style));
    }

    [Fact]
    public void Integrity_stays_when_remote()
    {
        Assert.Equal("<script src=http://other.test/a.js integrity=sha384-abc></script>", Rewrite("<script src=http://other.test/a.js integrity=sha384-abc></script>"));
    }

    [Fact]
    public void Scripts_are_left_alone()
    {
        const string text = "<script>var a='/dir/a.png', b='/gone.zip'</script>";
        Assert.Equal(text, Rewrite(text));
        const string file = "var a='/dir/a.png'";
        Assert.Equal(file, Rewrite(file, GrabberKind.Script));
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("shift_jis")]
    [InlineData("iso-8859-2")]
    public void Rewriting_keeps_every_other_byte(string charset)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(charset);
        var text = encoding.GetBytes($"<title>é</title><p>café à </p>");
        var before = text.Concat(Encoding.ASCII.GetBytes("<a href=\"/dir/a.png\">x</a>")).Concat(text).ToArray();
        var after = GrabberRewriter.Rewrite(before, charset, GrabberKind.Page, Page, PagePath, Saved);
        Assert.Equal(text.Concat(Encoding.ASCII.GetBytes("<a href=\"a.png\">x</a>")).Concat(text).ToArray(), after);
    }

    [Fact]
    public void Rewriting_keeps_invalid_utf8()
    {
        var junk = new byte[] { 0xFF, 0xFE + 1, 0xC3, 0x28, 0xE2, 0x82, 0x00, 0x80 };
        var before = Encoding.ASCII.GetBytes("x").Concat(junk).Concat(Encoding.ASCII.GetBytes("<a href=/dir/a.png>x</a>")).Concat(junk).ToArray();
        var after = GrabberRewriter.Rewrite(before, "utf-8", GrabberKind.Page, Page, PagePath, Saved);
        Assert.Equal(Encoding.ASCII.GetBytes("x").Concat(junk).Concat(Encoding.ASCII.GetBytes("<a href=a.png>x</a>")).Concat(junk).ToArray(), after);
    }

    [Fact]
    public void Rewriting_keeps_a_byte_order_mark()
    {
        var mark = new byte[] { 0xEF, 0xBB, 0xBF };
        var before = mark.Concat(Encoding.ASCII.GetBytes("<a href=/dir/a.png>x</a>")).ToArray();
        var after = GrabberRewriter.Rewrite(before, null, GrabberKind.Page, Page, PagePath, Saved);
        Assert.Equal(mark.Concat(Encoding.ASCII.GetBytes("<a href=a.png>x</a>")).ToArray(), after);
    }

    [Fact]
    public void Rewriting_keeps_utf16()
    {
        var before = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<p>é</p><a href=/dir/a.png>x</a>")).ToArray();
        var after = GrabberRewriter.Rewrite(before, null, GrabberKind.Page, Page, PagePath, Saved);
        Assert.Equal(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<p>é</p><a href=a.png>x</a>")).ToArray(), after);
    }

    [Fact]
    public void A_non_ascii_saved_name_is_escaped()
    {
        var after = GrabberRewriter.Rewrite(Encoding.UTF8.GetBytes("<a href=/café.png>x</a>"), "utf-8", GrabberKind.Page, Page, PagePath,
            uri => uri.AbsolutePath == "/caf%C3%A9.png" ? At("café.png") : null);
        Assert.Equal("<a href=../caf%C3%A9.png>x</a>", Encoding.ASCII.GetString(after));
    }

    [Fact]
    public void A_non_ascii_host_is_written_as_ascii()
    {
        var after = GrabberRewriter.Rewrite(Encoding.UTF8.GetBytes("<a href=http://café.test/x>x</a>"), "utf-8", GrabberKind.Page, Page, PagePath, _ => null);
        Assert.Equal("<a href=http://xn--caf-dma.test/x>x</a>", Encoding.ASCII.GetString(after));
    }

    [Fact]
    public void Same_text_gives_the_same_bytes_again()
    {
        const string text = "<a href=\"/dir/a.png\">x</a>";
        Assert.Equal(Rewrite(text), Rewrite(text));
    }

    [Fact]
    public void A_page_with_no_links_is_given_back()
    {
        var bytes = Encoding.UTF8.GetBytes("<p>no links</p>");
        Assert.Same(bytes, GrabberRewriter.Rewrite(bytes, "utf-8", GrabberKind.Page, Page, PagePath, Saved));
    }
}
