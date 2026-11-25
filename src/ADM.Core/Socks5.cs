using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ADM.Core;

/// <summary>The proxy refused, or can't do what was asked; trying again won't change that.</summary>
internal sealed class ProxyException(string message) : Exception(message);

/// <summary>The client side of a SOCKS5 CONNECT (RFC 1928), with a user name and password if given (RFC 1929).</summary>
internal static class Socks5
{
    /// <summary>
    /// Asks the proxy on the other end of <paramref name="stream"/> to connect to <paramref name="host"/>.
    /// A name goes to the proxy as it is, so the proxy resolves it.
    /// </summary>
    public static async Task ConnectAsync(Stream stream, string host, int port, NetworkCredential? login, CancellationToken ct)
    {
        await stream.WriteAsync(login == null ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 }, ct);
        var choice = await ReadAsync(stream, 2, ct);
        if (choice[0] != 5) throw new ProxyException("The proxy does not speak SOCKS5.");
        if (choice[1] == 2 && login != null)
        {
            var user = Encoding.UTF8.GetBytes(login.UserName);
            var password = Encoding.UTF8.GetBytes(login.Password);
            if (user.Length > 255 || password.Length > 255) throw new ProxyException("The proxy login is too long for SOCKS5.");
            var request = new byte[3 + user.Length + password.Length];
            request[0] = 1;
            request[1] = (byte)user.Length;
            user.CopyTo(request, 2);
            request[2 + user.Length] = (byte)password.Length;
            password.CopyTo(request, 3 + user.Length);
            await stream.WriteAsync(request, ct);
            if ((await ReadAsync(stream, 2, ct))[1] != 0) throw new ProxyException("The SOCKS5 proxy refused the login.");
        }
        else if (choice[1] != 0)
        {
            throw new ProxyException(choice[1] == 0xFF
                ? "The SOCKS5 proxy wants a login. Add one in Options > Proxy."
                : "The SOCKS5 proxy asked for a login method ADM doesn't have.");
        }

        var connect = new List<byte> { 5, 1, 0 };
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            connect.Add(address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            connect.AddRange(address.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255) throw new ProxyException("The host name is too long for SOCKS5.");
            connect.Add(3);
            connect.Add((byte)name.Length);
            connect.AddRange(name);
        }
        connect.Add((byte)(port >> 8));
        connect.Add((byte)(port & 255));
        await stream.WriteAsync(connect.ToArray(), ct);

        var reply = await ReadAsync(stream, 4, ct);
        if (reply[1] != 0) throw Failure(reply[1]);
        // The address the proxy bound for the connection is of no use here; read it off the stream.
        int left = reply[3] switch
        {
            1 => 4 + 2,
            4 => 16 + 2,
            3 => (await ReadAsync(stream, 1, ct))[0] + 2,
            _ => throw new ProxyException("The SOCKS5 proxy sent a reply ADM doesn't understand."),
        };
        await ReadAsync(stream, left, ct);
    }

    static Exception Failure(byte code) => code switch
    {
        // A refused or unreachable target is the kind of failure a later try can get past.
        5 => new IOException("The SOCKS5 proxy could not connect: connection refused.", new SocketException((int)SocketError.ConnectionRefused)),
        4 => new IOException("The SOCKS5 proxy could not reach the host.", new SocketException((int)SocketError.HostUnreachable)),
        3 => new IOException("The SOCKS5 proxy could not reach the network.", new SocketException((int)SocketError.NetworkUnreachable)),
        6 => new IOException("The SOCKS5 proxy could not connect: timed out."),
        2 => new ProxyException("The SOCKS5 proxy does not allow this connection."),
        7 => new ProxyException("The SOCKS5 proxy can't connect on request."),
        8 => new ProxyException("The SOCKS5 proxy can't use this kind of address."),
        _ => new IOException("The SOCKS5 proxy failed."),
    };

    static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(bytes, ct);
        }
        catch (EndOfStreamException)
        {
            throw new IOException("The proxy closed the connection.");
        }
        return bytes;
    }
}
