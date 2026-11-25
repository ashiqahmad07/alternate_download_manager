using System.IO;
using System.Security;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>Starts ADM in the tray at sign-in through the current user's Run key.</summary>
static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ADM";
    const string OldValueName = "FluxDM";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Turns startup on the first time ADM runs, as IDM's is; once the user turns it off in Options, it stays off.</summary>
    public static void TurnOnTheFirstTime(DownloadManager manager)
    {
        try
        {
            if (manager.State.Settings.ApplyStartWithWindowsDefault(() => Set(true))) manager.MarkDirty();
        }
        catch (Exception problem) when (problem is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not worth stopping for: the next start tries again, and the box in Options still turns it on.
            Log.Error("Start with Windows could not be turned on.", problem);
        }
    }

    /// <summary>Replaces the entry made under the app's old name: it starts a program that has gone, and Options would show this as off.</summary>
    public static void TakeOverOldEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldValueName) == null) return;
            key.DeleteValue(OldValueName);
            Set(true);
        }
        catch (Exception problem) when (problem is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not worth stopping for: the box in Options still turns it on.
            Log.Error("The old startup entry could not be replaced.", problem);
        }
    }
}
