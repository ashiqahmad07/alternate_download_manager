using ADM.Core;

namespace ADM.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Server_limit_lowers_the_requested_connections_for_matching_sites()
    {
        var s = new Settings { ServerLimits = { new ServerLimit { Sites = "*.slow.net a.com", Connections = 2 } } };

        Assert.Equal(2, s.ConnectionsFor("https://cdn.slow.net/f.zip", 8));
        Assert.Equal(2, s.ConnectionsFor("https://a.com/f.zip", 8));
        Assert.Equal(8, s.ConnectionsFor("https://b.com/f.zip", 8));
        Assert.Equal(1, s.ConnectionsFor("https://a.com/f.zip", 1));
    }

    [Fact]
    public void Older_state_without_browser_settings_loads_the_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 4 } }""");
        try
        {
            var s = Store.Load(path).Settings;

            Assert.Equal(4, s.DefaultConnections);
            Assert.Equal("Alt", s.PreventCaptureKeys);
            Assert.Equal("Insert", s.ForceCaptureKeys);
            Assert.True(s.ForceOnLinkClick);
            Assert.True(s.PromptAfterTwoCancels);
            Assert.True(s.MenuDownloadLink);
            Assert.True(s.MenuDownloadAllLinks);
            Assert.Empty(s.Sounds);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Start_with_Windows_is_turned_on_the_first_time_only()
    {
        var s = new Settings();
        var turnedOn = 0;

        Assert.True(s.ApplyStartWithWindowsDefault(() => turnedOn++));
        // The user turned it off in Options: the next start leaves it off.
        Assert.False(s.ApplyStartWithWindowsDefault(() => turnedOn++));

        Assert.Equal(1, turnedOn);
        Assert.True(s.StartWithWindowsDefaultApplied);
    }

    [Fact]
    public void A_startup_entry_that_could_not_be_written_is_tried_again_at_the_next_start()
    {
        var s = new Settings();

        Assert.Throws<UnauthorizedAccessException>(() => s.ApplyStartWithWindowsDefault(() => throw new UnauthorizedAccessException()));
        Assert.False(s.StartWithWindowsDefaultApplied);

        var turnedOn = 0;
        Assert.True(s.ApplyStartWithWindowsDefault(() => turnedOn++));
        Assert.Equal(1, turnedOn);
    }

    [Fact]
    public void State_from_before_the_default_turns_Start_with_Windows_on_once_more()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 4 } }""");
        try
        {
            var s = Store.Load(path).Settings;
            Assert.False(s.StartWithWindowsDefaultApplied);

            s.ApplyStartWithWindowsDefault(() => { });
            Store.Save(path, new AppState { Settings = s });

            Assert.True(Store.Load(path).Settings.StartWithWindowsDefaultApplied);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Sounds_survive_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var state = new AppState();
            state.Settings.Sounds.Add(new EventSound { Event = SoundEvent.QueueStarted, On = true, File = @"C:\Sounds\start.wav" });
            Store.Save(path, state);

            var sound = Assert.Single(Store.Load(path).Settings.Sounds);

            Assert.Equal(SoundEvent.QueueStarted, sound.Event);
            Assert.True(sound.On);
            Assert.Equal(@"C:\Sounds\start.wav", sound.File);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
