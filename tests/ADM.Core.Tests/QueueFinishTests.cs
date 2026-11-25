using ADM.Core;

namespace ADM.Core.Tests;

public class QueueFinishTests
{
    [Fact]
    public void Shutdown_forces_programs_closed_only_when_asked()
    {
        // shutdown.exe implies /f for any delay above 0, so the delay must be 0 for "don't force" to hold.
        Assert.Equal(new[] { "/s", "/t", "0" }, QueueFinish.ShutdownArguments(force: false));
        Assert.Equal(new[] { "/s", "/t", "0", "/f" }, QueueFinish.ShutdownArguments(force: true));
    }

    [Fact]
    public void Only_an_existing_file_given_by_its_full_path_runs()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.True(QueueFinish.CanRun(file));
            Assert.False(QueueFinish.CanRun(file + ".missing"));
            Assert.False(QueueFinish.CanRun(Path.GetFileName(file)));
            Assert.False(QueueFinish.CanRun(""));
            Assert.False(QueueFinish.CanRun(null));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Older_schedule_loads_with_nothing_extra_to_do()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Queues": [ { "Id": "main", "Name": "Main queue", "Schedule": { "OnComplete": "Shutdown" } } ] }""");
        try
        {
            var main = Store.Load(path).Queues[0];

            Assert.Equal(CompletionAction.Shutdown, main.Schedule.OnComplete);
            Assert.False(main.Schedule.ForceCloseApps);
            Assert.Null(main.Schedule.RunWhenDone);
            Assert.True(main.LimitRetries);
            Assert.False(main.StartOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
