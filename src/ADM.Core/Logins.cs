using System.Net;

namespace ADM.Core;

/// <summary>
/// Answers a server's login challenge for the request being sent: first the download's own
/// login, but only on the host it was given for, then the first matching Site Login.
/// One instance serves every client; <see cref="Net.SendAsync"/> supplies the request's context.
/// </summary>
/// <remarks>
/// SocketsHttpHandler answers challenges after a redirect only when its credentials are a
/// <see cref="CredentialCache"/>, so this derives from one and re-implements
/// <see cref="ICredentials"/>; the host check below is what keeps a login on its own site.
/// </remarks>
internal sealed class LoginResolver : CredentialCache, ICredentials
{
    internal sealed record Context(string Host, NetworkCredential? Login, IReadOnlyList<SiteLogin> SiteLogins);

    public static readonly LoginResolver Instance = new();

    internal static readonly AsyncLocal<Context?> Current = new();

    LoginResolver() { }

    NetworkCredential? ICredentials.GetCredential(Uri uri, string authType)
    {
        if (Current.Value is not { } context) return null;
        if (context.Login != null && string.Equals(uri.Host, context.Host, StringComparison.OrdinalIgnoreCase))
            return context.Login;
        var site = context.SiteLogins.FirstOrDefault(s => SiteList.Matches(s.Sites, uri.ToString()));
        return site == null ? null : new NetworkCredential(site.UserName, Secrets.Unprotect(site.ProtectedPassword) ?? "");
    }
}
