using System.Diagnostics;
using System.Text.Json;

// Stands in for a virus scanner in the tests. Told what to do through environment variables:
//   ADM_TOOL_LOG     where to record, as JSON, its arguments, its process id, which arguments are existing files
//                       and which of those carry a Zone.Identifier stream; "<log>.exited" is written just before it exits
//   ADM_TOOL_WAIT    milliseconds to wait before exiting
//   ADM_TOOL_REMOVE  "1" deletes the file named by the last argument, as a scanner quarantining it does
//   ADM_TOOL_EXIT    the exit code, 0 by default

var log = Environment.GetEnvironmentVariable("ADM_TOOL_LOG");
if (log != null)
{
    var record = new
    {
        Args = args,
        Pid = Environment.ProcessId,
        Existing = args.Where(File.Exists).ToArray(),
        Marked = args.Where(a => File.Exists(a) && File.Exists(a + ":Zone.Identifier")).ToArray(),
    };
    File.WriteAllText(log, JsonSerializer.Serialize(record));
}

if (int.TryParse(Environment.GetEnvironmentVariable("ADM_TOOL_WAIT"), out var wait)) Thread.Sleep(wait);

if (Environment.GetEnvironmentVariable("ADM_TOOL_REMOVE") == "1" && args.Length > 0) File.Delete(args[^1]);

if (log != null) File.WriteAllText(log + ".exited", Stopwatch.GetTimestamp().ToString());

return int.TryParse(Environment.GetEnvironmentVariable("ADM_TOOL_EXIT"), out var code) ? code : 0;
