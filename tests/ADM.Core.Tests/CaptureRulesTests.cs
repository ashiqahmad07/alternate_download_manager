using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

public class CaptureRulesTests
{
    [Theory]
    [InlineData("https://a.com/f.zip", "f.zip", true)]
    [InlineData("https://a.com/F.ZIP", "F.ZIP", true)]
    [InlineData("https://a.com/movie.part.r01", "movie.part.r01", true)] // "r0*" in the default list
    [InlineData("https://a.com/page.html", "page.html", false)]
    [InlineData("https://a.com/noext", "noext", false)]
    public void Captures_the_listed_file_types(string url, string name, bool expected) =>
        Assert.Equal(expected, CaptureRules.ShouldCapture(new Settings(), url, name, -1));

    [Fact]
    public void Falls_back_to_the_url_path_when_the_name_is_unknown() =>
        Assert.True(CaptureRules.ShouldCapture(new Settings(), "https://a.com/dl/setup.exe?x=1", null, -1));

    [Fact]
    public void Uses_the_edited_type_list()
    {
        var s = new Settings { CaptureExtensions = "epub" };
        Assert.True(CaptureRules.ShouldCapture(s, "https://a.com/b.epub", "b.epub", -1));
        Assert.False(CaptureRules.ShouldCapture(s, "https://a.com/b.zip", "b.zip", -1));
    }

    [Fact]
    public void Skips_exception_sites_and_addresses()
    {
        var s = new Settings
        {
            CaptureExceptionSites = "*.update.microsoft.com",
            CaptureExceptionAddresses = "https://a.com/keep/*",
        };
        Assert.False(CaptureRules.ShouldCapture(s, "https://dl.update.microsoft.com/kb.exe", "kb.exe", -1));
        Assert.False(CaptureRules.ShouldCapture(s, "https://a.com/keep/f.zip", "f.zip", -1));
        Assert.True(CaptureRules.ShouldCapture(s, "https://a.com/other/f.zip", "f.zip", -1));
    }

    [Fact]
    public void Skips_files_known_to_be_smaller_than_the_minimum()
    {
        var s = new Settings { CaptureMinSizeKB = 100 };
        Assert.False(CaptureRules.ShouldCapture(s, "https://a.com/f.zip", "f.zip", 50 * 1024));
        Assert.True(CaptureRules.ShouldCapture(s, "https://a.com/f.zip", "f.zip", 200 * 1024));
        Assert.True(CaptureRules.ShouldCapture(s, "https://a.com/f.zip", "f.zip", -1));
    }

    [Theory]
    [InlineData("ftp://a.com/f.zip")]
    [InlineData("blob:https://a.com/1234")]
    [InlineData("data:application/zip;base64,AAAA")]
    public void Only_http_and_https_are_captured(string url) =>
        Assert.False(CaptureRules.ShouldCapture(new Settings(), url, "f.zip", -1));

    [Fact]
    public void Config_carries_the_rules_for_the_browser_extension()
    {
        var s = new Settings { CaptureExtensions = "zip  r0*", CaptureExceptionSites = "a.com", CaptureExceptionAddresses = "https://b.com/x/*", CaptureMinSizeKB = 7 };
        var config = CaptureRules.ToConfig(s);

        Assert.Equal(new[] { "zip", "r0*" }, config.Extensions);
        Assert.Equal("a.com", config.ExceptionSites);
        Assert.Equal("https://b.com/x/*", config.ExceptionAddresses);
        Assert.Equal(7, config.MinSizeKB);
    }

    [Fact]
    public void Config_reply_is_camel_case_json_for_the_extension()
    {
        var reply = JsonNode.Parse(CaptureRules.ConfigReply(new Settings { CaptureExtensions = "zip", CaptureMinSizeKB = 3 }))!;

        Assert.True((bool)reply["ok"]!);
        Assert.Equal("zip", (string)reply["config"]!["extensions"]![0]!);
        Assert.Equal(3, (long)reply["config"]!["minSizeKB"]!);
        Assert.NotNull(reply["config"]!["exceptionSites"]);
    }

    [Fact]
    public void Config_carries_the_special_keys_and_menu_items()
    {
        var s = new Settings { PreventCaptureKeys = "alt+ctrl", ForceOnLinkClick = false, MenuDownloadAllLinks = false };
        var config = JsonNode.Parse(CaptureRules.ConfigReply(s))!["config"]!;

        Assert.Equal(new[] { "ctrl", "alt" }, config["preventKeys"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(new[] { "insert" }, config["forceKeys"]!.AsArray().Select(k => (string)k!));
        Assert.False((bool)config["forceOnLinkClick"]!);
        Assert.True((bool)config["menuDownloadLink"]!);
        Assert.False((bool)config["menuDownloadAllLinks"]!);
    }

    [Fact]
    public void Config_carries_the_video_panel_settings()
    {
        var s = new Settings { VideoPanel = VideoPanelMode.Mini, VideoPanelMinSizeKB = 500, VideoPanelExceptionSites = "*.tube.example" };
        var config = JsonNode.Parse(CaptureRules.ConfigReply(s))!["config"]!;

        Assert.Equal("mini", (string)config["videoPanel"]!);
        Assert.Equal(500, (long)config["videoPanelMinSizeKB"]!);
        Assert.Equal("*.tube.example", (string)config["videoPanelExceptionSites"]!);
        Assert.Equal("full", CaptureRules.ToConfig(new Settings()).VideoPanel);
    }

    [Fact]
    public void Invalid_keys_are_sent_as_no_keys()
    {
        var config = CaptureRules.ToConfig(new Settings { PreventCaptureKeys = "Win" });
        Assert.Empty(config.PreventKeys);
    }

    // Decide: the defaults are Alt to prevent and Insert to force.
    [Fact]
    public void Without_special_keys_the_capture_rules_decide() =>
        Assert.True(CaptureRules.Decide(new Settings(), "https://a.com/f.zip", "f.zip", -1, Array.Empty<string>()));

    [Fact]
    public void Prevent_keys_skip_a_download_of_a_listed_type() =>
        Assert.False(CaptureRules.Decide(new Settings(), "https://a.com/f.zip", "f.zip", -1, new[] { "alt" }));

    [Fact]
    public void Force_keys_take_any_type_and_exception_sites()
    {
        var s = new Settings { CaptureExceptionSites = "a.com", CaptureMinSizeKB = 100 };
        Assert.True(CaptureRules.Decide(s, "https://b.com/page.html", "page.html", -1, new[] { "insert" }));
        Assert.True(CaptureRules.Decide(s, "https://a.com/f.zip", "f.zip", 10, new[] { "insert" }));
    }

    [Fact]
    public void Prevent_wins_when_both_are_held() =>
        Assert.False(CaptureRules.Decide(new Settings(), "https://a.com/f.zip", "f.zip", -1, new[] { "alt", "insert" }));

    [Fact]
    public void Force_keys_do_not_capture_other_schemes() =>
        Assert.False(CaptureRules.Decide(new Settings(), "ftp://a.com/f.zip", "f.zip", -1, new[] { "insert" }));

    [Fact]
    public void Exception_for_a_whole_site_adds_its_host()
    {
        var s = new Settings { CaptureExceptionSites = "x.com" };

        Assert.True(CaptureRules.AddException(s, "https://dl.a.com/f.zip?t=1", wholeSite: true));
        Assert.False(CaptureRules.AddException(s, "https://dl.a.com/other.zip", wholeSite: true));

        Assert.Equal("x.com dl.a.com", s.CaptureExceptionSites);
        Assert.False(CaptureRules.ShouldCapture(s, "https://dl.a.com/g.zip", "g.zip", -1));
    }

    [Fact]
    public void Exception_for_one_address_adds_only_that_address()
    {
        var s = new Settings();

        Assert.True(CaptureRules.AddException(s, "https://a.com/f.zip?t=1", wholeSite: false));
        Assert.False(CaptureRules.AddException(s, "https://a.com/f.zip?t=1", wholeSite: false));

        Assert.Equal("https://a.com/f.zip?t=1", s.CaptureExceptionAddresses);
        Assert.False(CaptureRules.ShouldCapture(s, "https://a.com/f.zip?t=1", "f.zip", -1));
        Assert.True(CaptureRules.ShouldCapture(s, "https://a.com/f.zip?t=2", "f.zip", -1));
    }
}

public class CaptureCancelsTests
{
    [Fact]
    public void The_second_cancel_of_an_address_asks_then_counting_starts_again()
    {
        var cancels = new CaptureCancels();

        Assert.False(cancels.Cancelled("https://a.com/f.zip"));
        Assert.False(cancels.Cancelled("https://a.com/g.zip"));
        Assert.True(cancels.Cancelled("https://a.com/f.zip"));
        Assert.False(cancels.Cancelled("https://a.com/f.zip"));
        Assert.True(cancels.Cancelled("https://a.com/g.zip"));
    }
}
