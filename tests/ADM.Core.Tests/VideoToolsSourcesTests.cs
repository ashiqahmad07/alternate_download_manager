using System.Text.RegularExpressions;

namespace ADM.Core.Tests;

/// <summary>
/// The installer carries the video tools that <c>scripts\fetch-video-tools.ps1</c> fetches, and puts them where the app
/// looks. Read as files: a script that fetched from somewhere else, or an installer that put them elsewhere, would only
/// show as the app asking to set up video downloads after all.
/// </summary>
public class VideoToolsSourcesTests
{
    static readonly string Root = RepoRoot();

    [Fact]
    public void The_installer_fetches_each_tool_from_where_the_app_does()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "fetch-video-tools.ps1"));
        var addresses = Regex.Matches(script, @"^\$(\w+) = ""(https://[^""]+)""", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => new Uri(m.Groups[2].Value));
        var official = ToolSources.Official;

        Assert.Equal(official.YtDlp, addresses["YtDlp"]);
        Assert.Equal(official.YtDlpSums, addresses["YtDlpSums"]);
        Assert.Equal(official.Deno, addresses["Deno"]);
        Assert.Equal(official.DenoSum, addresses["DenoSum"]);
        Assert.Equal(official.Ffmpeg, addresses["Ffmpeg"]);
        Assert.Equal(official.FfmpegSums, addresses["FfmpegSums"]);
    }

    [Fact]
    public void The_installer_puts_the_tools_where_the_app_looks_without_replacing_any()
    {
        var setup = File.ReadAllText(Path.Combine(Root, "installer", "ADM.iss"));
        var line = Regex.Match(setup, @"^Source: ""\{#Tools\}\\\*"";.*$", RegexOptions.Multiline);
        Assert.True(line.Success, "ADM.iss has no line installing {#Tools}");
        Assert.Contains(@"DestDir: ""{localappdata}\ADM\tools""", line.Value);
        // ADM keeps its own yt-dlp up to date there; an older one from setup mustn't replace it.
        Assert.Contains("onlyifdoesntexist", line.Value);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(localAppData, "ADM", "tools"), VideoTools.DefaultFolder, ignoreCase: true);
    }

    static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "ADM.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ADM.sln not found above the test folder");
    }
}
