using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>A reply from the FTP server that is not the one wanted.</summary>
internal sealed class FtpException(int code, string message) : Exception(message)
{
    public int Code => code;
}

/// <summary>The control connection closed or went silent: worth reconnecting for, like a dropped network.</summary>
internal sealed class FtpConnectionException(string message) : IOException(message);

/// <summary>Where the connections go when they go through a SOCKS5 proxy.</summary>
internal sealed record FtpRoute(string Host, int Port, NetworkCredential? Login);

/// <summary>
/// One control connection to an FTP server, and the passive-mode data connection of one transfer. A transfer
/// connection of a download has its own client, so each runs on its own TCP connections and the server can
/// serve them side by side. Disposing it closes both.
/// </summary>
internal sealed partial class FtpClient : IDisposable
{
    internal const string NotFound = "The file was not found on the FTP server.";
    internal const string LoginRefused = "The FTP server refused the login.";
    internal const string ReplyTooLong = "The FTP server sent a reply that is too long.";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    // What a server may send as one reply. Real replies are a line or a short list of features; a server
    // that goes on past these is broken or hostile, and is not worth the memory or the wait.
    const int MaxLineBytes = 8 * 1024;
    const int MaxReplyLines = 1000;
    const int MaxReplyBytes = 64 * 1024;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(60);

    readonly TcpClient _control;
    readonly Stream _stream;
    readonly byte[] _input = new byte[4096];
    int _inputStart, _inputEnd;
    readonly FtpRoute? _via;
    readonly string _path;
    string _dataHost = "";
    Encoding _encoding = Encoding.Latin1;
    bool _noEpsv;

    FtpClient(TcpClient control, FtpRoute? via, string path)
    {
        _control = control;
        _stream = control.GetStream();
        _via = via;
        _path = path;
    }

    /// <summary>Whether the server lists UTF8 among its features; a later connection can skip asking.</summary>
    public bool Utf8 { get; private set; }

    /// <summary>
    /// Connects, reads the greeting, logs in (as anonymous without a login) and switches to binary mode.
    /// </summary>
    /// <param name="knownUtf8">What an earlier connection to this server learned; null asks the server.</param>
    public static async Task<FtpClient> ConnectAsync(Uri address, NetworkCredential? login, FtpRoute? via, bool? knownUtf8, CancellationToken ct)
    {
        var path = Uri.UnescapeDataString(address.AbsolutePath);
        var tcp = await OpenSocketAsync(address.IdnHost, address.Port, via, ct);
        var client = new FtpClient(tcp, via, path);
        try
        {
            await client.StartAsync(address, login, knownUtf8, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    static async Task<TcpClient> OpenSocketAsync(string host, int port, FtpRoute? via, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ConnectTimeout);
            if (via == null)
            {
                await tcp.ConnectAsync(host, port, limit.Token);
            }
            else
            {
                await tcp.ConnectAsync(via.Host, via.Port, limit.Token);
                await Socks5.ConnectAsync(tcp.GetStream(), host, port, via.Login, limit.Token);
            }
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    async Task StartAsync(Uri address, NetworkCredential? login, bool? knownUtf8, CancellationToken ct)
    {
        // Data connections go to the address the control connection reached, never to one the server names.
        // Through a proxy that address is the proxy's, so they ask for the server by the name it was given.
        _dataHost = _via == null && _control.Client.RemoteEndPoint is IPEndPoint remote ? remote.Address.ToString() : address.IdnHost;

        var greeting = await ReadReplyAsync(ct);
        if (greeting.Code != 220) throw Failure(greeting);

        var reply = await SendAsync("USER " + (login?.UserName ?? "anonymous"), ct);
        if (reply.Code is 331 or 332) reply = await SendAsync("PASS " + (login?.Password ?? "anonymous@example.com"), ct);
        if (reply.Code != 230) throw Failure(reply);

        if (knownUtf8 is null)
        {
            var features = await SendAsync("FEAT", ct);
            Utf8 = features.Code == 211 && features.Text.Split('\n').Any(line => line.Trim().Equals("UTF8", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Utf8 = knownUtf8.Value;
        }
        if (Utf8 && (await SendAsync("OPTS UTF8 ON", ct)).Code is >= 200 and < 300) _encoding = new UTF8Encoding(false);

        var type = await SendAsync("TYPE I", ct);
        if (type.Code != 200) throw Failure(type);
    }

    /// <summary>The file's size, or -1 when the server doesn't say.</summary>
    public async Task<long> SizeAsync(CancellationToken ct)
    {
        var reply = await SendAsync("SIZE " + _path, ct);
        if (reply.Code == 213 && long.TryParse(reply.Text.Trim(), CultureInfo.InvariantCulture, out var size)) return size;
        if (reply.Code is 550 or 421) throw Failure(reply);
        return -1;
    }

    /// <summary>When the file last changed (UTC), or null when the server doesn't say.</summary>
    public async Task<DateTime?> ModifiedAsync(CancellationToken ct)
    {
        var reply = await SendAsync("MDTM " + _path, ct);
        if (reply.Code != 213) return null;
        var text = reply.Text.Trim();
        return text.Length >= 14
            && DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified)
                ? modified
                : null;
    }

    /// <summary>Whether the next transfer may start at <paramref name="position"/>.</summary>
    public async Task<bool> TryRestAsync(long position, CancellationToken ct)
    {
        var reply = await SendAsync("REST " + position.ToString(CultureInfo.InvariantCulture), ct);
        if (reply.Code == 421) throw Failure(reply);
        return reply.Code == 350;
    }

    /// <summary>
    /// Opens a data connection and asks for the file from <paramref name="restart"/> on. The bytes come as
    /// the stream is read; the transfer ends when it is disposed.
    /// </summary>
    public async Task<Stream> RetrieveAsync(long restart, CancellationToken ct)
    {
        var data = await OpenDataAsync(ct);
        try
        {
            if (restart > 0 && !await TryRestAsync(restart, ct))
                throw new RangesRefusedException();
            var reply = await SendAsync("RETR " + _path, ct);
            if (reply.Code is not (125 or 150)) throw Failure(reply);
            return new FtpDataStream(data.GetStream(), this, data);
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>After the data connection has ended: the server says whether the whole transfer went through.</summary>
    internal async Task EndTransferAsync(CancellationToken ct)
    {
        var reply = await ReadReplyAsync(ct);
        if (reply.Code is not (226 or 250)) throw Failure(reply);
    }

    async Task<TcpClient> OpenDataAsync(CancellationToken ct)
    {
        int port = 0;
        if (!_noEpsv)
        {
            var reply = await SendAsync("EPSV", ct);
            if (reply.Code == 229) port = ParseEpsv(reply.Text);
            else if (reply.Code == 421) throw Failure(reply);
            else _noEpsv = true;
        }
        if (port == 0)
        {
            var reply = await SendAsync("PASV", ct);
            if (reply.Code != 227) throw Failure(reply);
            port = ParsePasv(reply.Text);
        }
        return await OpenSocketAsync(_dataHost, port, _via, ct);
    }

    static int ParseEpsv(string text)
    {
        // "Entering Extended Passive Mode (|||6446|)": the character after the bracket separates the fields.
        int open = text.IndexOf('('), close = text.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            var parts = text[(open + 1)..close].Split(text[open + 1]);
            if (parts.Length >= 4 && int.TryParse(parts[3], out var port) && port is > 0 and <= 65535) return port;
        }
        throw new FtpConnectionException("The FTP server sent an address for the data connection that ADM can't read.");
    }

    static int ParsePasv(string text)
    {
        // "Entering Passive Mode (h1,h2,h3,h4,p1,p2)": only the port is used; the host is the server's own.
        var match = PasvReply().Match(text);
        if (match.Success)
        {
            int port = int.Parse(match.Groups[1].Value) * 256 + int.Parse(match.Groups[2].Value);
            if (port is > 0 and <= 65535) return port;
        }
        throw new FtpConnectionException("The FTP server sent an address for the data connection that ADM can't read.");
    }

    [GeneratedRegex(@"\d+,\d+,\d+,\d+,(\d+),(\d+)")]
    private static partial Regex PasvReply();

    async Task<FtpReply> SendAsync(string command, CancellationToken ct)
    {
        // The path and login come from the address; a line break in one would send the server a second command.
        if (command.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new InvalidOperationException("The address or login has a line break in it, which FTP can't carry.");
        await _stream.WriteAsync(_encoding.GetBytes(command + "\r\n"), ct);
        return await ReadReplyAsync(ct);
    }

    async Task<FtpReply> ReadReplyAsync(CancellationToken ct)
    {
        // The whole reply has a deadline, not just each line, so a server can't keep one going a line at a time.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ReplyTimeout);
        string first = await ReadLineAsync(deadline.Token);
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            throw new FtpException(0, "The FTP server sent something that is not a reply.");
        var text = new StringBuilder(first.Length > 4 ? first[4..] : "");
        if (first.Length > 3 && first[3] == '-')
        {
            // A reply of several lines ends at the first line that starts with the same code and a space.
            var ending = first[..3] + " ";
            int lines = 1, bytes = first.Length;
            while (true)
            {
                var line = await ReadLineAsync(deadline.Token);
                if (line.StartsWith(ending, StringComparison.Ordinal))
                {
                    text.Append('\n').Append(line, 4, line.Length - 4);
                    break;
                }
                if (++lines > MaxReplyLines || (bytes += line.Length) > MaxReplyBytes) throw new FtpException(0, ReplyTooLong);
                text.Append('\n').Append(line);
            }
        }
        return new FtpReply(code, text.ToString());
    }

    /// <summary>One line of a reply without its line break; a line over <see cref="MaxLineBytes"/> is refused.</summary>
    async Task<string> ReadLineAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Net.ReadTimeout);
        var line = new List<byte>(80);
        while (true)
        {
            if (_inputStart == _inputEnd)
            {
                _inputStart = 0;
                _inputEnd = await _stream.ReadAsync(_input, limit.Token);
                if (_inputEnd == 0) throw new FtpConnectionException("The FTP server closed the connection.");
            }
            while (_inputStart < _inputEnd)
            {
                byte next = _input[_inputStart++];
                if (next == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.Latin1.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
                }
                line.Add(next);
                if (line.Count > MaxLineBytes) throw new FtpException(0, ReplyTooLong);
            }
        }
    }

    internal static FtpException Failure(FtpReply reply) => reply.Code switch
    {
        530 => new FtpException(530, LoginRefused),
        550 => new FtpException(550, NotFound),
        421 => new FtpException(421, "The FTP server isn't taking more connections: " + reply.Text.Trim()),
        _ => new FtpException(reply.Code, $"The FTP server said: {reply.Code} {reply.Text.Trim()}"),
    };

    public void Dispose() => _control.Dispose();
}

internal readonly record struct FtpReply(int Code, string Text);

/// <summary>The bytes of one transfer. At the end of the data the server's verdict is read, so a transfer it cut short isn't taken for a whole one.</summary>
internal sealed class FtpDataStream(Stream data, FtpClient owner, IDisposable socket) : Stream
{
    bool _ended;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await data.ReadAsync(buffer, ct);
        if (read == 0 && !_ended && buffer.Length > 0)
        {
            _ended = true;
            await owner.EndTransferAsync(ct);
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            data.Dispose();
            socket.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
