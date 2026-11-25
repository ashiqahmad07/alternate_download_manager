using System.Globalization;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>One quality of an HLS stream, from an #EXT-X-STREAM-INF line.</summary>
public sealed record HlsVariant(Uri Uri, long Bandwidth, int Width, int Height, string Codecs, string? AudioGroup, string? SubtitleGroup)
{
    internal static readonly string[] VideoCodecs = { "avc1", "avc3", "hvc1", "hev1", "dvh1", "dvhe", "vp09", "vp8", "av01" };

    /// <summary>True when the listed codecs include no video.</summary>
    public bool AudioOnly => Codecs.Length > 0
        && !Codecs.Split(',').Any(c => VideoCodecs.Any(v => c.Trim().StartsWith(v, StringComparison.OrdinalIgnoreCase)));

    public string Label => Height > 0 ? $"{Height}p" : AudioOnly ? "Audio" : $"{Bandwidth / 1000} kbps";
}

/// <summary>An alternative audio or subtitle track, from an #EXT-X-MEDIA line; no URI means it is inside the variant.</summary>
public sealed record HlsRendition(string GroupId, string Name, string? Language, bool IsDefault, Uri? Uri);

/// <summary>An HLS master playlist (RFC 8216 section 4.3.4): the qualities and the alternative tracks.</summary>
public sealed partial class HlsMaster
{
    HlsMaster(List<HlsVariant> variants, List<HlsRendition> audio, List<HlsRendition> subtitles)
    {
        Variants = variants;
        Audio = audio;
        Subtitles = subtitles;
    }

    public IReadOnlyList<HlsVariant> Variants { get; }
    public IReadOnlyList<HlsRendition> Audio { get; }
    public IReadOnlyList<HlsRendition> Subtitles { get; }

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF");

    public static HlsMaster Parse(string text, Uri baseUri)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        var variants = new List<HlsVariant>();
        var audio = new List<HlsRendition>();
        var subtitles = new List<HlsRendition>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attributes(lines[i]);
                var rendition = new HlsRendition(
                    a.GetValueOrDefault("GROUP-ID", ""),
                    a.GetValueOrDefault("NAME", ""),
                    a.GetValueOrDefault("LANGUAGE"),
                    a.GetValueOrDefault("DEFAULT") == "YES",
                    a.TryGetValue("URI", out var uri) ? new Uri(baseUri, uri) : null);
                if (a.GetValueOrDefault("TYPE") == "AUDIO") audio.Add(rendition);
                else if (a.GetValueOrDefault("TYPE") == "SUBTITLES") subtitles.Add(rendition);
            }
            else if (lines[i].StartsWith("#EXT-X-STREAM-INF:"))
            {
                var next = lines.Skip(i + 1).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (next == null) continue;
                var a = Attributes(lines[i]);
                long.TryParse(a.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth);
                var size = a.GetValueOrDefault("RESOLUTION", "").Split('x');
                int width = 0, height = 0;
                if (size.Length == 2)
                {
                    int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                    int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                }
                variants.Add(new HlsVariant(new Uri(baseUri, next), bandwidth, width, height,
                    a.GetValueOrDefault("CODECS", ""), a.GetValueOrDefault("AUDIO"), a.GetValueOrDefault("SUBTITLES")));
            }
        }
        return new HlsMaster(variants, audio, subtitles);
    }

    /// <summary>
    /// The video variant with exactly <paramref name="bandwidth"/>, else the closest lower one, else the
    /// lowest; the highest when no bandwidth is asked for.
    /// </summary>
    public HlsVariant Pick(long? bandwidth)
    {
        var video = Variants.Where(v => !v.AudioOnly).ToList();
        if (video.Count == 0) video = Variants.ToList();
        if (video.Count == 0) throw new InvalidDataException("The master playlist lists no variants.");
        if (bandwidth is not long wanted) return video.MaxBy(v => v.Bandwidth)!;
        return video.FirstOrDefault(v => v.Bandwidth == wanted)
            ?? video.Where(v => v.Bandwidth < wanted).MaxBy(v => v.Bandwidth)
            ?? video.MinBy(v => v.Bandwidth)!;
    }

    /// <summary>The separate audio track the variant plays with, or null when its audio is muxed in.</summary>
    public HlsRendition? AudioFor(HlsVariant variant) => Choose(Audio, variant.AudioGroup);

    public HlsRendition? SubtitlesFor(HlsVariant variant) => Choose(Subtitles, variant.SubtitleGroup);

    static HlsRendition? Choose(IEnumerable<HlsRendition> renditions, string? group)
    {
        if (group == null) return null;
        var inGroup = renditions.Where(r => r.GroupId == group && r.Uri != null).ToList();
        return inGroup.FirstOrDefault(r => r.IsDefault) ?? inGroup.FirstOrDefault();
    }

    [GeneratedRegex("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)")]
    private static partial Regex AttributePattern();

    internal static Dictionary<string, string> Attributes(string line) =>
        AttributePattern().Matches(line[(line.IndexOf(':') + 1)..])
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim('"'));
}
