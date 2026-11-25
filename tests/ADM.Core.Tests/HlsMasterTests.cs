using System.Runtime.CompilerServices;
using ADM.Core;

namespace ADM.Core.Tests;

// tests/fixtures/master.m3u8 is shared with tests/extension/hls.test.mjs, which expects the same results.
public class HlsMasterTests
{
    static readonly Uri Base = new("https://a.com/v/master.m3u8");

    static HlsMaster Fixture([CallerFilePath] string here = "") =>
        HlsMaster.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "master.m3u8")), Base);

    [Fact]
    public void Parses_variants_in_playlist_order_without_iframe_streams()
    {
        var m = Fixture();

        Assert.Equal(
            new[] { "https://a.com/v/360p.m3u8", "https://cdn.example.com/1080p.m3u8", "https://a.com/v/720p.m3u8", "https://a.com/v/audio-only.m3u8" },
            m.Variants.Select(v => v.Uri.ToString()));
        var first = m.Variants[0];
        Assert.Equal(800000, first.Bandwidth);
        Assert.Equal((640, 360), (first.Width, first.Height));
        Assert.Equal("avc1.4d401e,mp4a.40.2", first.Codecs);
        Assert.Equal("aud", first.AudioGroup);
        Assert.Equal("subs", first.SubtitleGroup);
        Assert.Null(m.Variants[2].SubtitleGroup);
    }

    [Fact]
    public void Labels_name_the_height_or_audio()
    {
        Assert.Equal(new[] { "360p", "1080p", "720p", "Audio" }, Fixture().Variants.Select(v => v.Label));
        Assert.True(Fixture().Variants[3].AudioOnly);
        Assert.False(Fixture().Variants[0].AudioOnly);
    }

    [Fact]
    public void Parses_audio_and_subtitle_renditions()
    {
        var m = Fixture();

        Assert.Equal(new[] { "en", "de" }, m.Audio.Select(a => a.Language));
        Assert.True(m.Audio[0].IsDefault);
        Assert.Equal("https://a.com/v/audio/en.m3u8", m.Audio[0].Uri!.ToString());
        Assert.Equal("English", Assert.Single(m.Subtitles).Name);
    }

    [Fact]
    public void Picks_the_best_video_quality_by_default()
    {
        Assert.Equal(5000000, Fixture().Pick(null).Bandwidth);
    }

    [Theory]
    [InlineData(2500000, 2500000)]
    [InlineData(3000000, 2500000)] // gone from the playlist: the closest lower one
    [InlineData(100000, 800000)] // nothing lower: the lowest video
    public void Missing_quality_falls_back_to_the_closest_lower_one(long asked, long expected) =>
        Assert.Equal(expected, Fixture().Pick(asked).Bandwidth);

    [Fact]
    public void A_variant_uses_the_default_audio_and_subtitles_of_its_groups()
    {
        var m = Fixture();

        Assert.Equal("https://a.com/v/audio/en.m3u8", m.AudioFor(m.Variants[1])!.Uri!.ToString());
        Assert.Equal("https://a.com/v/subs/en.m3u8", m.SubtitlesFor(m.Variants[1])!.Uri!.ToString());
        Assert.Null(m.SubtitlesFor(m.Variants[2]));
    }

    [Fact]
    public void Audio_carried_inside_the_variant_has_no_separate_track()
    {
        var m = HlsMaster.Parse("""
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="Main",DEFAULT=YES
            #EXT-X-STREAM-INF:BANDWIDTH=1000,AUDIO="a"
            v.m3u8
            """, Base);

        Assert.Null(m.AudioFor(m.Variants[0]));
    }
}
