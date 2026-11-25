using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>The state file after a power cut: a broken or empty state.json gives way to the last good copy.</summary>
public sealed class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    static AppState StateWith(params string[] urls)
    {
        var state = new AppState();
        foreach (var url in urls) state.Downloads.Add(new DownloadItem { Url = url });
        return state;
    }

    [Fact]
    public void Old_state_loads_with_no_projects()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Downloads": [ { "Url": "http://a.example/1.zip" } ] }""");

        var state = Store.Load(StatePath);

        Assert.Equal("http://a.example/1.zip", Assert.Single(state.Downloads).Url);
        Assert.NotNull(state.GrabberProjects);
        Assert.Empty(state.GrabberProjects);
    }

    [Fact]
    public void Cut_short_state_falls_back_to_the_last_good_copy()
    {
        Store.Save(StatePath, StateWith("http://a.example/1.zip"));
        Store.Save(StatePath, StateWith("http://a.example/1.zip", "http://a.example/2.zip"));
        // After a power cut, NTFS can keep a file's new length while its data never arrived.
        File.WriteAllBytes(StatePath, new byte[new FileInfo(StatePath).Length]);

        var state = Store.Load(StatePath);

        Assert.Equal("http://a.example/1.zip", Assert.Single(state.Downloads).Url);
        Assert.True(File.Exists(StatePath + ".corrupt"));
    }

    [Fact]
    public void Empty_state_falls_back_to_the_last_good_copy()
    {
        Store.Save(StatePath, StateWith("http://a.example/1.zip"));
        Store.Save(StatePath, StateWith("http://a.example/1.zip", "http://a.example/2.zip"));
        File.WriteAllBytes(StatePath, Array.Empty<byte>());

        var state = Store.Load(StatePath);

        Assert.Equal("http://a.example/1.zip", Assert.Single(state.Downloads).Url);
    }

    [Fact]
    public void Missing_state_with_a_backup_loads_the_backup()
    {
        // A replace cut off between its two renames can leave only the backup.
        Store.Save(StatePath, StateWith("http://a.example/1.zip"));
        Store.Save(StatePath, StateWith("http://a.example/1.zip", "http://a.example/2.zip"));
        File.Delete(StatePath);

        Assert.Equal("http://a.example/1.zip", Assert.Single(Store.Load(StatePath).Downloads).Url);
    }

    [Fact]
    public void Save_keeps_the_previous_state_as_the_backup()
    {
        Store.Save(StatePath, StateWith("http://a.example/1.zip"));
        Assert.False(File.Exists(StatePath + ".bak"));
        Store.Save(StatePath, StateWith("http://a.example/2.zip"));

        Assert.Equal("http://a.example/2.zip", Assert.Single(Store.Load(StatePath).Downloads).Url);
        Assert.Equal("http://a.example/1.zip", Assert.Single(Store.Load(StatePath + ".bak").Downloads).Url);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }
}
