using ADM.Core;

namespace ADM.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parses_idm_style_download_arguments()
    {
        var c = CommandLine.Parse(new[] { "/d", "http://a.com/f.zip", "/p", @"D:\Dl", "/f", "x.zip", "/n", "/a", "/q", "/h" });

        Assert.Equal("http://a.com/f.zip", c.Url);
        Assert.Equal(@"D:\Dl", c.Directory);
        Assert.Equal("x.zip", c.FileName);
        Assert.True(c.Silent);
        Assert.True(c.AddToQueueOnly);
        Assert.True(c.ExitWhenDone);
    }

    [Fact]
    public void Accepts_a_bare_url() =>
        Assert.Equal("http://a.com/f.zip", CommandLine.Parse(new[] { "http://a.com/f.zip" }).Url);

    [Fact]
    public void Flags_are_case_insensitive_and_accept_dashes()
    {
        var c = CommandLine.Parse(new[] { "-D", "http://a.com/f", "/S" });
        Assert.Equal("http://a.com/f", c.Url);
        Assert.True(c.StartQueue);
    }

    [Fact]
    public void Recognises_background_start()
    {
        var c = CommandLine.Parse(new[] { "--background" });
        Assert.True(c.Background);
        Assert.Null(c.Url);
    }

    [Fact]
    public void A_flag_missing_its_value_is_ignored() =>
        Assert.Null(CommandLine.Parse(new[] { "/d" }).Url);
}
