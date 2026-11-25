namespace ADM.Core.Tests;

/// <summary>The tray icon is the app's own, read as files: the app can't be loaded from here.</summary>
public sealed class TrayTests
{
    [Fact]
    public void The_app_does_without_Windows_Forms()
    {
        // About 20 MB of modules, loaded for the tray icon alone until TrayIcon replaced it.
        Assert.DoesNotContain("UseWindowsForms", File.ReadAllText(Path.Combine(AppIconTests.AppFolder, "ADM.App.csproj")));
        foreach (var file in Directory.GetFiles(AppIconTests.AppFolder, "*.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.False(code.Contains("System.Windows.Forms") || code.Contains("System.Drawing"), $"{Path.GetFileName(file)} names Windows Forms or System.Drawing");
        }
    }

    [Fact]
    public void The_tray_window_is_not_one_of_WPF_s()
    {
        // WPF's HwndSource starts its drawing on the graphics card, Direct3D 11 and the card's driver among it,
        // even for a hidden window: about 13 MB more working set, and 8 MB more private memory, while ADM waits in the tray.
        var code = File.ReadAllText(Path.Combine(AppIconTests.AppFolder, "TrayIcon.cs"));
        Assert.DoesNotContain("new HwndSource", code);
        Assert.Contains("CreateWindowEx", code);
    }
}
