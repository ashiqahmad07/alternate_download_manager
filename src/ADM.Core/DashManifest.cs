using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ADM.Core;

public enum DashMedia { Video, Audio }

/// <summary>One part of a track: all of an address, or a byte range of it.</summary>
public sealed record DashPart(Uri Uri, ByteRange? Range = null);

/// <summary>
/// What a manifest says about a representation's parts. Its media parts are in <see cref="Media"/>, or they
/// are all of <see cref="WholeFile"/>, whose size isn't known yet.
/// </summary>
public sealed record DashParts(DashPart? Init, IReadOnlyList<DashPart> Media, Uri? WholeFile = null);

/// <summary>What a download takes from each period: its main track, and the separate audio that goes with it, if any.</summary>
public sealed record DashChoice(IReadOnlyList<DashRepresentation> Main, IReadOnlyList<DashRepresentation> Audio);

/// <summary>One stretch of a stream, with the qualities it comes in.</summary>
public sealed record DashPeriod(IReadOnlyList<DashRepresentation> Video, IReadOnlyList<DashRepresentation> Audio, decimal? Seconds)
{
    /// <summary>As in <see cref="HlsMaster.Pick"/>; null when this period has no video.</summary>
    internal DashRepresentation? PickVideo(long? bandwidth)
    {
        if (Video.Count == 0) return null;
        if (bandwidth is not long wanted) return Video.MaxBy(r => r.Bandwidth);
        return Video.FirstOrDefault(r => r.Bandwidth == wanted)
            ?? Video.Where(r => r.Bandwidth < wanted).MaxBy(r => r.Bandwidth)
            ?? Video.MinBy(r => r.Bandwidth);
    }

    /// <summary>The first adaptation set marked main, else the first, at its highest bandwidth; null with no audio.</summary>
    internal DashRepresentation? BestAudio()
    {
        var chosen = Audio.FirstOrDefault(r => r.IsMain) ?? Audio.FirstOrDefault();
        return chosen == null ? null : Audio.Where(r => r.AdaptationSet == chosen.AdaptationSet).MaxBy(r => r.Bandwidth);
    }
}

/// <summary>One quality of one period. Its parts are worked out when asked for, so qualities not chosen cost nothing.</summary>
public sealed partial class DashRepresentation
{
    readonly XElement[] _levels;
    readonly Uri _base;
    readonly decimal? _periodSeconds;

    /// <param name="levels">This representation's element, its adaptation set's and its period's, lowest first.</param>
    /// <param name="address">Its base address, with every level above it taken into account.</param>
    internal DashRepresentation(DashMedia media, XElement[] levels, Uri address, decimal? periodSeconds, int adaptationSet, bool isMain)
    {
        _levels = levels;
        _base = address;
        _periodSeconds = periodSeconds;
        Media = media;
        AdaptationSet = adaptationSet;
        IsMain = isMain;
        Id = (string?)levels[0].Attribute("id") ?? "";
        // These only describe it, so one that can't be read costs no more than a plainer label.
        long.TryParse((string?)levels[0].Attribute("bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth);
        int.TryParse(Inherited("width"), NumberStyles.None, CultureInfo.InvariantCulture, out var width);
        int.TryParse(Inherited("height"), NumberStyles.None, CultureInfo.InvariantCulture, out var height);
        (Bandwidth, Width, Height) = (bandwidth, width, height);
        Codecs = Inherited("codecs") ?? "";
        MimeType = Inherited("mimeType") ?? "";
        Language = (string?)levels[1].Attribute("lang");
    }

    public string Id { get; }
    public DashMedia Media { get; }
    public long Bandwidth { get; }
    public int Width { get; }
    public int Height { get; }
    public string Codecs { get; }
    public string MimeType { get; }
    public string? Language { get; }

    internal int AdaptationSet { get; }
    internal bool IsMain { get; }

    /// <summary>What its container is called in a file's name.</summary>
    public string Extension => FileNames.ExtensionFor(MimeType) ?? (Media == DashMedia.Video ? ".mp4" : ".m4a");

    /// <summary>A video with its sound inside it, which needs no separate track.</summary>
    public bool HasAudio => DashManifest.HasCodec(Codecs, DashManifest.AudioCodecs);

    public string Label => Height > 0 ? $"{Height}p" : Media == DashMedia.Audio ? "Audio" : $"{Bandwidth / 1000} kbps";

    // A representation inherits these from its adaptation set, but not from its period.
    string? Inherited(string attribute) => (string?)_levels[0].Attribute(attribute) ?? (string?)_levels[1].Attribute(attribute);

    /// <summary>
    /// The parts, as far as the manifest says. Segment information at the lowest level that has any decides
    /// its kind, and anything missing there comes from the same element higher up.
    /// </summary>
    public DashParts Parts()
    {
        var kind = _levels.SelectMany(level => level.Elements())
            .FirstOrDefault(e => e.Name.LocalName is "SegmentTemplate" or "SegmentList" or "SegmentBase")?.Name.LocalName;
        var chain = _levels.SelectMany(level => level.Elements().Where(e => e.Name.LocalName == kind)).ToList();
        var parts = kind switch
        {
            "SegmentTemplate" => FromTemplate(chain),
            "SegmentList" => FromList(chain),
            // Its index isn't read. A WebM's is Cues rather than sidx, and the file as it stands holds everything an index could list.
            "SegmentBase" => WholeFile(Initialization(chain)),
            _ => WholeFile(null),
        };
        if (parts.Media.Count == 0 && parts.WholeFile == null)
            throw new InvalidDataException("The manifest lists no parts for a representation.");
        return parts;
    }

    DashParts FromTemplate(List<XElement> chain)
    {
        var media = Attribute(chain, "media") ?? throw new InvalidDataException("The manifest's segment template names no media address.");
        var timescale = DashManifest.ReadNumber(Attribute(chain, "timescale"), 1);
        if (timescale == 0) throw DashManifest.UnreadableNumber();
        var first = DashManifest.ReadNumber(Attribute(chain, "startNumber"), 1);
        var init = Attribute(chain, "initialization") is { } address ? new DashPart(Address(Fill(address, 0, 0))) : Initialization(chain);
        var times = Child(chain, "SegmentTimeline") is { } timeline ? Timeline(timeline, chain, timescale) : Evenly(chain, timescale);
        return new DashParts(init, times.Select((time, i) => new DashPart(Address(Fill(media, first + i, time)))).ToList());
    }

    /// <summary>When each part starts, for a template that gives them all one duration; counted before anything is made.</summary>
    IEnumerable<long> Evenly(List<XElement> chain, long timescale)
    {
        var duration = DashManifest.ReadNumber(Attribute(chain, "duration"), 0);
        if (duration == 0) throw new InvalidDataException("The manifest's segment template gives no duration or timeline.");
        var count = DashManifest.Counted(() => decimal.Ceiling(PeriodSeconds() * timescale / duration));
        return Enumerable.Range(0, (int)Math.Max(0, count)).Select(i => i * duration);
    }

    decimal PeriodSeconds() => _periodSeconds
        ?? throw new InvalidDataException("The manifest doesn't say how long a period is, so its parts can't be counted.");

    /// <summary>When each part starts, from the S elements of a SegmentTimeline; counted before each run of repeats.</summary>
    IEnumerable<long> Timeline(XElement timeline, List<XElement> chain, long timescale)
    {
        var entries = timeline.Elements().Where(e => e.Name.LocalName == "S").ToList();
        var time = 0L;
        var count = 0L;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            time = DashManifest.ReadNumber((string?)entry.Attribute("t"), time);
            var duration = DashManifest.ReadNumber((string?)entry.Attribute("d"), 0);
            if (duration == 0) throw DashManifest.UnreadableNumber();
            var repeats = DashManifest.ReadSigned((string?)entry.Attribute("r"), 0);
            if (repeats < 0)
            {
                var (start, following) = (time, i + 1 < entries.Count ? entries[i + 1].Attribute("t")?.Value : null);
                repeats = (long)DashManifest.Counted(() => decimal.Ceiling((TimelineEnd(following, chain, timescale) - start) / duration)) - 1;
            }
            if (repeats >= DashManifest.MaxParts - count) throw DashManifest.TooManyParts();
            for (long repeat = 0; repeat <= repeats; repeat++, count++, time += duration) yield return time;
        }
    }

    /// <summary>Where a negative repeat runs to: the next entry's time, or the end of the period, on the times' own clock.</summary>
    decimal TimelineEnd(string? following, List<XElement> chain, long timescale) => following != null
        ? DashManifest.ReadNumber(following, 0)
        : DashManifest.ReadNumber(Attribute(chain, "presentationTimeOffset"), 0) + PeriodSeconds() * timescale;

    DashParts FromList(List<XElement> chain)
    {
        var urls = chain.Select(e => e.Elements().Where(c => c.Name.LocalName == "SegmentURL").ToList()).FirstOrDefault(found => found.Count > 0) ?? new List<XElement>();
        if (urls.Count > DashManifest.MaxParts) throw DashManifest.TooManyParts();
        return new DashParts(Initialization(chain), urls.Select(url => new DashPart(
            url.Attribute("media") is { } media ? Address(media.Value) : _base,
            url.Attribute("mediaRange") is { } range ? DashManifest.ReadRange(range.Value) : null)).ToList());
    }

    /// <summary>All of this representation's base address. A file's own initialisation section is already in it.</summary>
    DashParts WholeFile(DashPart? init)
    {
        if (!_levels.Any(level => level.Elements().Any(e => e.Name.LocalName == "BaseURL")) || _base.AbsolutePath.EndsWith('/'))
            throw new InvalidDataException("The manifest doesn't say where a representation's parts are.");
        return new DashParts(init?.Uri == _base ? null : init, Array.Empty<DashPart>(), WholeFile: _base);
    }

    DashPart? Initialization(List<XElement> chain)
    {
        if (Child(chain, "Initialization") is not { } init) return null;
        return new DashPart(
            init.Attribute("sourceURL") is { } source ? Address(source.Value) : _base,
            init.Attribute("range") is { } range ? DashManifest.ReadRange(range.Value) : null);
    }

    static string? Attribute(List<XElement> chain, string name) => chain.Select(e => (string?)e.Attribute(name)).FirstOrDefault(value => value != null);

    static XElement? Child(List<XElement> chain, string name) => chain.SelectMany(e => e.Elements()).FirstOrDefault(c => c.Name.LocalName == name);

    Uri Address(string text) => DashManifest.Resolve(_base, text);

    // Its width has a limit, so that a manifest can't ask for a name a billion digits long.
    [GeneratedRegex(@"\$(?:(RepresentationID|Number|Time|Bandwidth)(?:%0?(\d{0,3})([dxX]))?)?\$")]
    private static partial Regex TemplateName();

    string Fill(string template, long number, long time) => TemplateName().Replace(template, name =>
    {
        var what = name.Groups[1].Value;
        if (what.Length == 0) return "$";
        if (what == "RepresentationID") return Id;
        var value = what == "Number" ? number : what == "Time" ? time : Bandwidth;
        var format = name.Groups[3].Value is "x" or "X" ? name.Groups[3].Value : "D";
        return value.ToString(format + name.Groups[2].Value, CultureInfo.InvariantCulture);
    });
}

/// <summary>
/// A static DASH manifest (ISO/IEC 23009-1): its periods and the qualities they come in. A manifest comes
/// from a server, so no DTD is read, it can name only web addresses, and it can't expand beyond a fixed
/// number of parts. DRM and live manifests are refused.
/// </summary>
public sealed partial class DashManifest
{
    /// <summary>Longer than this isn't read.</summary>
    public const int MaxManifestChars = 32 * 1024 * 1024;

    /// <summary>The most parts one track may have: a day of one-second parts, with room to spare.</summary>
    public const int MaxParts = 100_000;

    /// <summary>
    /// The longest part a byte range becomes. A part is held in memory until it is whole, and a pause loses
    /// the ones not finished, so a representation that is one file is fetched as many of these.
    /// </summary>
    public const int MaxPartBytes = 4 * 1024 * 1024;

    internal static readonly string[] AudioCodecs = { "mp4a", "ac-3", "ec-3", "ac-4", "opus", "flac", "vorbis", "alac", "dtsc", "dtse", "dtsh", "dtsl", "mha1", "mhm1" };

    const string TrickMode = "http://dashif.org/guidelines/trickmode";

    DashManifest(List<DashPeriod> periods) => Periods = periods;

    public IReadOnlyList<DashPeriod> Periods { get; }

    public static DashManifest Parse(string text, Uri address)
    {
        if (text.Length > MaxManifestChars) throw new InvalidDataException("The manifest is too large.");
        var root = Read(text).Root!;
        if (root.Name.LocalName != "MPD") throw new InvalidDataException(NotAManifest);
        if ((string?)root.Attribute("type") == "dynamic") throw new NotSupportedException("Live DASH streams are not supported.");
        // All of it: saving only what isn't protected, such as a film's sound, would look like a download that worked.
        if (root.Descendants().Any(e => e.Name.LocalName == "ContentProtection")) throw new NotSupportedException("Streams protected with DRM are not supported.");

        var elements = root.Elements().Where(e => e.Name.LocalName == "Period").ToList();
        var total = ReadDuration((string?)root.Attribute("mediaPresentationDuration"));
        var mpdBase = Based(address, root);
        var periods = new List<DashPeriod>();
        decimal? start = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            var period = elements[i];
            if (period.Attributes().Any(a => a.Name.LocalName == "href"))
                throw new NotSupportedException("This manifest keeps its periods at another address, which is not supported.");
            start = ReadDuration((string?)period.Attribute("start")) ?? start;
            var next = i + 1 < elements.Count ? ReadDuration((string?)elements[i + 1].Attribute("start")) : null;
            var seconds = ReadDuration((string?)period.Attribute("duration")) ?? (next ?? total) - start;
            periods.Add(ReadPeriod(period, Based(mpdBase, period), seconds));
            start += seconds;
        }
        return new DashManifest(periods);
    }

    static DashPeriod ReadPeriod(XElement period, Uri periodBase, decimal? seconds)
    {
        var video = new List<DashRepresentation>();
        var audio = new List<DashRepresentation>();
        int number = 0;
        foreach (var set in period.Elements().Where(e => e.Name.LocalName == "AdaptationSet"))
        {
            number++;
            if (set.Elements().Any(e => e.Name.LocalName is "EssentialProperty" or "SupplementalProperty" && (string?)e.Attribute("schemeIdUri") == TrickMode)) continue;
            var setBase = Based(periodBase, set);
            var isMain = set.Elements().Any(e => e.Name.LocalName == "Role" && (string?)e.Attribute("value") == "main");
            foreach (var element in set.Elements().Where(e => e.Name.LocalName == "Representation"))
            {
                if (MediaOf(element, set) is not { } media) continue;
                (media == DashMedia.Video ? video : audio).Add(new DashRepresentation(media, new[] { element, set, period }, Based(setBase, element), seconds, number, isMain));
            }
        }
        return new DashPeriod(video, audio, seconds);
    }

    /// <summary>What a representation holds: its adaptation set or its container says, or else its codecs. Text and pictures are left out.</summary>
    static DashMedia? MediaOf(XElement representation, XElement set)
    {
        var mimeType = (string?)representation.Attribute("mimeType") ?? (string?)set.Attribute("mimeType") ?? "";
        var codecs = (string?)representation.Attribute("codecs") ?? (string?)set.Attribute("codecs") ?? "";
        var type = (string?)set.Attribute("contentType") ?? mimeType.Split('/')[0];
        if (type == "video") return DashMedia.Video;
        if (type == "audio") return DashMedia.Audio;
        if (type is "text" or "image") return null;
        if (HasCodec(codecs, HlsVariant.VideoCodecs)) return DashMedia.Video;
        return HasCodec(codecs, AudioCodecs) ? DashMedia.Audio : null;
    }

    internal static bool HasCodec(string codecs, string[] names) =>
        codecs.Split(',').Any(c => names.Any(name => c.Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase)));

    const string NotAManifest = "The URL did not return a DASH manifest.";

    static XDocument Read(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿')), settings);
            return XDocument.Load(reader);
        }
        // An HTML page has a DOCTYPE too, and is simply not a manifest.
        catch (XmlException) when (text.Contains("<!DOCTYPE", StringComparison.Ordinal) && text.Contains("MPD", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The manifest has a DTD, which is not allowed.");
        }
        catch (XmlException)
        {
            throw new InvalidDataException(NotAManifest);
        }
    }

    /// <summary>
    /// The representations to download, one per period. The quality is chosen as in <see cref="HlsMaster.Pick"/>,
    /// period by period, as each can have its own list.
    /// </summary>
    public DashChoice Pick(long? bandwidth, bool audioOnly)
    {
        var main = new List<DashRepresentation>();
        var audio = new List<DashRepresentation>();
        var anyVideo = Periods.Any(period => period.Video.Count > 0);
        foreach (var period in Periods)
        {
            var sound = period.BestAudio();
            if (!audioOnly && period.PickVideo(bandwidth) is { } video)
            {
                main.Add(video);
                if (sound != null && !video.HasAudio) audio.Add(sound);
            }
            else if (sound != null && (audioOnly || !anyVideo))
            {
                main.Add(sound);
            }
        }
        if (main.Count == 0)
            throw new InvalidDataException(audioOnly ? "This stream has no separate audio track to download on its own." : "The manifest lists no video or audio.");
        return new DashChoice(main, audio);
    }

    /// <summary>
    /// What the browser's download panel offers for a manifest, as the native host's reply: the qualities of the
    /// longest period's video, best first, as a choice of one goes to <see cref="Pick"/>, and whether there is audio
    /// to take on its own. A refused manifest gives its reason instead, and the panel offers it with no choices.
    /// </summary>
    public static string ChoicesJson(string text, Uri address)
    {
        DashManifest manifest;
        try
        {
            manifest = Parse(text, address);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString();
        }
        // The longest, as a film is longer than the advert before it.
        var longest = manifest.Periods.MaxBy(period => period.Seconds ?? 0);
        var qualities = new JsonArray();
        foreach (var video in (longest?.Video ?? []).OrderByDescending(r => r.Bandwidth))
            qualities.Add(new JsonObject { ["label"] = video.Label, ["bandwidth"] = video.Bandwidth });
        return new JsonObject
        {
            ["ok"] = true,
            ["qualities"] = qualities,
            ["audioOnly"] = manifest.Periods.Any(period => period.Audio.Count > 0),
        }.ToJsonString();
    }

    /// <summary>A part as it is, or, if it is a long byte range, as pieces of <see cref="MaxPartBytes"/> that join back to it.</summary>
    public static IEnumerable<DashPart> Pieces(DashPart part)
    {
        if (part.Range is not { Length: > MaxPartBytes } range)
        {
            yield return part;
            yield break;
        }
        for (long at = 0; at < range.Length; at += MaxPartBytes)
            yield return part with { Range = new ByteRange(range.Offset + at, Math.Min(MaxPartBytes, range.Length - at)) };
    }

    internal static InvalidDataException TooManyParts() => new($"The manifest lists more than {MaxParts} parts.");

    /// <summary>A count of parts, under <see cref="MaxParts"/>; one too big even to work out is refused the same way.</summary>
    internal static decimal Counted(Func<decimal> count)
    {
        decimal parts;
        try
        {
            parts = count();
        }
        catch (OverflowException)
        {
            throw TooManyParts();
        }
        return parts <= MaxParts ? parts : throw TooManyParts();
    }

    static Uri Based(Uri parent, XElement level) =>
        level.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL") is { } address ? Resolve(parent, address.Value.Trim()) : parent;

    /// <summary>A web address from the manifest. Never in a message: addresses can hold tokens.</summary>
    internal static Uri Resolve(Uri parent, string text) =>
        Uri.TryCreate(parent, text, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : throw new InvalidDataException("The manifest names an address that isn't HTTP or HTTPS.");

    internal static InvalidDataException UnreadableNumber() => new("The manifest gives a number it can't be read from.");

    internal static long ReadNumber(string? text, long missing)
    {
        if (text == null) return missing;
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw UnreadableNumber();
    }

    internal static long ReadSigned(string? text, long missing)
    {
        if (text == null) return missing;
        return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : throw UnreadableNumber();
    }

    /// <summary>A byte range as a manifest writes it, first byte and last: 0-99.</summary>
    internal static ByteRange ReadRange(string text)
    {
        var ends = text.Trim().Split('-');
        if (ends.Length == 2
            && long.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            && long.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            && last >= first && last < long.MaxValue)
            return new ByteRange(first, last - first + 1);
        throw new InvalidDataException("The manifest gives a byte range it can't be read from.");
    }

    [GeneratedRegex(@"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d*)?)S)?)?$")]
    private static partial Regex Duration();

    /// <summary>
    /// An ISO 8601 duration, in seconds; null if there is none. In <see cref="decimal"/>, because a count of
    /// parts that comes out one too high asks for a part that doesn't exist.
    /// </summary>
    internal static decimal? ReadDuration(string? text)
    {
        if (text == null) return null;
        var parts = Duration().Match(text.Trim());
        if (!parts.Success) throw UnreadableDuration();
        try
        {
            decimal Seconds(int group, int each) => parts.Groups[group].Success ? decimal.Parse(parts.Groups[group].Value, CultureInfo.InvariantCulture) * each : 0;
            return Seconds(1, 365 * 86400) + Seconds(2, 30 * 86400) + Seconds(3, 86400) + Seconds(4, 3600) + Seconds(5, 60) + Seconds(6, 1);
        }
        catch (OverflowException)
        {
            throw UnreadableDuration();
        }
    }

    static InvalidDataException UnreadableDuration() => new("The manifest gives a duration it can't be read from.");
}
