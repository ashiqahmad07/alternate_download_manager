namespace ADM.Core.Tests;

/// <summary>The app's icon, read as a file: the app can't be loaded from here. Looking at it is for <c>scripts\make-icons.py</c>'s user.</summary>
public class AppIconTests
{
    internal static readonly string AppFolder =Path.Combine(RepoRoot(), "src", "ADM.App");

    [Fact]
    public void Icon_has_each_size_Windows_asks_for()
    {
        // At display scales from 100% to 200%, and 256 for Explorer's big views.
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, Sizes(Path.Combine(AppFolder, "Assets", "ADM.ico")));
    }

    [Fact]
    public void The_app_asks_for_the_icon_by_the_name_it_was_built_in_under()
    {
        // A different name would only show as the app failing to start.
        const string name = "\"ADM.App.Assets.ADM.ico\"";
        Assert.Contains($"LogicalName={name}", File.ReadAllText(Path.Combine(AppFolder, "ADM.App.csproj")));
        Assert.Contains($"Resource = {name}", File.ReadAllText(Path.Combine(AppFolder, "AppIcon.cs")));
    }

    /// <summary>The widths in an icon file's directory, each checked to be square. In a width's one byte, 0 means 256.</summary>
    static int[] Sizes(string file)
    {
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
        return Enumerable.Range(0, BitConverter.ToUInt16(bytes, 4)).Select(i =>
        {
            int entry = 6 + i * 16;
            Assert.Equal(bytes[entry], bytes[entry + 1]);
            return bytes[entry] == 0 ? 256 : bytes[entry];
        }).OrderBy(size => size).ToArray();
    }

    static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "ADM.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ADM.sln not found above the test folder");
    }
}
