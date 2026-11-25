using System.Text.Json;

namespace ADM.Core;

/// <summary>The rules the browser extension follows, as JSON for its cache.</summary>
public sealed record CaptureConfig(
    string[] Extensions,
    string ExceptionSites,
    string ExceptionAddresses,
    long MinSizeKB,
    string[] PreventKeys,
    string[] ForceKeys,
    bool ForceOnLinkClick,
    bool MenuDownloadLink,
    bool MenuDownloadAllLinks,
    string VideoPanel,
    long VideoPanelMinSizeKB,
    string VideoPanelExceptionSites);

/// <summary>Decides which browser downloads ADM takes over (IDM's "File Types" tab).</summary>
public static class CaptureRules
{
    public const string DefaultExtensions =
        "zip rar 7z gz gzip tgz tar bz2 xz z lzh arj ace sit sitx sea r0* r1* iso img bin " +
        "exe msi msu msix apk dmg deb rpm pdf epub ppt pps plj tif tiff " +
        "mp3 aac m4a wav wma ogg opus flac aif ra mp4 m4v mkv avi mov wmv asf flv webm 3gp mpg mpeg mpe mpa ogv qt rm rmvb torrent";

    public const string DefaultExceptionSites = "*.update.microsoft.com download.windowsupdate.com *.download.windowsupdate.com";

    /// <param name="size">Size in bytes, or a negative number when the browser does not know it.</param>
    public static bool ShouldCapture(Settings settings, string url, string? fileName, long size)
    {
        if (!IsWeb(url, out var uri)) return false;

        var name = string.IsNullOrWhiteSpace(fileName) ? Uri.UnescapeDataString(uri.AbsolutePath) : fileName;
        var extension = Path.GetExtension(name).TrimStart('.');
        if (extension.Length == 0 || !Split(settings.CaptureExtensions).Any(p => SiteList.Wildcard(p, extension)))
            return false;
        if (SiteList.Matches(settings.CaptureExceptionSites, url) || SiteList.Matches(settings.CaptureExceptionAddresses, url))
            return false;
        return size < 0 || size >= settings.CaptureMinSizeKB * 1024;
    }

    /// <summary>
    /// <see cref="ShouldCapture"/> with the special keys held at the click that started the download:
    /// the prevent keys leave it to the browser, and the force keys take any http or https link.
    /// </summary>
    public static bool Decide(Settings settings, string url, string? fileName, long size, IReadOnlyCollection<string> heldKeys)
    {
        if (!IsWeb(url, out _)) return false;
        if (SpecialKeys.AllHeld(SpecialKeys.Parse(settings.PreventCaptureKeys) ?? Array.Empty<string>(), heldKeys)) return false;
        if (SpecialKeys.AllHeld(SpecialKeys.Parse(settings.ForceCaptureKeys) ?? Array.Empty<string>(), heldKeys)) return true;
        return ShouldCapture(settings, url, fileName, size);
    }

    /// <summary>
    /// Stops capturing from the site of <paramref name="url"/>, or from that one address; false when it
    /// was already excluded.
    /// </summary>
    public static bool AddException(Settings settings, string url, bool wholeSite)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (wholeSite)
        {
            if (SiteList.Matches(settings.CaptureExceptionSites, url)) return false;
            settings.CaptureExceptionSites = Append(settings.CaptureExceptionSites, uri.Host);
        }
        else
        {
            if (SiteList.Matches(settings.CaptureExceptionAddresses, url)) return false;
            settings.CaptureExceptionAddresses = Append(settings.CaptureExceptionAddresses, url);
        }
        return true;
    }

    public static CaptureConfig ToConfig(Settings settings) => new(
        Split(settings.CaptureExtensions),
        settings.CaptureExceptionSites,
        settings.CaptureExceptionAddresses,
        settings.CaptureMinSizeKB,
        SpecialKeys.Parse(settings.PreventCaptureKeys) ?? Array.Empty<string>(),
        SpecialKeys.Parse(settings.ForceCaptureKeys) ?? Array.Empty<string>(),
        settings.ForceOnLinkClick,
        settings.MenuDownloadLink,
        settings.MenuDownloadAllLinks,
        settings.VideoPanel.ToString().ToLowerInvariant(),
        settings.VideoPanelMinSizeKB,
        settings.VideoPanelExceptionSites);

    /// <summary>The reply to a "config" message from the browser extension.</summary>
    public static string ConfigReply(Settings settings) =>
        JsonSerializer.Serialize(new { ok = true, config = ToConfig(settings) }, JsonOptions);

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static string[] Split(string? list) => (list ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    static string Append(string? list, string pattern) => string.Join(" ", Split(list).Append(pattern));

    static bool IsWeb(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
