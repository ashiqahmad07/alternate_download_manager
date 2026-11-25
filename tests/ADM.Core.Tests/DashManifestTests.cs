using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

// The fixtures are in tests/fixtures/dash/.
public class DashManifestTests
{
    static readonly Uri Address = new("https://a.com/v/stream.mpd");

    static string Text(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "dash", name));

    static DashManifest Fixture(string name, [CallerFilePath] string here = "") =>
        DashManifest.Parse(Text(name, here), Address);

    static DashRepresentation Rep(DashManifest manifest, string id) =>
        manifest.Periods.SelectMany(p => p.Video.Concat(p.Audio)).Single(r => r.Id == id);

    static string[] Media(DashParts parts) => parts.Media.Select(p => p.Uri.ToString()).ToArray();

    /// <summary>A manifest of one video representation, whose segment information is <paramref name="segments"/>.</summary>
    static DashManifest OneVideo(string segments, string length = "PT10S") => DashManifest.Parse($"""
        <MPD type="static" mediaPresentationDuration="{length}"><Period><AdaptationSet mimeType="video/mp4">
          <Representation id="v" bandwidth="1000">{segments}</Representation>
        </AdaptationSet></Period></MPD>
        """, Address);

    static DashRepresentation OnlyVideo(DashManifest manifest) => Assert.Single(Assert.Single(manifest.Periods).Video);

    [Fact]
    public void Numbered_template_counts_from_the_period_and_fills_in_its_names()
    {
        var parts = Rep(Fixture("template-number.mpd"), "v720").Parts();

        Assert.Equal("https://a.com/v/media/video/v720/init.mp4", parts.Init!.Uri.ToString());
        Assert.Null(parts.Init.Range);
        // Ten seconds in parts of four: the last is cut short, and still a part.
        Assert.Equal(new[]
        {
            "https://a.com/v/media/video/v720/seg-005.m4s?b=1500000",
            "https://a.com/v/media/video/v720/seg-006.m4s?b=1500000",
            "https://a.com/v/media/video/v720/seg-007.m4s?b=1500000",
        }, Media(parts));
    }

    [Fact]
    public void Representation_overrides_only_what_it_names()
    {
        var parts = Rep(Fixture("template-number.mpd"), "v360").Parts();

        Assert.Equal("https://a.com/v/media/video/v360/init.mp4", parts.Init!.Uri.ToString());
        Assert.Equal("https://a.com/v/media/video/v360/seg-001.m4s?b=500000", Media(parts)[0]);
        Assert.Equal(3, parts.Media.Count);
    }

    [Fact]
    public void Absolute_base_address_replaces_the_ones_above_it()
    {
        var parts = Rep(Fixture("template-number.mpd"), "a").Parts();

        Assert.Equal("https://cdn.example.com/sound/init-$.mp4", parts.Init!.Uri.ToString());
        Assert.Equal(new[] { "https://cdn.example.com/sound/1.m4a", "https://cdn.example.com/sound/2.m4a", "https://cdn.example.com/sound/3.m4a" }, Media(parts));
    }

    [Fact]
    public void Timeline_gives_each_part_its_time_and_number()
    {
        var parts = Rep(Fixture("template-time.mpd"), "v").Parts();

        Assert.Equal("https://a.com/v/init.mp4", parts.Init!.Uri.ToString());
        Assert.Equal(new[]
        {
            "https://a.com/v/t900-n3.m4s",
            "https://a.com/v/t90900-n4.m4s",
            "https://a.com/v/t1000000-n5.m4s",
        }, Media(parts));
    }

    [Fact]
    public void Timeline_repeats_up_to_the_next_entry_or_the_period_end()
    {
        var parts = Rep(Fixture("timeline-repeat.mpd"), "v").Parts();

        Assert.Null(parts.Init);
        var expected = new long[] { 0, 2000, 4000, 6000, 10000, 12000, 14000, 16000, 17000, 18000, 19000 };
        Assert.Equal(expected.Select(t => $"https://a.com/v/{t}.m4s"), Media(parts));
    }

    [Fact]
    public void Timeline_running_to_the_end_counts_the_offset_of_its_times()
    {
        var parts = OnlyVideo(OneVideo("""
            <SegmentTemplate timescale="1000" presentationTimeOffset="5000" media="$Time$.m4s">
              <SegmentTimeline><S t="5000" d="2000" r="-1" /></SegmentTimeline>
            </SegmentTemplate>
            """, "PT4S")).Parts();

        Assert.Equal(new[] { "https://a.com/v/5000.m4s", "https://a.com/v/7000.m4s" }, Media(parts));
    }

    [Theory]
    [InlineData("$Number$", "7")]
    [InlineData("$Number%05d$", "00007")]
    [InlineData("$Number%d$", "7")]
    [InlineData("$Bandwidth%08d$", "00001000")]
    [InlineData("a$$b", "a$b")]
    [InlineData("$Unknown$", "$Unknown$")]
    [InlineData("$RepresentationID$", "v")]
    public void Template_names_are_filled_in(string template, string expected)
    {
        var parts = OnlyVideo(OneVideo($"""<SegmentTemplate startNumber="7" duration="10" media="{template}" />""")).Parts();

        // Uri escapes nothing here; the '$' and digits stay as they are.
        Assert.Equal($"https://a.com/v/{expected}", Media(parts)[0]);
    }

    [Fact]
    public void Absurd_width_in_a_template_is_left_as_text()
    {
        var parts = OnlyVideo(OneVideo("""<SegmentTemplate duration="10" media="$Number%0999999999d$" />""")).Parts();

        // Filled in, it would be a billion digits long.
        Assert.True(Media(parts)[0].Length < 100, "the width was taken at its word");
    }

    [Fact]
    public void Part_count_has_no_rounding_error()
    {
        // As doubles, 0.07 * 100 is 7.000000000000001, and that would ask for an eighth part that doesn't exist.
        var parts = OnlyVideo(OneVideo("""<SegmentTemplate timescale="100" duration="1" media="$Number$.m4s" />""", "PT0.07S")).Parts();

        Assert.Equal(7, parts.Media.Count);
    }

    [Fact]
    public void A_period_that_does_not_divide_evenly_gets_a_last_part()
    {
        var parts = OnlyVideo(OneVideo("""<SegmentTemplate timescale="1000" duration="2000" media="$Number$.m4s" />""", "PT4.001S")).Parts();

        Assert.Equal(3, parts.Media.Count);
    }

    [Theory]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("P1DT1S", 86401)]
    [InlineData("PT0S", 0)]
    public void Durations_are_read(string text, double seconds) =>
        Assert.Equal((decimal)seconds, DashManifest.ReadDuration(text));

    [Theory]
    [InlineData("soon")]
    [InlineData("1H")]
    [InlineData("PT-1S")]
    [InlineData("PT99999999999999999999999999999999S")]
    public void Duration_that_cannot_be_read_is_malformed(string text) =>
        Assert.Equal("The manifest gives a duration it can't be read from.",
            Assert.Throws<InvalidDataException>(() => DashManifest.ReadDuration(text)).Message);

    [Fact]
    public void Segment_list_names_its_parts_and_inherits_its_initialisation()
    {
        var parts = Rep(Fixture("segment-list.mpd"), "named").Parts();

        Assert.Equal(new DashPart(new Uri("https://a.com/v/init.mp4"), new ByteRange(0, 100)), parts.Init);
        Assert.Equal(new[]
        {
            new DashPart(new Uri("https://a.com/v/a.m4s")),
            new DashPart(new Uri("https://a.com/v/b.m4s"), new ByteRange(100, 100)),
        }, parts.Media);
    }

    [Fact]
    public void Segment_list_can_be_ranges_of_one_address()
    {
        var parts = Rep(Fixture("segment-list.mpd"), "ranges").Parts();
        var whole = new Uri("https://a.com/v/whole.mp4");

        Assert.Equal(new DashPart(whole, new ByteRange(0, 50)), parts.Init);
        Assert.Equal(new[] { new DashPart(whole, new ByteRange(50, 100)), new DashPart(whole, new ByteRange(150, 150)) }, parts.Media);
    }

    [Theory]
    [InlineData("both", "both.mp4")]
    [InlineData("index-only", "index-only.mp4")]
    public void Segment_base_is_a_whole_file_that_holds_its_initialisation(string id, string file)
    {
        var parts = Rep(Fixture("segment-base.mpd"), id).Parts();

        Assert.Equal(new Uri($"https://a.com/v/{file}"), parts.WholeFile);
        Assert.Null(parts.Init);
        Assert.Empty(parts.Media);
    }

    [Fact]
    public void Segment_base_can_keep_its_initialisation_elsewhere()
    {
        var parts = Rep(Fixture("segment-base.mpd"), "init-elsewhere").Parts();

        Assert.Equal(new DashPart(new Uri("https://a.com/v/init.mp4")), parts.Init);
        Assert.Equal(new Uri("https://a.com/v/init-elsewhere.mp4"), parts.WholeFile);
    }

    [Fact]
    public void Representation_with_only_an_address_is_a_whole_file()
    {
        var parts = Rep(Fixture("whole-file.mpd"), "plain").Parts();

        Assert.Equal(new Uri("https://a.com/v/plain.webm"), parts.WholeFile);
        Assert.Null(parts.Init);
        Assert.Empty(parts.Media);
    }

    [Fact]
    public void Segment_base_with_no_index_is_a_whole_file_that_holds_its_initialisation()
    {
        var parts = Rep(Fixture("whole-file.mpd"), "no-index").Parts();

        Assert.Equal(new Uri("https://a.com/v/no-index.webm"), parts.WholeFile);
        Assert.Null(parts.Init);
    }

    [Fact]
    public void Representation_with_nothing_to_go_on_is_malformed()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("")).Parts());

        Assert.Equal("The manifest doesn't say where a representation's parts are.", e.Message);
    }

    [Fact]
    public void Details_are_read_and_inherited()
    {
        var manifest = Fixture("template-number.mpd");
        var video = Rep(manifest, "v720");
        var audio = Rep(manifest, "a");

        Assert.Equal((DashMedia.Video, 1500000L, 1280, 720), (video.Media, video.Bandwidth, video.Width, video.Height));
        Assert.Equal(("avc1.4d401f", "video/mp4", ".mp4"), (video.Codecs, video.MimeType, video.Extension));
        Assert.Equal((DashMedia.Audio, "en", ".m4a"), (audio.Media, audio.Language, audio.Extension));
        Assert.Equal((".webm", ".webm"), (Rep(Fixture("whole-file.mpd"), "plain").Extension, Rep(Fixture("whole-file.mpd"), "sound").Extension));
    }

    [Fact]
    public void Labels_name_the_height_or_audio()
    {
        var manifest = Fixture("template-number.mpd");

        Assert.Equal("720p", Rep(manifest, "v720").Label);
        Assert.Equal("Audio", Rep(manifest, "a").Label);
        Assert.Equal("1 kbps", OnlyVideo(OneVideo("")).Label);
    }

    [Fact]
    public void Period_lengths_come_from_their_own_duration_the_next_start_or_the_whole()
    {
        var manifest = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT30S">
              <Period duration="PT5S" />
              <Period />
              <Period start="PT12S" />
              <Period start="PT20S" />
            </MPD>
            """, Address);

        Assert.Equal(new decimal?[] { 5, 7, 8, 10 }, manifest.Periods.Select(p => p.Seconds));
    }

    [Fact]
    public void Each_period_gets_its_own_parts()
    {
        var manifest = Fixture("multi-period.mpd");
        var choice = manifest.Pick(null, false);

        Assert.Equal(new long[] { 1500000, 1400000 }, choice.Main.Select(r => r.Bandwidth));
        Assert.Equal(new[] { "https://a.com/v/one/1500000/1.m4s", "https://a.com/v/one/1500000/2.m4s" }, Media(choice.Main[0].Parts()));
        Assert.Equal(new[] { "https://a.com/v/two/1400000/1.m4s", "https://a.com/v/two/1400000/2.m4s", "https://a.com/v/two/1400000/3.m4s" }, Media(choice.Main[1].Parts()));
        Assert.Equal(2, choice.Audio.Count);
    }

    [Theory]
    [InlineData(null, 1500000, 1400000)] // the best of each period
    [InlineData(500000L, 500000, 400000)]
    [InlineData(1400000L, 500000, 1400000)] // the closest lower one where it is gone
    [InlineData(100000L, 500000, 400000)] // nothing lower: the lowest video
    public void Picks_like_hls(long? asked, long first, long second)
    {
        var choice = Fixture("multi-period.mpd").Pick(asked, false);

        Assert.Equal(new[] { first, second }, choice.Main.Select(r => r.Bandwidth));
    }

    const string TwoAudioSets = """
        <MPD type="static" mediaPresentationDuration="PT10S"><Period>
          <AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1000" /></AdaptationSet>
          <AdaptationSet mimeType="audio/mp4" lang="de">
            <Representation id="de-high" bandwidth="256000" />
          </AdaptationSet>
          <AdaptationSet mimeType="audio/mp4" lang="en">
            <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main" />
            <Representation id="en-low" bandwidth="64000" />
            <Representation id="en-high" bandwidth="128000" />
          </AdaptationSet>
        </Period></MPD>
        """;

    [Fact]
    public void Audio_comes_from_the_main_set_at_its_best()
    {
        var choice = DashManifest.Parse(TwoAudioSets, Address).Pick(null, false);

        Assert.Equal("en-high", Assert.Single(choice.Audio).Id);
    }

    [Fact]
    public void With_no_main_set_audio_comes_from_the_first()
    {
        var choice = DashManifest.Parse(TwoAudioSets.Replace("value=\"main\"", "value=\"commentary\""), Address).Pick(null, false);

        Assert.Equal("de-high", Assert.Single(choice.Audio).Id);
    }

    [Fact]
    public void Muxed_video_gets_no_separate_audio()
    {
        var choice = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period>
              <AdaptationSet mimeType="video/mp4">
                <Representation id="v" bandwidth="1000" codecs="avc1.4d401f,mp4a.40.2" />
              </AdaptationSet>
              <AdaptationSet mimeType="audio/mp4"><Representation id="a" bandwidth="128000" /></AdaptationSet>
            </Period></MPD>
            """, Address).Pick(null, false);

        Assert.True(Assert.Single(choice.Main).HasAudio);
        Assert.Empty(choice.Audio);
    }

    [Fact]
    public void Audio_only_takes_the_audio_as_main()
    {
        var choice = DashManifest.Parse(TwoAudioSets, Address).Pick(null, true);

        Assert.Equal("en-high", Assert.Single(choice.Main).Id);
        Assert.Empty(choice.Audio);
    }

    [Fact]
    public void Audio_only_with_no_audio_fails_clearly()
    {
        var e = Assert.Throws<InvalidDataException>(() => OneVideo("").Pick(null, true));

        Assert.Equal("This stream has no separate audio track to download on its own.", e.Message);
    }

    [Fact]
    public void Audio_only_manifest_gives_its_audio_as_main()
    {
        var choice = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period>
              <AdaptationSet mimeType="audio/mp4"><Representation id="a" bandwidth="128000" /></AdaptationSet>
            </Period></MPD>
            """, Address).Pick(null, false);

        Assert.Equal("a", Assert.Single(choice.Main).Id);
        Assert.Empty(choice.Audio);
    }

    [Fact]
    public void Manifest_with_nothing_to_download_fails_clearly()
    {
        var e = Assert.Throws<InvalidDataException>(() => DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period>
              <AdaptationSet mimeType="text/vtt"><Representation id="t" bandwidth="1000" /></AdaptationSet>
              <AdaptationSet mimeType="image/jpeg"><Representation id="i" bandwidth="1000" /></AdaptationSet>
            </Period></MPD>
            """, Address).Pick(null, false));

        Assert.Equal("The manifest lists no video or audio.", e.Message);
    }

    [Fact]
    public void Trick_mode_is_left_out()
    {
        var manifest = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period>
              <AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1000000" /></AdaptationSet>
              <AdaptationSet mimeType="video/mp4">
                <EssentialProperty schemeIdUri="http://dashif.org/guidelines/trickmode" value="1" />
                <Representation id="trick" bandwidth="9000000" />
              </AdaptationSet>
            </Period></MPD>
            """, Address);

        Assert.Equal("v", Assert.Single(manifest.Periods[0].Video).Id);
    }

    [Theory]
    [InlineData("application/mp4", "avc1.4d401f", DashMedia.Video)]
    [InlineData("application/mp4", "mp4a.40.2", DashMedia.Audio)]
    [InlineData("", "opus", DashMedia.Audio)]
    public void Codecs_say_what_a_container_does_not(string mimeType, string codecs, DashMedia expected)
    {
        var manifest = DashManifest.Parse($"""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period><AdaptationSet>
              <Representation id="r" bandwidth="1000" mimeType="{mimeType}" codecs="{codecs}" />
            </AdaptationSet></Period></MPD>
            """, Address);

        Assert.Equal(expected, manifest.Periods[0].Video.Concat(manifest.Periods[0].Audio).Single().Media);
    }

    [Fact]
    public void Text_in_mp4_is_left_out()
    {
        var manifest = DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period><AdaptationSet>
              <Representation id="r" bandwidth="1000" mimeType="application/mp4" codecs="wvtt" />
            </AdaptationSet></Period></MPD>
            """, Address);

        Assert.Empty(manifest.Periods[0].Video.Concat(manifest.Periods[0].Audio));
    }

    [Fact]
    public void Manifest_with_another_namespace_or_none_is_read()
    {
        const string body = """<Period><AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1" /></AdaptationSet></Period>""";

        foreach (var open in new[] { "<MPD>", """<MPD xmlns="urn:mpeg:DASH:schema:MPD:2011">""" })
            Assert.Equal("v", OnlyVideo(DashManifest.Parse($"{open}{body}</MPD>", Address)).Id);
        Assert.Equal("v", OnlyVideo(DashManifest.Parse($"\uFEFF<MPD>{body}</MPD>", Address)).Id);
    }

    [Fact]
    public void Fingerprint_of_a_byte_range_is_unchanged()
    {
        // A sync check's fingerprint holds this text. If it changed, every saved byte-range download would count as changed, once.
        Assert.Equal("ByteRange { Offset = 5, Length = 7 }", new ByteRange(5, 7).ToString());
    }

    [Fact]
    public void Protected_manifest_is_refused()
    {
        // Its audio is in the clear, and saving just that would look like a download that worked.
        var e = Assert.Throws<NotSupportedException>(() => Fixture("drm.mpd"));

        Assert.Equal("Streams protected with DRM are not supported.", e.Message);
    }

    [Fact]
    public void Protection_on_one_representation_refuses_the_manifest_too()
    {
        var e = Assert.Throws<NotSupportedException>(() => OneVideo("""<ContentProtection schemeIdUri="urn:uuid:1" />"""));

        Assert.Equal("Streams protected with DRM are not supported.", e.Message);
    }

    [Fact]
    public void Live_manifest_is_refused()
    {
        var e = Assert.Throws<NotSupportedException>(() => Fixture("live.mpd"));

        Assert.Equal("Live DASH streams are not supported.", e.Message);
    }

    [Theory]
    [InlineData("dtd.mpd")]
    [InlineData("billion-laughs.mpd")]
    public void Manifest_with_a_dtd_is_refused(string name)
    {
        var watch = Stopwatch.StartNew();

        var e = Assert.Throws<InvalidDataException>(() => Fixture(name));

        Assert.Equal("The manifest has a DTD, which is not allowed.", e.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Something_else_is_not_a_manifest()
    {
        const string message = "The URL did not return a DASH manifest.";

        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => Fixture("not-mpd.xml")).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => DashManifest.Parse("#EXTM3U\n", Address)).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => DashManifest.Parse("", Address)).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => DashManifest.Parse("<!DOCTYPE html><html></html>", Address)).Message);
    }

    [Fact]
    public void Period_at_another_address_is_refused()
    {
        var e = Assert.Throws<NotSupportedException>(() => Fixture("remote-period.mpd"));

        Assert.Equal("This manifest keeps its periods at another address, which is not supported.", e.Message);
    }

    [Fact]
    public void Address_that_is_not_web_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => Fixture("not-web.mpd"));

        Assert.Equal("The manifest names an address that isn't HTTP or HTTPS.", e.Message);
    }

    [Fact]
    public void Part_address_that_is_not_web_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("""<SegmentTemplate duration="10" media="file:///C:/Windows/$Number$" />""")).Parts());

        Assert.Equal("The manifest names an address that isn't HTTP or HTTPS.", e.Message);
    }

    [Fact]
    public void Manifest_that_is_too_long_is_refused_unread()
    {
        var e = Assert.Throws<InvalidDataException>(() => DashManifest.Parse(new string(' ', DashManifest.MaxManifestChars + 1), Address));

        Assert.Equal("The manifest is too large.", e.Message);
    }

    [Fact]
    public void Parsing_expands_nothing()
    {
        // Each of these would refuse to expand; only the one asked for does.
        var manifest = Fixture("endless-number.mpd");

        Assert.Single(manifest.Periods[0].Video);
    }

    static string TooManyParts => $"The manifest lists more than {DashManifest.MaxParts} parts.";

    [Fact]
    public void Endless_template_is_refused_without_expanding()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        var e = Assert.Throws<InvalidDataException>(() => Rep(Fixture("endless-number.mpd"), "v").Parts());

        Assert.Equal(TooManyParts, e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024, "it expanded before counting");
    }

    [Fact]
    public void Template_too_long_to_multiply_is_refused_like_any_other()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo(
            """<SegmentTemplate timescale="9223372036854775807" duration="1" media="$Number$.m4s" />""",
            "PT9999999999999999999999S")).Parts());

        Assert.Equal(TooManyParts, e.Message);
    }

    [Fact]
    public void Endless_timeline_is_refused_without_expanding()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        var e = Assert.Throws<InvalidDataException>(() => Rep(Fixture("endless-timeline.mpd"), "v").Parts());

        Assert.Equal(TooManyParts, e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024, "it expanded before counting");
    }

    [Fact]
    public void Timeline_running_on_forever_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("""
            <SegmentTemplate timescale="1" media="$Time$.m4s"><SegmentTimeline><S t="0" d="1" r="-1" /></SegmentTimeline></SegmentTemplate>
            """, "P100000Y")).Parts());

        Assert.Equal(TooManyParts, e.Message);
    }

    [Fact]
    public void Timeline_of_many_entries_is_counted_as_it_goes()
    {
        var entries = string.Concat(Enumerable.Repeat("""<S d="1" />""", DashManifest.MaxParts + 1));
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo($"""
            <SegmentTemplate timescale="1" media="$Time$.m4s"><SegmentTimeline>{entries}</SegmentTimeline></SegmentTemplate>
            """)).Parts());

        Assert.Equal(TooManyParts, e.Message);
    }

    [Fact]
    public void Segment_list_too_long_is_refused()
    {
        var urls = string.Concat(Enumerable.Repeat("""<SegmentURL media="a" />""", DashManifest.MaxParts + 1));
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo($"<SegmentList>{urls}</SegmentList>")).Parts());

        Assert.Equal(TooManyParts, e.Message);
    }

    [Fact]
    public void Exactly_the_most_parts_are_allowed()
    {
        var parts = OnlyVideo(OneVideo("""<SegmentTemplate timescale="1" duration="1" media="$Number$.m4s" />""", $"PT{DashManifest.MaxParts}S")).Parts();

        Assert.Equal(DashManifest.MaxParts, parts.Media.Count);
    }

    [Fact]
    public void One_part_more_than_the_most_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("""<SegmentTemplate timescale="1" duration="1" media="$Number$.m4s" />""", $"PT{DashManifest.MaxParts + 1}S")).Parts());

        Assert.Equal(TooManyParts, e.Message);
    }

    [Fact]
    public void Timeline_of_exactly_the_most_parts_is_allowed()
    {
        var parts = OnlyVideo(OneVideo($"""
            <SegmentTemplate timescale="1" media="$Time$.m4s"><SegmentTimeline><S t="0" d="1" r="{DashManifest.MaxParts - 1}" /></SegmentTimeline></SegmentTemplate>
            """)).Parts();

        Assert.Equal(DashManifest.MaxParts, parts.Media.Count);
    }

    [Fact]
    public void Count_with_no_length_to_go_on_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => Rep(Fixture("unknown-length.mpd"), "v").Parts());

        Assert.Equal("The manifest doesn't say how long a period is, so its parts can't be counted.", e.Message);
    }

    [Fact]
    public void Timeline_running_to_an_unknown_end_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => Assert.Single(DashManifest.Parse("""
            <MPD type="static"><Period><AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1">
              <SegmentTemplate timescale="1" media="$Time$"><SegmentTimeline><S t="0" d="1" r="-1" /></SegmentTimeline></SegmentTemplate>
            </Representation></AdaptationSet></Period></MPD>
            """, Address).Periods[0].Video).Parts());

        Assert.Equal("The manifest doesn't say how long a period is, so its parts can't be counted.", e.Message);
    }

    [Theory]
    [InlineData("""<SegmentTemplate duration="10" />""", "The manifest's segment template names no media address.")]
    [InlineData("""<SegmentTemplate media="$Number$" />""", "The manifest's segment template gives no duration or timeline.")]
    [InlineData("""<SegmentTemplate duration="0" media="$Number$" />""", "The manifest's segment template gives no duration or timeline.")]
    [InlineData("""<SegmentTemplate timescale="0" duration="1" media="$Number$" />""", "The manifest gives a number it can't be read from.")]
    [InlineData("""<SegmentTemplate duration="ten" media="$Number$" />""", "The manifest gives a number it can't be read from.")]
    [InlineData("""<SegmentTemplate media="$Time$"><SegmentTimeline><S t="0" /></SegmentTimeline></SegmentTemplate>""", "The manifest gives a number it can't be read from.")]
    [InlineData("""<SegmentTemplate media="$Time$"><SegmentTimeline><S t="0" d="0" /></SegmentTimeline></SegmentTemplate>""", "The manifest gives a number it can't be read from.")]
    [InlineData("""<SegmentTemplate media="$Time$"><SegmentTimeline /></SegmentTemplate>""", "The manifest lists no parts for a representation.")]
    [InlineData("""<SegmentList />""", "The manifest lists no parts for a representation.")]
    [InlineData("""<SegmentList><SegmentURL mediaRange="-5" /></SegmentList>""", "The manifest gives a byte range it can't be read from.")]
    [InlineData("""<SegmentList><SegmentURL mediaRange="later" /></SegmentList>""", "The manifest gives a byte range it can't be read from.")]
    [InlineData("""<SegmentList><SegmentURL mediaRange="9-1" /></SegmentList>""", "The manifest gives a byte range it can't be read from.")]
    [InlineData("""<SegmentList><SegmentURL mediaRange="0-9223372036854775807" /></SegmentList>""", "The manifest gives a byte range it can't be read from.")]
    public void What_cannot_be_read_says_so(string segments, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo(segments)).Parts());

        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void A_period_ending_before_it_starts_has_no_parts()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(DashManifest.Parse("""
            <MPD type="static" mediaPresentationDuration="PT5S"><Period start="PT9S"><AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="1"><SegmentTemplate duration="1" media="$Number$" /></Representation>
            </AdaptationSet></Period></MPD>
            """, Address)).Parts());

        Assert.Equal("The manifest lists no parts for a representation.", e.Message);
    }

    [Fact]
    public void A_base_address_that_is_a_folder_is_no_whole_file()
    {
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("<BaseURL>folder/</BaseURL>")).Parts());

        Assert.Equal("The manifest doesn't say where a representation's parts are.", e.Message);
    }

    [Fact]
    public void The_address_of_a_refused_part_stays_out_of_the_message()
    {
        // Messages reach the log and the Status column, and addresses can hold tokens.
        var e = Assert.Throws<InvalidDataException>(() => OnlyVideo(OneVideo("""<SegmentTemplate duration="10" media="ftp://user:secret@a.com/$Number$" />""")).Parts());

        Assert.DoesNotContain("secret", e.Message);
    }

    // What the native host tells the extension's download panel.
    static JsonNode Choices(string text) => JsonNode.Parse(DashManifest.ChoicesJson(text, Address))!;

    static (string Label, long Bandwidth)[] Qualities(JsonNode choices) =>
        choices["qualities"]!.AsArray().Select(q => ((string)q!["label"]!, (long)q["bandwidth"]!)).ToArray();

    [Fact]
    public void Choices_list_the_qualities_best_first()
    {
        var choices = Choices(Text("template-number.mpd"));

        Assert.True((bool)choices["ok"]!);
        Assert.Equal(new[] { ("720p", 1500000L), ("360p", 500000L) }, Qualities(choices));
        Assert.True((bool)choices["audioOnly"]!);
    }

    [Fact]
    public void Choices_come_from_the_longest_period()
    {
        // The first period is four seconds and the second six, as an advert before a film would be.
        var choices = Choices(Text("multi-period.mpd"));

        Assert.Equal(new[] { ("720p", 1400000L), ("360p", 400000L) }, Qualities(choices));
    }

    [Fact]
    public void Choices_of_a_muxed_video_offer_no_audio_on_its_own()
    {
        var choices = Choices("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period><AdaptationSet mimeType="video/mp4">
              <SegmentTemplate duration="2" media="$Number$.m4s" />
              <Representation id="v" bandwidth="1000" height="240" codecs="avc1.42c00d,mp4a.40.2" />
            </AdaptationSet></Period></MPD>
            """);

        Assert.Equal(new[] { ("240p", 1000L) }, Qualities(choices));
        Assert.False((bool)choices["audioOnly"]!);
    }

    [Fact]
    public void Choices_of_an_audio_only_manifest_list_no_quality()
    {
        // The panel then offers it as "Best quality", which downloads the audio.
        var choices = Choices("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period><AdaptationSet mimeType="audio/mp4">
              <SegmentTemplate duration="2" media="$Number$.m4a" />
              <Representation id="a" bandwidth="128000" />
            </AdaptationSet></Period></MPD>
            """);

        Assert.True((bool)choices["ok"]!);
        Assert.Empty(Qualities(choices));
    }

    [Theory]
    [InlineData("drm.mpd", "Streams protected with DRM are not supported.")]
    [InlineData("live.mpd", "Live DASH streams are not supported.")]
    [InlineData("not-mpd.xml", "The URL did not return a DASH manifest.")]
    public void Choices_of_a_refused_manifest_say_why(string name, string message)
    {
        var choices = Choices(Text(name));

        Assert.False((bool)choices["ok"]!);
        Assert.Equal(message, (string)choices["error"]!);
        Assert.Null(choices["qualities"]);
    }
}
