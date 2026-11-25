using System.IO.Pipes;
using System.Text;

namespace ADM.Core;

/// <summary>
/// Local, current-user-only named pipe that carries one JSON line per request from the
/// browser native-messaging host (or a second app instance) to the running app.
/// </summary>
public static class IngestPipe
{
    public const string Name = "ADM.Ingest";

    public static async Task RunServerAsync(Func<string, Task<string>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            _ = ServeAsync(server, handle, ct);
        }
    }

    static async Task ServeAsync(NamedPipeServerStream server, Func<string, Task<string>> handle, CancellationToken ct)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                if (line != null) await writer.WriteLineAsync(await handle(line));
            }
            catch (IOException)
            {
                // Client went away mid-request.
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Sends one JSON line and returns the reply; throws <see cref="TimeoutException"/> when no server is listening.</summary>
    public static async Task<string?> SendAsync(string jsonLine, int connectTimeoutMs, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(connectTimeoutMs, ct);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(jsonLine.AsMemory(), ct);
        return await reader.ReadLineAsync(ct);
    }
}
