using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ADM.Core.Tests;

/// <summary>
/// An FTP server in the test process, serving one payload at <see cref="FilePath"/> in passive mode.
/// Each data connection is held to <see cref="PerConnectionKBps"/>, so more connections are faster,
/// and the switches make it behave like the servers ADM meets: no REST, a connection limit, a dropped
/// data connection, no EPSV, a PASV reply naming another host, a changing file, a required login.
/// </summary>
sealed class FtpTestServer : IAsyncDisposable
{
    public const string FilePath = "/pub/file.bin";

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentBag<Task> _sessions = new();
    readonly ConcurrentQueue<long> _restarts = new();
    readonly ConcurrentQueue<string> _users = new();
    int _controls, _controlsEver, _refusedControls, _transfers, _maxTransfers, _logins, _drops;
    int _perConnectionKBps = 512, _dropAfter, _maxControls, _version;
    bool _refuseRest, _noEpsv, _otherHost, _requireLogin;
    string _user = "user", _password = "pass";

    FtpTestServer(byte[] payload) => Payload = payload;

    /// <summary>The file as it is at <see cref="Version"/> 0.</summary>
    public byte[] Payload { get; }
    public int Port { get; private set; }
    public string FileUrl => $"ftp://127.0.0.1:{Port}{FilePath}";
    public string UrlWithLogin(string user, string password) => $"ftp://{user}:{password}@127.0.0.1:{Port}{FilePath}";

    /// <summary>Each data connection's speed; 0 is unlimited.</summary>
    public int PerConnectionKBps { get => Volatile.Read(ref _perConnectionKBps); set => Volatile.Write(ref _perConnectionKBps, value); }

    /// <summary>The first RETR starting at this offset or later gets no reply, and the connection stays open in silence.</summary>
    public long NoRetrReplyOnceFrom { get => Volatile.Read(ref _noRetrReplyOnceFrom); set => Volatile.Write(ref _noRetrReplyOnceFrom, value); }
    long _noRetrReplyOnceFrom;

    /// <summary>While set, a new connection is answered 421, as a server going through a restart may.</summary>
    public bool Down { get => Volatile.Read(ref _down); set => Volatile.Write(ref _down, value); }
    bool _down;

    /// <summary>What RETR answers instead of sending the file; null sends it.</summary>
    public string? RetrReply { get; set; }

    /// <summary>REST answers 502, so the file can only be fetched whole.</summary>
    public bool RefuseRest { get => Volatile.Read(ref _refuseRest); set => Volatile.Write(ref _refuseRest, value); }

    /// <summary>Control connections beyond this many are answered 421; 0 allows any number.</summary>
    public int MaxControlConnections { get => Volatile.Read(ref _maxControls); set => Volatile.Write(ref _maxControls, value); }

    /// <summary>The next data connection is cut off after this many bytes; set back to 0 once it has been.</summary>
    public int DropOnceAfterBytes { get => Volatile.Read(ref _dropAfter); set => Volatile.Write(ref _dropAfter, value); }

    /// <summary>Only a transfer starting at this offset or later is cut off by <see cref="DropOnceAfterBytes"/>.</summary>
    public long DropOnlyFrom { get => Volatile.Read(ref _dropOnlyFrom); set => Volatile.Write(ref _dropOnlyFrom, value); }
    long _dropOnlyFrom;

    /// <summary>A transfer starting at this offset or later opens its data connection and sends nothing until the client leaves; 0 is off.</summary>
    public long SilentFrom { get => Volatile.Read(ref _silentFrom); set => Volatile.Write(ref _silentFrom, value); }
    long _silentFrom;

    /// <summary>A RETR starting at this offset or later gets no reply until the client leaves; 0 is off.</summary>
    public long NoRetrReplyFrom { get => Volatile.Read(ref _noRetrReplyFrom); set => Volatile.Write(ref _noRetrReplyFrom, value); }
    long _noRetrReplyFrom;

    /// <summary>Sends this instead of the greeting, for a server that misbehaves from its first line; the session carries on afterwards.</summary>
    public Func<Stream, CancellationToken, Task>? Greeting { get => Volatile.Read(ref _greeting); set => Volatile.Write(ref _greeting, value); }
    Func<Stream, CancellationToken, Task>? _greeting;

    /// <summary>SIZE is not understood, so the file's length is only known when its data ends.</summary>
    public bool SizeUnknown { get => Volatile.Read(ref _sizeUnknown); set => Volatile.Write(ref _sizeUnknown, value); }
    bool _sizeUnknown;

    /// <summary>A dropped data connection is closed cleanly, so it ends like a finished one; only the 426 reply tells them apart.</summary>
    public bool DropGracefully { get => Volatile.Read(ref _dropGracefully); set => Volatile.Write(ref _dropGracefully, value); }
    bool _dropGracefully;

    /// <summary>A dropped data connection is reported as a finished transfer, so only its length gives it away.</summary>
    public bool DropClaimsSuccess { get => Volatile.Read(ref _dropClaimsSuccess); set => Volatile.Write(ref _dropClaimsSuccess, value); }
    bool _dropClaimsSuccess;

    /// <summary>EPSV is not understood, so PASV has to do.</summary>
    public bool NoEpsv { get => Volatile.Read(ref _noEpsv); set => Volatile.Write(ref _noEpsv, value); }

    /// <summary>PASV names an address no client should connect to; the data connection is still on this server.</summary>
    public bool PasvNamesOtherHost { get => Volatile.Read(ref _otherHost); set => Volatile.Write(ref _otherHost, value); }

    /// <summary>The file's content and modification time change with this; the size does not.</summary>
    public int Version { get => Volatile.Read(ref _version); set => Volatile.Write(ref _version, value); }

    /// <summary>Only <see cref="User"/> with <see cref="Password"/> may log in; otherwise anyone may, as anonymous.</summary>
    public bool RequireLogin { get => Volatile.Read(ref _requireLogin); set => Volatile.Write(ref _requireLogin, value); }
    public string User { get => Volatile.Read(ref _user); set => Volatile.Write(ref _user, value); }
    public string Password { get => Volatile.Read(ref _password); set => Volatile.Write(ref _password, value); }

    public byte[] PayloadFor(int version) => version == 0 ? Payload : Payload.Select(b => (byte)(b ^ (0x60 + version))).ToArray();

    /// <summary>The REST offset of each RETR, 0 where there was none.</summary>
    public IReadOnlyCollection<long> Restarts => _restarts;

    /// <summary>The most data transfers that were running at once.</summary>
    public int MaxConcurrentTransfers => Volatile.Read(ref _maxTransfers);

    /// <summary>Control connections accepted, refused ones included.</summary>
    public int ControlConnections => Volatile.Read(ref _controlsEver);

    /// <summary>Control connections open now.</summary>
    public int ActiveControlConnections => Volatile.Read(ref _controls);

    /// <summary>Control connections turned away with 421.</summary>
    public int Refused => Volatile.Read(ref _refusedControls);

    /// <summary>PASS commands received.</summary>
    public int LoginAttempts => Volatile.Read(ref _logins);

    /// <summary>The argument of each USER command.</summary>
    public IReadOnlyCollection<string> UsersSeen => _users;

    /// <summary>Every line a client sent, as it was sent.</summary>
    public IReadOnlyCollection<string> Commands => _commands;
    readonly ConcurrentQueue<string> _commands = new();

    /// <summary>Data connections cut off by <see cref="DropOnceAfterBytes"/>.</summary>
    public int Drops => Volatile.Read(ref _drops);

    public static Task<FtpTestServer> StartAsync(int size)
    {
        var payload = new byte[size];
        new Random(1234).NextBytes(payload);
        return StartAsync(payload);
    }

    /// <summary>A server whose one file is <paramref name="payload"/>, such as an archive built by a test.</summary>
    public static Task<FtpTestServer> StartAsync(byte[] payload)
    {
        var server = new FtpTestServer(payload);
        server._listener.Start();
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        _ = server.AcceptLoopAsync();
        return Task.FromResult(server);
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sessions.Add(Task.Run(() => SessionAsync(client)));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
    }

    async Task SessionAsync(TcpClient client)
    {
        using var _ = client;
        int active = Interlocked.Increment(ref _controls);
        Interlocked.Increment(ref _controlsEver);
        TcpListener? passive = null;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

            if (Down || (MaxControlConnections > 0 && active > MaxControlConnections))
            {
                Interlocked.Increment(ref _refusedControls);
                await writer.WriteLineAsync("421 Too many connections from your address.");
                return;
            }
            if (Greeting is { } greet) await greet(stream, _stop.Token);
            else await writer.WriteLineAsync("220 ADM test FTP server");

            string? user = null;
            bool loggedIn = false;
            long restart = 0;
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                _commands.Enqueue(line);
                var space = line.IndexOf(' ');
                var command = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var argument = space < 0 ? "" : line[(space + 1)..];

                if (command is "USER")
                {
                    user = argument;
                    _users.Enqueue(argument);
                    await writer.WriteLineAsync("331 Password required");
                }
                else if (command is "PASS")
                {
                    Interlocked.Increment(ref _logins);
                    if (RequireLogin && (user != User || argument != Password))
                        await writer.WriteLineAsync("530 Login incorrect.");
                    else
                    {
                        loggedIn = true;
                        await writer.WriteLineAsync("230 Logged in");
                    }
                }
                else if (command is "QUIT")
                {
                    await writer.WriteLineAsync("221 Goodbye");
                    return;
                }
                else if (!loggedIn && RequireLogin)
                {
                    await writer.WriteLineAsync("530 Please login with USER and PASS.");
                }
                else
                {
                    switch (command)
                    {
                        case "TYPE":
                            await writer.WriteLineAsync("200 Type set");
                            break;
                        case "OPTS":
                            await writer.WriteLineAsync("200 OK");
                            break;
                        case "FEAT":
                            await writer.WriteAsync("211-Features:\r\n UTF8\r\n REST STREAM\r\n SIZE\r\n MDTM\r\n EPSV\r\n211 End\r\n");
                            break;
                        case "SIZE":
                            await writer.WriteLineAsync(argument != FilePath ? "550 No such file" : SizeUnknown ? "502 SIZE not implemented" : $"213 {Payload.Length}");
                            break;
                        case "MDTM":
                            await writer.WriteLineAsync(argument == FilePath
                                ? $"213 {new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMinutes(Version):yyyyMMddHHmmss}"
                                : "550 No such file");
                            break;
                        case "EPSV" when NoEpsv:
                            await writer.WriteLineAsync("500 EPSV not understood");
                            break;
                        case "EPSV":
                        case "PASV":
                            passive?.Stop();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            int port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            await writer.WriteLineAsync(command == "EPSV"
                                ? $"229 Entering Extended Passive Mode (|||{port}|)"
                                : $"227 Entering Passive Mode ({(PasvNamesOtherHost ? "10,255,255,1" : "127,0,0,1")},{port >> 8},{port & 255})");
                            break;
                        case "REST":
                            if (RefuseRest)
                            {
                                await writer.WriteLineAsync("502 REST not implemented");
                            }
                            else
                            {
                                restart = long.Parse(argument);
                                await writer.WriteLineAsync($"350 Restarting at {restart}");
                            }
                            break;
                        case "RETR":
                            if (argument != FilePath)
                            {
                                await writer.WriteLineAsync("550 No such file");
                            }
                            else if (passive == null)
                            {
                                await writer.WriteLineAsync("425 Use PASV or EPSV first");
                            }
                            else if (RetrReply != null)
                            {
                                await writer.WriteLineAsync(RetrReply);
                            }
                            else if (NoRetrReplyFrom > 0 && restart >= NoRetrReplyFrom)
                            {
                                await Task.Delay(Timeout.Infinite, _stop.Token);
                            }
                            else if (NoRetrReplyOnceFrom > 0 && restart >= NoRetrReplyOnceFrom && Interlocked.Exchange(ref _noRetrReplyOnceFrom, 0) > 0)
                            {
                                await Task.Delay(Timeout.Infinite, _stop.Token);
                            }
                            else
                            {
                                await RetrieveAsync(passive, restart, writer);
                                passive.Stop();
                                passive = null;
                                restart = 0;
                            }
                            break;
                        default:
                            await writer.WriteLineAsync("502 Command not implemented");
                            break;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the test is over.
        }
        finally
        {
            passive?.Stop();
            Interlocked.Decrement(ref _controls);
        }
    }

    async Task RetrieveAsync(TcpListener passive, long restart, StreamWriter control)
    {
        _restarts.Enqueue(restart);
        int running = Interlocked.Increment(ref _transfers), peak;
        while (running > (peak = Volatile.Read(ref _maxTransfers)) && Interlocked.CompareExchange(ref _maxTransfers, running, peak) != peak) { }
        try
        {
            using var accepting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            accepting.CancelAfter(TimeSpan.FromSeconds(10));
            using var data = await passive.AcceptTcpClientAsync(accepting.Token);
            await control.WriteLineAsync("150 Opening data connection");

            if (SilentFrom > 0 && restart >= SilentFrom)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            var content = PayloadFor(Version);
            int dropAfter = restart >= DropOnlyFrom ? Interlocked.Exchange(ref _dropAfter, 0) : 0;
            long sent = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                var stream = data.GetStream();
                var at = (int)Math.Min(restart, content.Length);
                while (at < content.Length)
                {
                    int count = Math.Min(16 * 1024, content.Length - at);
                    if (dropAfter > 0 && sent + count > dropAfter) count = (int)(dropAfter - sent);
                    await stream.WriteAsync(content.AsMemory(at, count), _stop.Token);
                    at += count;
                    sent += count;
                    if (dropAfter > 0 && sent >= dropAfter)
                    {
                        Interlocked.Increment(ref _drops);
                        // A reset, as a connection lost on the way looks to the client; or, if asked, a clean close.
                        if (!DropGracefully) data.LingerState = new LingerOption(true, 0);
                        data.Close();
                        await control.WriteLineAsync(DropClaimsSuccess ? "226 Transfer complete" : "426 Connection closed; transfer aborted.");
                        return;
                    }
                    var kbps = PerConnectionKBps;
                    if (kbps > 0)
                    {
                        var due = TimeSpan.FromSeconds(sent / (kbps * 1024.0)) - clock.Elapsed;
                        if (due > TimeSpan.Zero) await Task.Delay(due, _stop.Token);
                    }
                }
                data.Close();
                await control.WriteLineAsync("226 Transfer complete");
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // The client had all it wanted and closed the connection.
                try { await control.WriteLineAsync("426 Connection closed; transfer aborted."); } catch (IOException) { }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _transfers);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>A SOCKS5 proxy for the tests: it relays every CONNECT, with a login if it is given one.</summary>
sealed class Socks5TestRelay : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentQueue<string> _targets = new();
    readonly ConcurrentBag<Task> _sessions = new();
    readonly string? _user, _password;

    public Socks5TestRelay(string? user = null, string? password = null)
    {
        _user = user;
        _password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>The host and port of each CONNECT it carried out.</summary>
    public IReadOnlyCollection<string> Targets => _targets;

    /// <summary>Connects here whatever host was asked for, so an address only a proxy can reach works in a test.</summary>
    public string? ConnectInstead { get; init; }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sessions.Add(Task.Run(() => RelayAsync(client)));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
    }

    async Task RelayAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var head = await ReadAsync(stream, 2);
            var methods = await ReadAsync(stream, head[1]);
            byte method = _user == null ? (byte)0x00 : (byte)0x02;
            if (!methods.Contains(method))
            {
                await stream.WriteAsync(new byte[] { 5, 0xFF }, _stop.Token);
                return;
            }
            await stream.WriteAsync(new byte[] { 5, method }, _stop.Token);
            if (method == 0x02)
            {
                var version = await ReadAsync(stream, 2);
                var user = Encoding.UTF8.GetString(await ReadAsync(stream, version[1]));
                var password = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                bool ok = user == _user && password == _password;
                await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) }, _stop.Token);
                if (!ok) return;
            }

            var request = await ReadAsync(stream, 4);
            string host = request[3] switch
            {
                1 => new IPAddress(await ReadAsync(stream, 4)).ToString(),
                3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0])),
                4 => new IPAddress(await ReadAsync(stream, 16)).ToString(),
                _ => throw new IOException("Unknown address type."),
            };
            var portBytes = await ReadAsync(stream, 2);
            int port = portBytes[0] << 8 | portBytes[1];
            if (request[1] != 1)
            {
                await stream.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                return;
            }

            using var target = new TcpClient();
            await target.ConnectAsync(ConnectInstead ?? host, port, _stop.Token);
            _targets.Enqueue($"{host}:{port}");
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);

            var onward = target.GetStream();
            using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var up = stream.CopyToAsync(onward, done.Token);
            var down = onward.CopyToAsync(stream, done.Token);
            await Task.WhenAny(up, down);
            done.Cancel();
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    async Task<byte[]> ReadAsync(NetworkStream stream, int count)
    {
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, _stop.Token);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _stop.Dispose();
    }
}
