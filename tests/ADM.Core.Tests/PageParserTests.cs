using System.Diagnostics;
using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class PageParserTests
{
    static List<(string Value, PageLinkKind Kind)> Links(string html) =>
        PageParser.Html(html).Select(l => (l.Value, l.Kind)).ToList();

    static void AssertSpansCutOutTheValues(string html)
    {
        foreach (var link in PageParser.Html(html))
            Assert.Contains(html.Substring(link.Start, link.Length), new[] { link.Value, System.Net.WebUtility.HtmlEncode(link.Value) });
    }

    [Theory]
    [InlineData("<a href='x'>", PageLinkKind.Link)]
    [InlineData("<area href='x'>", PageLinkKind.Link)]
    [InlineData("<img src='x'>", PageLinkKind.Embedded)]
    [InlineData("<source src='x'>", PageLinkKind.Embedded)]
    [InlineData("<video src='x'>", PageLinkKind.Embedded)]
    [InlineData("<video poster='x'>", PageLinkKind.Embedded)]
    [InlineData("<audio src='x'>", PageLinkKind.Embedded)]
    [InlineData("<track src='x'>", PageLinkKind.Embedded)]
    [InlineData("<embed src='x'>", PageLinkKind.Embedded)]
    [InlineData("<object data='x'>", PageLinkKind.Embedded)]
    [InlineData("<input type=image src='x'>", PageLinkKind.Embedded)]
    [InlineData("<body background='x'>", PageLinkKind.Embedded)]
    [InlineData("<td background='x'>", PageLinkKind.Embedded)]
    [InlineData("<img data-src='x'>", PageLinkKind.Embedded)]
    [InlineData("<img data-original='x'>", PageLinkKind.Embedded)]
    [InlineData("<img data-lazy-src='x'>", PageLinkKind.Embedded)]
    [InlineData("<script src='x'></script>", PageLinkKind.Script)]
    [InlineData("<frame src='x'>", PageLinkKind.Frame)]
    [InlineData("<iframe src='x'></iframe>", PageLinkKind.Frame)]
    [InlineData("<base href='x'>", PageLinkKind.Base)]
    [InlineData("<link rel=stylesheet href='x'>", PageLinkKind.Style)]
    [InlineData("<link rel='Style Sheet' href='y'>", null)]
    [InlineData("<link rel=icon href='x'>", PageLinkKind.Embedded)]
    [InlineData("<link rel='shortcut icon' href='x'>", PageLinkKind.Embedded)]
    [InlineData("<link rel=preload href='x'>", PageLinkKind.Embedded)]
    [InlineData("<link rel=alternate href='x'>", PageLinkKind.Link)]
    [InlineData("<link rel=next href='x'>", PageLinkKind.Link)]
    [InlineData("<link rel=prev href='x'>", PageLinkKind.Link)]
    [InlineData("<link rel=canonical href='y'>", null)]
    [InlineData("<link rel=preconnect href='y'>", null)]
    [InlineData("<input src='y'>", null)]
    [InlineData("<div src='y' href='y'>", null)]
    public void Reads_each_element(string html, PageLinkKind? kind)
    {
        if (kind == null) Assert.Empty(Links(html));
        else Assert.Equal(new[] { ("x", kind.Value) }, Links(html));
    }

    [Theory]
    [InlineData("<A HREF=x>")]
    [InlineData("<a href = x >")]
    [InlineData("<a href=x />")]
    [InlineData("<a\nhref\n=\n\"x\"\n>")]
    [InlineData("<a class=c href=\"x\" id=i>")]
    [InlineData("<a href=x href=y>")]
    [InlineData("<a href=' x '>")]
    [InlineData("<a data-a='>' href=x>")]
    [InlineData("<a href=\"\nx\t\">")]
    public void Reads_attributes_however_they_are_written(string html)
    {
        Assert.Equal("x", Assert.Single(PageParser.Html(html)).Value);
    }

    [Fact]
    public void Entities_are_decoded()
    {
        var link = Assert.Single(PageParser.Html("<a href='p?a=1&amp;b=2&#38;c=3'>t</a>"));
        Assert.Equal("p?a=1&b=2&c=3", link.Value);
    }

    [Fact]
    public void Spans_cut_out_the_raw_value()
    {
        const string html = "<a href='a.html'>a</a><a href=b.html>b</a><a href=\"c.html\" x=1>c</a><img src=d.png>";
        Assert.Equal(new[] { "a.html", "b.html", "c.html", "d.png" }, PageParser.Html(html).Select(l => html.Substring(l.Start, l.Length)));
        AssertSpansCutOutTheValues(html);
    }

    [Fact]
    public void Span_of_an_entity_covers_it_as_written()
    {
        const string html = "<a href='a&amp;b'>";
        var link = Assert.Single(PageParser.Html(html));
        Assert.Equal("a&amp;b", html.Substring(link.Start, link.Length));
    }

    [Fact]
    public void Srcset_gives_each_candidate()
    {
        const string html = "<img srcset=\"a.png 1x, b,c.png 2x,d.png\">";
        var links = PageParser.Html(html);
        Assert.Equal(new[] { "a.png", "b,c.png", "d.png" }, links.Select(l => l.Value));
        Assert.All(links, l => Assert.Equal(PageLinkKind.Embedded, l.Kind));
        Assert.Equal(new[] { "a.png", "b,c.png", "d.png" }, links.Select(l => html.Substring(l.Start, l.Length)));
    }

    [Theory]
    [InlineData("<meta http-equiv=refresh content='5; url=x'>")]
    [InlineData("<meta http-equiv='Refresh' content=\"0;URL=x\">")]
    [InlineData("<meta http-equiv=refresh content=\"0; url='x'\">")]
    public void Meta_refresh_is_a_link(string html)
    {
        var link = Assert.Single(PageParser.Html(html));
        Assert.Equal(("x", PageLinkKind.Refresh), (link.Value, link.Kind));
        Assert.Equal("x", html.Substring(link.Start, link.Length));
    }

    [Theory]
    [InlineData("<meta http-equiv=refresh content='5'>")]
    [InlineData("<meta name=refresh content='0; url=y'>")]
    public void Other_meta_is_left_out(string html) => Assert.Empty(Links(html));

    [Theory]
    [InlineData("<!-- <a href=y> -->")]
    [InlineData("<!-- <a href=y>")]
    [InlineData("<textarea><a href=y></textarea>")]
    [InlineData("<TITLE><a href=y></TITLE>")]
    [InlineData("<!doctype html <a href=y>")]
    [InlineData("<?php <a href=y> ?>")]
    [InlineData("< a href=y>")]
    [InlineData("<a href")]
    [InlineData("<a href=")]
    [InlineData("<a href='y")]
    [InlineData("<a")]
    [InlineData("<")]
    [InlineData("")]
    public void Text_that_is_not_a_tag_gives_nothing(string html) => Assert.Empty(Links(html));

    [Fact]
    public void A_tag_in_a_script_is_not_a_tag()
    {
        Assert.Empty(Links("<script>document.write('<a href=y>')</script>"));
        Assert.Equal(new[] { ("x", PageLinkKind.Link) }, Links("<SCRIPT>'<a href=y>'</SCRIPT><a href=x>"));
    }

    [Fact]
    public void Noscript_is_read_since_no_script_runs()
    {
        Assert.Equal(new[] { ("x", PageLinkKind.Embedded) }, Links("<noscript><img src=x></noscript>"));
    }

    [Fact]
    public void Text_of_a_link_is_kept()
    {
        var links = PageParser.Html("<a href=x> Big <b>fat</b>\n&amp; file </a><img src=y><a href=z><img src=w></a>");
        Assert.Equal("Big fat & file", links[0].Text);
        Assert.Equal("", links[2].Text);
    }

    [Fact]
    public void Text_of_a_link_is_cut_short()
    {
        var link = PageParser.Html("<a href=x>" + new string('t', 5000) + "</a>")[0];
        Assert.Equal(200, link.Text.Length);
    }

    [Fact]
    public void Text_of_a_link_never_closed_is_kept()
    {
        var links = PageParser.Html("<a href=x>one<a href=y>two");
        Assert.Equal(new[] { "one", "two" }, links.Select(l => l.Text));
    }

    [Fact]
    public void Style_attribute_and_block_are_read_as_css()
    {
        const string html = "<div style='background:url(a.png)'></div><style>b{background:url(\"b.png\")}</style>";
        var links = PageParser.Html(html);
        Assert.Equal(new[] { "a.png", "b.png" }, links.Select(l => l.Value));
        Assert.Equal(new[] { "a.png", "b.png" }, links.Select(l => html.Substring(l.Start, l.Length)));
    }

    [Fact]
    public void Style_attribute_quoted_with_entities()
    {
        const string html = "<div style=\"background:url(&quot;a.png&quot;)\">";
        var link = Assert.Single(PageParser.Html(html));
        Assert.Equal("a.png", link.Value);
        Assert.Equal("a.png", html.Substring(link.Start, link.Length));
    }

    [Fact]
    public void Inline_script_is_screened_with_its_place()
    {
        const string html = "<script>load('http://a.test/b.zip')</script>";
        var link = Assert.Single(PageParser.Html(html));
        Assert.Equal(("http://a.test/b.zip", PageLinkKind.Link), (link.Value, link.Kind));
        Assert.True(link.ScriptText);
    }

    [Theory]
    [InlineData("a{b:url(x)}")]
    [InlineData("a{b:URL( x )}")]
    [InlineData("a{b:url('x')}")]
    [InlineData("a{b:url( \"x\" )}")]
    public void Css_url_in_any_quoting(string css)
    {
        var link = Assert.Single(PageParser.Css(css));
        Assert.Equal(("x", PageLinkKind.Embedded), (link.Value, link.Kind));
        Assert.Equal("x", css.Substring(link.Start, link.Length));
    }

    [Theory]
    [InlineData("@import 'x';")]
    [InlineData("@import \"x\" screen;")]
    [InlineData("@IMPORT url(x);")]
    [InlineData("@import url('x');")]
    public void Css_import_is_a_style(string css)
    {
        var link = Assert.Single(PageParser.Css(css));
        Assert.Equal(("x", PageLinkKind.Style), (link.Value, link.Kind));
        Assert.Equal("x", css.Substring(link.Start, link.Length));
    }

    [Theory]
    [InlineData("/* url(y) */")]
    [InlineData("/* url(y)")]
    [InlineData("a{b:'url(y)'}")]
    [InlineData("a{b:url(}")]
    [InlineData("a{b:url(data:image/png;base64,AA)}")]
    public void Css_without_a_link(string css)
    {
        Assert.DoesNotContain(PageParser.Css(css), l => l.Value == "y" || l.Value.Length == 0 || l.Value.StartsWith("data:"));
    }

    [Theory]
    [InlineData("x='http://a.test/b.zip'", "http://a.test/b.zip")]
    [InlineData("x=\"https://a.test/\"", "https://a.test/")]
    [InlineData("x=`ftp://a.test/b`", "ftp://a.test/b")]
    [InlineData("x='HTTP://a.test/b'", "HTTP://a.test/b")]
    [InlineData("x='//a.test/b.zip'", "//a.test/b.zip")]
    [InlineData("x='https:\\/\\/a.test\\/b.zip'", "https://a.test/b.zip")]
    [InlineData("x='/files/b.zip'", "/files/b.zip")]
    [InlineData("x='files/b.PDF'", "files/b.PDF")]
    [InlineData("x='b.html'", "b.html")]
    [InlineData("x='b.png?v=2'", "b.png?v=2")]
    public void Script_address_is_found(string script, string expected)
    {
        Assert.Equal(expected, Assert.Single(PageParser.Script(script)).Value);
    }

    [Theory]
    [InlineData("x='not an address'")]
    [InlineData("x='text/html'")]
    [InlineData("x='a.b.c'")]
    [InlineData("x='use strict'")]
    [InlineData("x='../${y}.png'")]
    [InlineData("x='<a href=y.png>'")]
    [InlineData("x='http://a b.test/c'")]
    [InlineData("x='javascript:alert(1)'")]
    [InlineData("x=`http://a.test/${y}`")]
    [InlineData("// http://a.test/b.zip")]
    [InlineData("x='")]
    [InlineData("x='a.png")]
    public void Script_without_an_address(string script) => Assert.Empty(PageParser.Script(script));

    [Fact]
    public void Apostrophe_in_a_comment_does_not_hide_the_next_line()
    {
        Assert.Equal("b.zip", Assert.Single(PageParser.Script("// don't\nx='b.zip'")).Value);
    }

    [Fact]
    public void The_real_encoding_decodes_a_value()
    {
        var link = Assert.Single(PageParser.Html("<a href='cafÃ©.html'>", raw => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw))));
        Assert.Equal("café.html", link.Value);
    }

    [Fact]
    public void An_entity_is_not_read_as_bytes_of_the_real_encoding()
    {
        var link = Assert.Single(PageParser.Html("<a href='caf&eacute;.html'>", raw => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw))));
        Assert.Equal("café.html", link.Value);
    }

    [Theory]
    [InlineData("<a ")]
    [InlineData("<a a=")]
    [InlineData("<a href=\"")]
    [InlineData("<!-- ")]
    [InlineData("<!x ")]
    [InlineData("</a ")]
    [InlineData("<script>")]
    [InlineData("<style>")]
    [InlineData("<textarea>")]
    [InlineData("<a href=x>")]
    [InlineData("<img srcset=\"a 1x,")]
    [InlineData("<img srcset=' , ")]
    [InlineData("<div style='url(")]
    [InlineData("<script>'")]
    [InlineData("<script>`")]
    [InlineData("<style>url(")]
    [InlineData("<style>/*")]
    public void Broken_page_is_read_in_proportion_to_its_size(string repeated)
    {
        var html = string.Concat(Enumerable.Repeat(repeated, 2 * 1024 * 1024 / repeated.Length));
        var clock = Stopwatch.StartNew();
        var links = PageParser.Html(html);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.NotNull(links);
    }

    [Theory]
    [InlineData("url(")]
    [InlineData("url('")]
    [InlineData("/*")]
    [InlineData("@import ")]
    [InlineData("@import '")]
    public void Broken_css_is_read_in_proportion_to_its_size(string repeated)
    {
        var css = string.Concat(Enumerable.Repeat(repeated, 2 * 1024 * 1024 / repeated.Length));
        var clock = Stopwatch.StartNew();
        PageParser.Css(css);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("`")]
    [InlineData("\\")]
    [InlineData("'\\")]
    [InlineData("'a.png")]
    public void Broken_script_is_read_in_proportion_to_its_size(string repeated)
    {
        var script = string.Concat(Enumerable.Repeat(repeated, 2 * 1024 * 1024 / repeated.Length));
        var clock = Stopwatch.StartNew();
        PageParser.Script(script);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_tag_with_too_many_attributes_keeps_its_first_ones()
    {
        var html = "<a href=x " + string.Concat(Enumerable.Repeat("a=1 ", 100_000)) + ">";
        Assert.Equal("x", Assert.Single(PageParser.Html(html)).Value);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, null, "utf-8")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, null, "utf-16")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, null, "utf-16BE")]
    [InlineData(new byte[] { 0x61 }, "windows-1252", "Windows-1252")]
    [InlineData(new byte[] { 0x61 }, "no-such-charset", "utf-8")]
    [InlineData(new byte[] { 0x61 }, null, "utf-8")]
    public void Encoding_comes_from_the_mark_then_the_header(byte[] bytes, string? charset, string expected)
    {
        Assert.Equal(expected, PageBody.From(bytes, charset).Encoding.WebName, ignoreCase: true);
    }

    [Theory]
    [InlineData("<meta charset=Shift_JIS>", "shift_jis")]
    [InlineData("<meta charset='windows-1252'>", "windows-1252")]
    [InlineData("<meta http-equiv=content-type content='text/html; charset=iso-8859-2'>", "iso-8859-2")]
    public void Encoding_comes_from_a_meta_if_the_header_says_nothing(string html, string expected)
    {
        Assert.Equal(expected, PageBody.From(Encoding.ASCII.GetBytes(html), null).Encoding.WebName, ignoreCase: true);
    }

    [Fact]
    public void A_meta_past_the_first_kilobyte_is_not_read()
    {
        var html = new string(' ', 1100) + "<meta charset=Shift_JIS>";
        Assert.Equal("utf-8", PageBody.From(Encoding.ASCII.GetBytes(html), null).Encoding.WebName, ignoreCase: true);
    }

    [Fact]
    public void Page_body_gives_back_its_bytes()
    {
        var bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        var body = PageBody.From(bytes, null);
        Assert.Equal(bytes, body.Encode(body.Text));
    }

    [Fact]
    public void Utf16_body_gives_back_its_bytes()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<a href='é.html'>")).ToArray();
        var body = PageBody.From(bytes, null);
        Assert.Equal("é.html", Assert.Single(PageParser.Html(body.Text, body.Decode)).Value);
        Assert.Equal(bytes, body.Encode(body.Text));
    }

    [Fact]
    public void Latin1_header_reads_a_value_by_it()
    {
        var bytes = Encoding.Latin1.GetBytes("<a href='café.html'>");
        var body = PageBody.From(bytes, "iso-8859-1");
        Assert.Equal("café.html", Assert.Single(PageParser.Html(body.Text, body.Decode)).Value);
    }
}
