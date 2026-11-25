using System.Net;

namespace ADM.Core;

/// <summary>Sends requests through the manually configured proxy, except to hosts on its bypass list.</summary>
internal sealed class ProxyRouter : IWebProxy
{
    readonly Uri _proxy;
    readonly string _bypass;

    public ProxyRouter(ProxySettings settings)
    {
        var scheme = settings.Kind switch
        {
            ProxyKind.Https => "https",
            ProxyKind.Socks4 => "socks4",
            ProxyKind.Socks5 => "socks5",
            _ => "http",
        };
        _proxy = new UriBuilder(scheme, settings.Host, settings.Port).Uri;
        _bypass = settings.Bypass;
        if (!string.IsNullOrEmpty(settings.UserName))
            Credentials = new NetworkCredential(settings.UserName, Secrets.Unprotect(settings.ProtectedPassword) ?? "");
    }

    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination) => _proxy;

    public bool IsBypassed(Uri host) => SiteList.Matches(_bypass, host.ToString());
}
