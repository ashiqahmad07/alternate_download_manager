using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Writes ZIP archives byte by byte, so a test can declare what a real tool never would.</summary>
sealed class ZipBuilder
{
    public const ushort Stored = 0, Deflated = 8, EncryptedFlag = 1, Utf8Flag = 0x800;
    const uint Saturated = uint.MaxValue;

    sealed record Item(byte[] Name, byte[] Packed, ushort Method, ushort Flags, long Size, uint Crc, ushort Time, ushort Date);

    // 2026-01-02 03:04:06, as a ZIP holds it.
    public const ushort DosTime = (3 << 11) | (4 << 5) | (6 / 2);
    public const ushort DosDate = ((2026 - 1980) << 9) | (1 << 5) | 2;
    public static readonly DateTime DosModified = new(2026, 1, 2, 3, 4, 6);

    readonly List<Item> _items = new();

    public long DirectoryOffset { get; private set; }
    public long DirectorySize { get; private set; }

    public static uint CrcOf(ReadOnlySpan<byte> data) => Crc32.Final(Crc32.Update(Crc32.Initial, data));

    public static byte[] Deflate(byte[] content)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(content);
        return packed.ToArray();
    }

    /// <param name="declaredSize">What the directory says it unpacks to, when that isn't the truth.</param>
    /// <param name="declaredCrc">What the directory says its CRC-32 is, when that isn't the truth.</param>
    /// <param name="method">Written as the method whatever the data is, for one ADM can't unpack.</param>
    public ZipBuilder Add(string name, byte[] content, bool deflate = false, ushort flags = 0, ushort? method = null,
        long? declaredSize = null, uint? declaredCrc = null, byte[]? rawName = null, ushort date = DosDate, ushort time = DosTime)
    {
        _items.Add(new Item(
            rawName ?? Encoding.UTF8.GetBytes(name),
            deflate ? Deflate(content) : content,
            method ?? (deflate ? Deflated : Stored),
            flags,
            declaredSize ?? content.Length,
            declaredCrc ?? CrcOf(content),
            time,
            date));
        return this;
    }

    /// <param name="zip64">Puts every size and offset in ZIP64 records.</param>
    /// <param name="zip64OffsetOnly">Saturates only the offset in each entry, so its extra field holds that alone.</param>
    public byte[] Build(bool zip64 = false, bool zip64OffsetOnly = false, byte[]? comment = null, ushort disk = 0, byte[]? trailing = null, long? claimedDirectorySize = null, uint zip64Disks = 1)
    {
        using var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        var offsets = new List<long>();
        foreach (var item in _items)
        {
            offsets.Add(file.Position);
            writer.Write(0x04034b50u);
            writer.Write((ushort)45);
            writer.Write(item.Flags);
            writer.Write(item.Method);
            writer.Write(item.Time);
            writer.Write(item.Date);
            writer.Write(item.Crc);
            writer.Write((uint)item.Packed.Length);
            writer.Write((uint)Math.Min(item.Size, uint.MaxValue));
            writer.Write((ushort)item.Name.Length);
            // Local extra fields needn't match the directory's, so the data's place must come from here.
            writer.Write((ushort)5);
            writer.Write(item.Name);
            writer.Write(new byte[5]);
            writer.Write(item.Packed);
        }

        DirectoryOffset = file.Position;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            bool bigSizes = zip64 && !zip64OffsetOnly;
            bool bigOffset = zip64 || zip64OffsetOnly;
            var extra = new MemoryStream();
            var extraWriter = new BinaryWriter(extra);
            if (bigSizes || bigOffset)
            {
                extraWriter.Write((ushort)0x9999);
                extraWriter.Write((ushort)2);
                extraWriter.Write((ushort)0);
                extraWriter.Write((ushort)1);
                extraWriter.Write((ushort)((bigSizes ? 16 : 0) + 8));
                if (bigSizes)
                {
                    extraWriter.Write(item.Size);
                    extraWriter.Write((long)item.Packed.Length);
                }
                extraWriter.Write(offsets[i]);
            }
            writer.Write(0x02014b50u);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(item.Flags);
            writer.Write(item.Method);
            writer.Write(item.Time);
            writer.Write(item.Date);
            writer.Write(item.Crc);
            writer.Write(bigSizes ? Saturated : (uint)item.Packed.Length);
            writer.Write(bigSizes ? Saturated : (uint)item.Size);
            writer.Write((ushort)item.Name.Length);
            writer.Write((ushort)extra.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(bigOffset ? Saturated : (uint)offsets[i]);
            writer.Write(item.Name);
            writer.Write(extra.ToArray());
        }
        DirectorySize = file.Position - DirectoryOffset;

        if (zip64)
        {
            long recordAt = file.Position;
            writer.Write(0x06064b50u);
            writer.Write(44L);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((long)_items.Count);
            writer.Write((long)_items.Count);
            writer.Write(DirectorySize);
            writer.Write(DirectoryOffset);
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write(recordAt);
            writer.Write(zip64Disks);
        }

        writer.Write(0x06054b50u);
        writer.Write(disk);
        writer.Write(disk);
        writer.Write(zip64 ? ushort.MaxValue : (ushort)_items.Count);
        writer.Write(zip64 ? ushort.MaxValue : (ushort)_items.Count);
        writer.Write(zip64 ? Saturated : (uint)(claimedDirectorySize ?? DirectorySize));
        writer.Write(zip64 ? Saturated : (uint)DirectoryOffset);
        writer.Write((ushort)(comment?.Length ?? 0));
        if (comment != null) writer.Write(comment);
        if (trailing != null) writer.Write(trailing);
        writer.Flush();
        return file.ToArray();
    }
}

public sealed class ZipPreviewTests : IDisposable
{
    static readonly byte[] Hello = Encoding.ASCII.GetBytes("hello from inside the archive");
    static readonly CancellationToken None = CancellationToken.None;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public ZipPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static byte[] Random(int size, int seed = 7)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static DownloadItem Remote(string url) => new() { Url = url, FileName = "archive.zip" };

    static Task<TestServer> Serve(byte[] archive) => TestServer.StartAsync(archive, TimeSpan.Zero);

    Task<IReadOnlyList<ZipExtractResult>> Extract(string url, IEnumerable<ZipEntryInfo> entries, Settings? settings = null) =>
        ZipPreview.ExtractAsync(Remote(url), settings ?? new Settings(), entries, _dir, null, None);

    /// <summary>Lists <paramref name="archive"/> from a local server and takes everything out of it.</summary>
    async Task<IReadOnlyList<ZipExtractResult>> ExtractAll(byte[] archive, Settings? settings = null)
    {
        await using var server = await Serve(archive);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        return await Extract(server.FileUrl, entries, settings);
    }

    string[] FilesLeft() => Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);

    static (long From, long To) ParseRange(string header)
    {
        var parts = header["bytes=".Length..].Split('-');
        return (long.Parse(parts[0]), long.Parse(parts[1]));
    }

    [Fact]
    public void Crc32_matches_the_known_value()
    {
        Assert.Equal(0xCBF43926u, ZipBuilder.CrcOf(Encoding.ASCII.GetBytes("123456789")));
        // In pieces, as a file is written.
        var crc = Crc32.Update(Crc32.Initial, Encoding.ASCII.GetBytes("1234"));
        crc = Crc32.Update(crc, Encoding.ASCII.GetBytes("56789"));
        Assert.Equal(0xCBF43926u, Crc32.Final(crc));
    }

    [Fact]
    public async Task Lists_a_remote_zip_from_its_end()
    {
        // A directory too big for the last 64 KB, in front of data that must not be fetched.
        var builder = new ZipBuilder().Add("big.bin", Random(8 * 1024 * 1024));
        for (int i = 0; i < 2000; i++) builder.Add($"folder/small-file-number-{i:D5}.txt", Hello);
        var archive = builder.Build();
        Assert.True(builder.DirectorySize > 64 * 1024);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(2001, entries.Count);
        Assert.Equal("big.bin", entries[0].Name);
        Assert.Equal(8 * 1024 * 1024, entries[0].Size);
        Assert.Equal("folder/small-file-number-01999.txt", entries[^1].Name);

        // After the one-byte question every download starts with, two ranges: the end, then the directory.
        var ranges = server.Ranges("/file.bin");
        Assert.Equal(3, ranges.Count);
        Assert.Equal("bytes=0-0", ranges[0]);
        Assert.Equal($"bytes={archive.Length - 65558}-{archive.Length - 1}", ranges[1]);
        Assert.Equal($"bytes={builder.DirectoryOffset}-{builder.DirectoryOffset + builder.DirectorySize - 1}", ranges[2]);
        long sent = ranges.Select(ParseRange).Sum(r => r.To - r.From + 1);
        Assert.True(sent < archive.Length / 10, $"sent {sent} of {archive.Length}");
    }

    [Fact]
    public async Task A_directory_in_the_last_bytes_needs_no_second_range()
    {
        var archive = new ZipBuilder().Add("a.txt", Hello).Add("b.txt", Hello).Build();
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(new[] { "a.txt", "b.txt" }, entries.Select(e => e.Name));
        Assert.Equal(new[] { "bytes=0-0", $"bytes=0-{archive.Length - 1}" }, server.Ranges("/file.bin"));
    }

    [Fact]
    public async Task Entries_carry_what_the_directory_says()
    {
        var content = Encoding.ASCII.GetBytes(new string('x', 5000));
        var archive = new ZipBuilder().Add("note.txt", Hello).Add("packed.txt", content, deflate: true, flags: ZipBuilder.EncryptedFlag).Build();
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(Hello.Length, entries[0].Packed);
        Assert.Equal(Hello.Length, entries[0].Size);
        Assert.Equal(0, entries[0].Method);
        Assert.False(entries[0].Encrypted);
        Assert.Equal(ZipBuilder.CrcOf(Hello), entries[0].Crc32);
        Assert.Equal(ZipBuilder.DosModified, entries[0].Modified);
        Assert.Equal(0, entries[0].HeaderOffset);

        Assert.True(entries[1].Packed < 5000);
        Assert.Equal(5000, entries[1].Size);
        Assert.Equal(8, entries[1].Method);
        Assert.True(entries[1].Encrypted);
        Assert.True(entries[1].HeaderOffset > 0);
    }

    [Fact]
    public async Task Lists_a_finished_local_zip()
    {
        var path = Path.Combine(_dir, "archive.zip");
        File.WriteAllBytes(path, new ZipBuilder().Add("a.txt", Hello).Build());
        // The address leads nowhere, so a list can only have come from the file.
        var item = new DownloadItem { Url = "http://127.0.0.1:1/archive.zip", Directory = _dir, FileName = "archive.zip", Status = DownloadStatus.Completed };

        var entries = await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task Extracts_from_a_finished_local_zip()
    {
        var path = Path.Combine(_dir, "archive.zip");
        File.WriteAllBytes(path, new ZipBuilder().Add("a.txt", Hello, deflate: true).Build());
        var item = new DownloadItem { Url = "http://127.0.0.1:1/archive.zip", Directory = _dir, FileName = "archive.zip", Status = DownloadStatus.Completed };
        var entries = await ZipPreview.ListAsync(item, new Settings(), None);
        var folder = Path.Combine(_dir, "out");

        var results = await ZipPreview.ExtractAsync(item, new Settings(), entries, folder, null, None);

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public async Task A_finished_download_whose_file_is_gone_asks_the_server()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = new DownloadItem { Url = server.FileUrl, Directory = _dir, FileName = "gone.zip", Status = DownloadStatus.Completed };

        var entries = await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task Reads_zip64_records()
    {
        var content = Random(70_000);
        var builder = new ZipBuilder().Add("first.bin", Hello).Add("second.bin", content, deflate: true);
        var archive = builder.Build(zip64: true);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(new[] { "first.bin", "second.bin" }, entries.Select(e => e.Name));
        Assert.Equal(Hello.Length, entries[0].Size);
        Assert.Equal(content.Length, entries[1].Size);
        Assert.True(entries[1].HeaderOffset > 0);

        var results = await Extract(server.FileUrl, entries);
        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, "second.bin")));
    }

    [Fact]
    public async Task A_zip64_extra_field_holds_only_what_does_not_fit()
    {
        var archive = new ZipBuilder().Add("first.bin", Hello).Add("second.bin", Hello).Build(zip64OffsetOnly: true);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(Hello.Length, entries[1].Size);
        Assert.Equal(Hello.Length, entries[1].Packed);
        Assert.True(entries[1].HeaderOffset > 0);
        var results = await Extract(server.FileUrl, entries);
        Assert.Equal(Hello, File.ReadAllBytes(results[1].Path!));
    }

    [Fact]
    public async Task Finds_the_records_behind_the_longest_comment()
    {
        var builder = new ZipBuilder().Add("a.txt", Hello);
        // The ZIP64 records fall outside the last 64 KB, so they take a read of their own.
        var archive = builder.Build(zip64: true, comment: new byte[ushort.MaxValue]);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task Bytes_after_the_end_record_are_ignored()
    {
        var archive = new ZipBuilder().Add("a.txt", Hello).Build(trailing: new byte[100]);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task A_signature_in_the_comment_is_not_the_end_record()
    {
        // Looks like an end record whose own comment runs on past the end of the file.
        var comment = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(comment, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(20), ushort.MaxValue);
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build(comment: comment));

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task An_empty_archive_lists_nothing()
    {
        await using var server = await Serve(new ZipBuilder().Build());

        Assert.Empty(await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));
    }

    [Fact]
    public async Task An_unfinished_download_asks_the_server()
    {
        // Whatever lies at its name, it isn't this download yet.
        File.WriteAllText(Path.Combine(_dir, "archive.zip"), "not the archive");
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = new DownloadItem { Url = server.FileUrl, Directory = _dir, FileName = "archive.zip", Status = DownloadStatus.Paused };

        var entries = await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(70_000)]
    public async Task Something_else_is_not_an_archive(int size)
    {
        var path = Path.Combine(_dir, "not.zip");
        File.WriteAllBytes(path, new byte[size]);
        var item = new DownloadItem { Directory = _dir, FileName = "not.zip", Status = DownloadStatus.Completed };

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(item, new Settings(), None));

        Assert.Equal(ZipPreview.NotAnArchive, e.Message);
    }

    [Theory]
    [InlineData("directory signature")]
    [InlineData("directory cut short")]
    [InlineData("name past the directory")]
    [InlineData("directory past the file")]
    [InlineData("zip64 record signature")]
    [InlineData("zip64 extra field cut short")]
    [InlineData("zip64 extra field missing")]
    [InlineData("zip64 negative size")]
    [InlineData("zip64 locator past the file")]
    [InlineData("offset saturated with no zip64 records")]
    [InlineData("size saturated with no zip64 records")]
    public async Task Damage_is_reported_as_damage(string damage)
    {
        await using var server = await Serve(Damaged(damage));

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));

        Assert.Equal(ZipPreview.NotAnArchive, e.Message);
    }

    static byte[] Damaged(string damage)
    {
        var builder = new ZipBuilder().Add("a.txt", Hello);
        var archive = builder.Build(zip64: damage.StartsWith("zip64"));
        var directoryAt = (int)builder.DirectoryOffset;
        var extraAt = directoryAt + 46 + "a.txt".Length;
        var endAt = archive.Length - 22;
        var span = archive.AsSpan();
        switch (damage)
        {
            case "directory signature": span[directoryAt] ^= 0xFF; break;
            case "directory cut short": BinaryPrimitives.WriteUInt32LittleEndian(span[(endAt + 12)..], 10); break;
            case "name past the directory": BinaryPrimitives.WriteUInt16LittleEndian(span[(directoryAt + 28)..], 60000); break;
            case "directory past the file": BinaryPrimitives.WriteUInt32LittleEndian(span[(endAt + 16)..], 1_000_000); break;
            case "zip64 record signature": span[endAt - 20 - 56] ^= 0xFF; break;
            // The ZIP64 field sits after a harmless extra field of 6 bytes, with its header at +6 and data at +10.
            case "zip64 extra field cut short": BinaryPrimitives.WriteUInt16LittleEndian(span[(extraAt + 8)..], 8); break;
            case "zip64 extra field missing": BinaryPrimitives.WriteUInt16LittleEndian(span[(extraAt + 6)..], 0x7777); break;
            case "zip64 negative size": BinaryPrimitives.WriteInt64LittleEndian(span[(extraAt + 10)..], -1); break;
            case "zip64 locator past the file": BinaryPrimitives.WriteInt64LittleEndian(span[(endAt - 20 + 8)..], long.MaxValue - 10); break;
            case "offset saturated with no zip64 records": BinaryPrimitives.WriteUInt32LittleEndian(span[(endAt + 16)..], uint.MaxValue); break;
            case "size saturated with no zip64 records": BinaryPrimitives.WriteUInt32LittleEndian(span[(endAt + 12)..], uint.MaxValue); break;
        }
        return archive;
    }

    [Fact]
    public async Task A_directory_over_the_limit_is_refused_before_it_is_fetched()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build(claimedDirectorySize: 64L * 1024 * 1024 + 1));

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));

        Assert.Equal(ZipPreview.DirectoryTooLarge, e.Message);
        Assert.Equal(2, server.Ranges("/file.bin").Count);
    }

    [Fact]
    public async Task Split_archives_are_refused()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build(disk: 1));

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));

        Assert.Equal(ZipPreview.SplitArchive, e.Message);
    }

    [Fact]
    public async Task Split_zip64_archives_are_refused()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build(zip64: true, zip64Disks: 2));

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));

        Assert.Equal(ZipPreview.SplitArchive, e.Message);
    }

    [Fact]
    public async Task A_saturated_count_alone_needs_no_zip64_records()
    {
        // Exactly 65535 entries, as an archive without ZIP64 records holds them: the directory is still right.
        var archive = new ZipBuilder().Add("a.txt", Hello).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(archive.Length - 22 + 10), ushort.MaxValue);
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task Names_are_utf8_when_flagged_and_cp437_otherwise()
    {
        var archive = new ZipBuilder()
            .Add("", Hello, flags: ZipBuilder.Utf8Flag, rawName: Encoding.UTF8.GetBytes("日本語.txt"))
            .Add("", Hello, rawName: new byte[] { 0x82, (byte)'.', (byte)'t', (byte)'x', (byte)'t' })
            .Build();
        await using var server = await Serve(archive);

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("日本語.txt", entries[0].Name);
        Assert.Equal("é.txt", entries[1].Name);
    }

    [Theory]
    [InlineData(0, ZipBuilder.DosTime)]
    [InlineData((46 << 9) | (13 << 5) | 1, ZipBuilder.DosTime)]
    [InlineData((46 << 9) | (2 << 5) | 31, ZipBuilder.DosTime)]
    [InlineData((46 << 9) | (1 << 5) | 0, ZipBuilder.DosTime)]
    [InlineData(ZipBuilder.DosDate, 24 << 11)]
    [InlineData(ZipBuilder.DosDate, 60 << 5)]
    [InlineData(ZipBuilder.DosDate, 30)]
    public async Task An_invalid_date_is_no_date(int date, int time)
    {
        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello, date: (ushort)date, time: (ushort)time).Build());

        var saved = Assert.Single(results);
        Assert.Equal(DateTime.MinValue, saved.Entry.Modified);
        Assert.Null(saved.Problem);
        Assert.True(File.GetLastWriteTime(saved.Path!) > DateTime.Now.AddMinutes(-5));
    }

    [Fact]
    public async Task Extracts_chosen_entries_with_identical_bytes()
    {
        var stored = Random(300_000, 1);
        var deflated = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compress me, please. ", 20_000)));
        var archive = new ZipBuilder().Add("stored.bin", stored).Add("deflated.txt", deflated, deflate: true).Add("left-behind.txt", Hello).Build();
        await using var server = await Serve(archive);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        var results = await Extract(server.FileUrl, entries.Where(e => e.Name != "left-behind.txt"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Equal(stored, File.ReadAllBytes(results[0].Path!));
        Assert.Equal(deflated, File.ReadAllBytes(results[1].Path!));
        Assert.Equal(Path.Combine(_dir, "stored.bin"), results[0].Path);
        Assert.Equal(Path.Combine(_dir, "deflated.txt"), results[1].Path);
        Assert.False(File.Exists(Path.Combine(_dir, "left-behind.txt")));
        Assert.Empty(Directory.GetFiles(_dir, "*.admpart", SearchOption.AllDirectories));
    }

    /// <summary>A stream that can't seek, which makes ZipArchive write each entry with a data descriptor.</summary>
    sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reads_archives_a_real_tool_wrote(bool seekable)
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("real archive text. ", 5000)));
        var noise = Random(200_000, 2);
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(seekable ? file : new ForwardOnly(file), ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("sub/text.txt", CompressionLevel.Optimal).Open()) entry.Write(text);
            using (var entry = zip.CreateEntry("noise.bin", CompressionLevel.NoCompression).Open()) entry.Write(noise);
        }

        var results = await ExtractAll(file.ToArray());

        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Equal(text, File.ReadAllBytes(Path.Combine(_dir, "sub", "text.txt")));
        Assert.Equal(noise, File.ReadAllBytes(Path.Combine(_dir, "noise.bin")));
        Assert.Contains(results, r => r.Entry.Method == 8);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("a/../b.txt")]
    [InlineData(@"C:\x.txt")]
    [InlineData("C:x.txt")]
    [InlineData(@"\\server\x")]
    [InlineData("//server/x")]
    [InlineData("/x.txt")]
    [InlineData(@"\x.txt")]
    [InlineData("file.txt:stream")]
    [InlineData(".../x.txt")]
    [InlineData(".. /x.txt")]
    [InlineData("name.txt.")]
    [InlineData("name.txt ")]
    [InlineData("NUL")]
    [InlineData("a/con.txt")]
    [InlineData("COM1.tar.gz")]
    [InlineData("a*b.txt")]
    [InlineData("a\tb.txt")]
    public async Task Entry_paths_cannot_escape_the_folder(string name)
    {
        var folder = Path.Combine(_dir, "deep", "out");
        var archive = new ZipBuilder().Add(name, Hello).Add("fine.txt", Hello).Build();
        await using var server = await Serve(archive);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, folder, null, None);

        Assert.Equal(ZipPreview.UnsafePath, results[0].Problem);
        Assert.Null(results[0].Path);
        // Only the good entry landed, anywhere under this test's folder.
        Assert.Equal(new[] { Path.Combine(folder, "fine.txt") }, FilesLeft());
    }

    [Fact]
    public async Task Inflating_past_the_declared_size_fails()
    {
        var bomb = new byte[5_000_000];
        var results = await ExtractAll(new ZipBuilder().Add("bomb.bin", bomb, deflate: true, declaredSize: 1000, declaredCrc: ZipBuilder.CrcOf(bomb.AsSpan(0, 1000))).Build());

        var failed = Assert.Single(results);
        Assert.Equal(ZipPreview.LargerThanDeclared, failed.Problem);
        Assert.Null(failed.Path);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task Stored_data_past_the_declared_size_fails()
    {
        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello, declaredSize: Hello.Length - 1).Build());

        Assert.Equal(ZipPreview.LargerThanDeclared, Assert.Single(results).Problem);
        Assert.Empty(FilesLeft());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpacking_to_less_than_declared_fails(bool deflate)
    {
        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello, deflate: deflate, declaredSize: Hello.Length + 1).Build());

        Assert.Equal(ZipPreview.SizeMismatch, Assert.Single(results).Problem);
        Assert.Empty(FilesLeft());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crc_mismatch_fails_and_leaves_no_file(bool deflate)
    {
        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello, deflate: deflate, declaredCrc: ZipBuilder.CrcOf(Hello) ^ 1).Build());

        var failed = Assert.Single(results);
        Assert.Equal(ZipPreview.CrcMismatch, failed.Problem);
        Assert.Null(failed.Path);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task Damaged_packed_data_fails_and_leaves_no_file()
    {
        // The first bits of a Deflate stream name its block type, and this one has none.
        var notDeflate = new byte[100];
        notDeflate[0] = 0x07;
        var archive = new ZipBuilder().Add("a.bin", notDeflate, method: ZipBuilder.Deflated).Build();

        var failed = Assert.Single(await ExtractAll(archive));

        Assert.Equal(ZipPreview.DamagedData, failed.Problem);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task An_entry_past_the_end_of_the_file_fails()
    {
        var builder = new ZipBuilder().Add("a.txt", Hello);
        var archive = builder.Build();
        // Its local header claims nothing wrong; only the directory's size for it runs off the end.
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan((int)builder.DirectoryOffset + 20), 1_000_000);

        var failed = Assert.Single(await ExtractAll(archive));

        Assert.Equal(ZipPreview.NotAnArchive, failed.Problem);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task An_entry_without_its_local_header_fails()
    {
        var archive = new ZipBuilder().Add("a.txt", Hello).Build();
        archive[0] ^= 0xFF;

        var failed = Assert.Single(await ExtractAll(archive));

        Assert.Equal(ZipPreview.NotAnArchive, failed.Problem);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task Encrypted_entries_are_refused()
    {
        var results = await ExtractAll(new ZipBuilder().Add("secret.txt", Hello, flags: ZipBuilder.EncryptedFlag).Add("open.txt", Hello).Build());

        Assert.Equal(ZipPreview.EntryEncrypted, results[0].Problem);
        Assert.Null(results[0].Path);
        Assert.False(File.Exists(Path.Combine(_dir, "secret.txt")));
        Assert.Equal(Hello, File.ReadAllBytes(results[1].Path!));
    }

    [Fact]
    public async Task Other_methods_are_skipped_with_a_reason()
    {
        var results = await ExtractAll(new ZipBuilder().Add("bzip.bin", Hello, method: 12).Build());

        var skipped = Assert.Single(results);
        Assert.Contains("12", skipped.Problem);
        Assert.Null(skipped.Path);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task A_taken_name_becomes_numbered()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "already here");

        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello).Add("a.txt", Hello).Build());

        Assert.Equal("already here", File.ReadAllText(Path.Combine(_dir, "a.txt")));
        Assert.Equal(Path.Combine(_dir, "a (1).txt"), results[0].Path);
        Assert.Equal(Path.Combine(_dir, "a (2).txt"), results[1].Path);
        Assert.Equal(Hello, File.ReadAllBytes(results[1].Path!));
    }

    [Fact]
    public async Task A_part_file_already_there_is_left_alone()
    {
        // It could be a running download's.
        var theirs = Path.Combine(_dir, "a.txt.admpart");
        File.WriteAllText(theirs, "someone else's progress");

        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello).Build());

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal("someone else's progress", File.ReadAllText(theirs));
        Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task Folders_are_created_as_needed()
    {
        var results = await ExtractAll(new ZipBuilder()
            .Add("one/two/three.txt", Hello)
            .Add(@"back\slash.txt", Hello)
            .Add("double//slash.txt", Hello)
            .Add("empty/", Array.Empty<byte>())
            .Add(@"empty2\", Array.Empty<byte>())
            .Add("empty.txt", Array.Empty<byte>())
            .Build());

        Assert.All(results, r => Assert.Null(r.Problem));
        Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(_dir, "one", "two", "three.txt")));
        Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(_dir, "back", "slash.txt")));
        Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(_dir, "double", "slash.txt")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "empty")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "empty2")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(_dir, "empty.txt")));
    }

    [Fact]
    public async Task A_dot_folder_in_a_name_means_nothing()
    {
        var results = await ExtractAll(new ZipBuilder().Add("./a/./b.txt", Hello).Build());

        Assert.Equal(Path.Combine(_dir, "a", "b.txt"), Assert.Single(results).Path);
    }

    [Fact]
    public async Task A_folder_entry_is_marked_as_one()
    {
        await using var server = await Serve(new ZipBuilder().Add("f/", Array.Empty<byte>()).Add(@"g\", Array.Empty<byte>()).Add("f/a", Hello).Build());

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(new[] { true, true, false }, entries.Select(e => e.IsFolder));
    }

    [Fact]
    public async Task One_entry_failing_does_not_stop_the_others()
    {
        // "a" is a file, so "a/b.txt" can't be made, and the entries after it still land.
        var results = await ExtractAll(new ZipBuilder().Add("a", Hello).Add("a/b.txt", Hello).Add("c.txt", Hello).Build());

        Assert.Null(results[0].Problem);
        Assert.NotNull(results[1].Problem);
        Assert.Null(results[1].Path);
        Assert.Equal(Hello, File.ReadAllBytes(results[2].Path!));
    }

    [Fact]
    public async Task A_declared_size_beyond_the_disk_is_refused()
    {
        var results = await ExtractAll(new ZipBuilder().Add("huge.bin", Hello, declaredSize: long.MaxValue / 2).Build(zip64: true));

        Assert.Contains("Not enough disk space", Assert.Single(results).Problem);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task Extracted_files_keep_their_time_and_carry_the_mark_of_the_web()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        var item = Remote(server.FileUrl);
        item.Referrer = "http://site.example/page";

        var results = await ZipPreview.ExtractAsync(item, new Settings(), entries, _dir, null, None);

        var saved = Assert.Single(results).Path!;
        Assert.Equal(ZipBuilder.DosModified, File.GetLastWriteTime(saved));
        var mark = File.ReadAllText(saved + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", mark);
        Assert.Contains("ReferrerUrl=http://site.example/page", mark);
        Assert.Contains("HostUrl=" + server.FileUrl, mark);
    }

    [Fact]
    public async Task The_mark_of_the_web_can_be_turned_off()
    {
        var results = await ExtractAll(new ZipBuilder().Add("a.txt", Hello).Build(), new Settings { MarkDownloads = false });

        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(Assert.Single(results).Path + ":Zone.Identifier"));
    }

    sealed class Reports(Action<long>? also = null) : IProgress<long>
    {
        public List<long> Values { get; } = new();

        public void Report(long value)
        {
            Values.Add(value);
            also?.Invoke(value);
        }
    }

    [Fact]
    public async Task Progress_counts_the_bytes_written()
    {
        var one = Random(100_000, 1);
        var two = Encoding.ASCII.GetBytes(new string('z', 50_000));
        await using var server = await Serve(new ZipBuilder().Add("one", one).Add("skipped", Hello, flags: ZipBuilder.EncryptedFlag).Add("two", two, deflate: true).Build());
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        var progress = new Reports();

        await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, progress, None);

        Assert.Equal(150_000, progress.Values[^1]);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
    }

    [Fact]
    public async Task Cancelling_leaves_no_part_file()
    {
        var archive = new ZipBuilder().Add("a.bin", Random(4 * 1024 * 1024)).Build();
        await using var server = await FtpTestServer.StartAsync(archive);
        server.PerConnectionKBps = 256;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        using var stop = new CancellationTokenSource();
        var progress = new Reports(_ => stop.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, progress, stop.Token));

        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task Server_without_ranges_cannot_preview()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.NoRangesUrl), new Settings(), None));

        Assert.Equal("The server can't send parts of this file, so it can't be previewed.", e.Message);
    }

    [Fact]
    public async Task Server_that_stops_sending_ranges_cannot_preview()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.RangesOnceUrl), new Settings(), None));

        Assert.Equal(ZipPreview.NoRanges, e.Message);
    }

    [Fact]
    public async Task Ftp_server_without_rest_cannot_preview()
    {
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.txt", Hello).Build());
        server.RefuseRest = true;

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None));

        Assert.Equal(ZipPreview.NoRanges, e.Message);
    }

    [Fact]
    public async Task Ftp_archive_lists_too()
    {
        var content = Random(300_000, 3);
        var archive = new ZipBuilder().Add("big.bin", Random(1024 * 1024, 4)).Add("wanted.bin", content, deflate: true).Build();
        await using var server = await FtpTestServer.StartAsync(archive);
        server.PerConnectionKBps = 0;

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal(new[] { "big.bin", "wanted.bin" }, entries.Select(e => e.Name));

        var results = await Extract(server.FileUrl, entries.Where(e => e.Name == "wanted.bin"));

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
        Assert.Contains(entries[1].HeaderOffset, server.Restarts);
    }

    [Fact]
    public async Task A_dropped_connection_starts_the_entry_again()
    {
        var content = Random(300_000, 5);
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", content).Build());
        server.PerConnectionKBps = 0;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        // Past the local header, whose own read starts at byte 0, so it is the data that is cut off.
        server.DropOnlyFrom = 1;
        server.DropOnceAfterBytes = 100_000;
        var progress = new Reports();

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, progress, None);

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
        Assert.Equal(1, server.Drops);
        // Some was written before the drop, and the count went back when the entry started again.
        Assert.Contains(progress.Values.Zip(progress.Values.Skip(1)), pair => pair.Second < pair.First);
        Assert.Equal(content.Length, progress.Values[^1]);
    }

    [Fact]
    public async Task A_lost_connection_while_listing_is_tried_again()
    {
        var archive = new ZipBuilder().Add("a.bin", Random(200_000, 6)).Build();
        await using var server = await FtpTestServer.StartAsync(archive);
        server.PerConnectionKBps = 0;
        server.DropOnceAfterBytes = 10;

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);

        Assert.Equal("a.bin", Assert.Single(entries).Name);
        Assert.Equal(1, server.Drops);
    }

    static readonly RetryPolicy Quick = new()
    {
        FirstBackoff = TimeSpan.FromMilliseconds(20),
        MaxBackoff = TimeSpan.FromMilliseconds(20),
        ReadTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task A_silent_connection_is_tried_again()
    {
        // Half of the first range, then silence; the requests after it are answered.
        await using var server = await Serve(new ZipBuilder().Add("a.bin", Random(200_000, 8)).Build());

        var entries = await ZipPreview.ListAsync(Remote(server.StallUrl), new Settings(), Quick, None);

        Assert.Equal("a.bin", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task A_server_that_never_answers_is_given_up_on()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => ZipPreview.ListAsync(Remote(server.HangingScriptUrl), new Settings(), Quick, None));

        Assert.Equal(ZipPreview.NoAnswer, e.Message);
    }

    [Fact]
    public async Task An_entry_whose_data_never_comes_says_so()
    {
        var archive = new ZipBuilder().Add("a.bin", Random(200_000, 9)).Build();
        await using var server = await FtpTestServer.StartAsync(archive);
        server.PerConnectionKBps = 0;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        // A data connection that accepts and sends nothing for longer than a read's deadline.
        server.SilentFrom = 1;
        var quick = new RetryPolicy { FirstBackoff = Quick.FirstBackoff, MaxBackoff = Quick.MaxBackoff, ReadTimeout = TimeSpan.FromMilliseconds(300) };

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, null, quick, None);

        Assert.Equal(ZipPreview.NoAnswer, Assert.Single(results).Problem);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task A_request_never_answered_says_so()
    {
        var archive = new ZipBuilder().Add("a.bin", Random(200_000, 15)).Build();
        await using var server = await FtpTestServer.StartAsync(archive);
        server.PerConnectionKBps = 0;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        // RETR gets no reply at all, so the data connection never opens.
        server.NoRetrReplyFrom = 1;
        var quick = new RetryPolicy { FirstBackoff = Quick.FirstBackoff, MaxBackoff = Quick.MaxBackoff, ReadTimeout = TimeSpan.FromMilliseconds(300) };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, null, quick, None);

        Assert.Equal(ZipPreview.NoAnswer, Assert.Single(results).Problem);
        Assert.Empty(FilesLeft());
        // The preview's own deadline ended it, not the FTP client's 30 seconds for a reply.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_read_cut_short_is_not_taken_for_the_whole()
    {
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", Random(200_000, 10)).Build());
        server.PerConnectionKBps = 0;
        server.DropGracefully = true;
        server.DropClaimsSuccess = true;
        server.DropOnceAfterBytes = 10;

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), Quick, None);

        Assert.Equal("a.bin", Assert.Single(entries).Name);
        Assert.Equal(1, server.Drops);
    }

    [Fact]
    public async Task An_entry_cut_short_is_not_taken_for_the_whole()
    {
        var content = Random(200_000, 11);
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", content).Build());
        server.PerConnectionKBps = 0;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        server.DropGracefully = true;
        server.DropClaimsSuccess = true;
        server.DropOnlyFrom = 1;
        server.DropOnceAfterBytes = 50_000;

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, null, Quick, None);

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
        Assert.Equal(1, server.Drops);
    }

    [Fact]
    public async Task A_slow_read_that_keeps_coming_is_left_to_finish()
    {
        // 16 KB every half second: the deadline passes well before the end, but never between two pieces.
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", Random(200_000, 12)).Build());
        server.PerConnectionKBps = 32;
        var patient = new RetryPolicy { FirstBackoff = Quick.FirstBackoff, MaxBackoff = Quick.MaxBackoff, ReadTimeout = TimeSpan.FromMilliseconds(1500) };

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), patient, None);

        Assert.Equal("a.bin", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task A_slow_entry_that_keeps_coming_is_left_to_finish()
    {
        var content = Random(150_000, 13);
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", content).Build());
        server.PerConnectionKBps = 0;
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        server.PerConnectionKBps = 32;
        var patient = new RetryPolicy { FirstBackoff = Quick.FirstBackoff, MaxBackoff = Quick.MaxBackoff, ReadTimeout = TimeSpan.FromMilliseconds(1500) };

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, null, patient, None);

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
        Assert.Equal(0, server.Drops);
    }

    [Fact]
    public async Task A_server_taking_one_connection_at_a_time_still_works()
    {
        var content = Random(100_000, 14);
        await using var server = await FtpTestServer.StartAsync(new ZipBuilder().Add("a.bin", content).Build());
        server.PerConnectionKBps = 0;
        server.MaxControlConnections = 1;

        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, _dir, null, None);

        Assert.Null(Assert.Single(results).Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
    }

    [Fact]
    public async Task Listing_leaves_the_download_as_it_was()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.RedirectUrl);
        item.ResumeSupported = false;
        item.TotalBytes = -1;

        await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.False(item.ResumeSupported);
        Assert.Equal(-1, item.TotalBytes);
        Assert.Equal(server.RedirectUrl, item.Url);
        Assert.Empty(item.SegmentsSnapshot);
    }

    [Fact]
    public async Task Redirects_are_asked_once_and_the_ranges_go_where_they_lead()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());

        var entries = await ZipPreview.ListAsync(Remote(server.RedirectUrl), new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
        Assert.Equal(1, server.Hits("/redirect/file.bin"));
        Assert.Equal(2, server.Hits("/file.bin"));
    }

    [Fact]
    public async Task The_downloads_login_is_used()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.SecureUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => ZipPreview.ListAsync(item, new Settings(), None));

        item.UserName = "user";
        item.ProtectedPassword = Secrets.Protect("pass");
        Assert.Equal("a.txt", Assert.Single(await ZipPreview.ListAsync(item, new Settings(), None)).Name);
    }

    [Fact]
    public async Task The_downloads_cookies_are_sent()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.BaseUrl + "/cookies.bin");
        item.Cookies = "session=1";

        var entries = await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
        Assert.All(server.CookiesSeen, seen => Assert.Equal("session=1", seen.Cookie));
        Assert.Equal(2, server.CookiesSeen.Count);
    }

    [Fact]
    public async Task The_downloads_referer_and_user_agent_are_sent()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.BaseUrl + "/cookies.bin");
        item.Referrer = "http://site.example/page";
        item.UserAgent = "ADM test agent";

        await ZipPreview.ListAsync(item, new Settings(), None);

        Assert.All(server.HeadersSeen, seen =>
        {
            Assert.Equal("http://site.example/page", seen.Referer);
            Assert.Equal("ADM test agent", seen.UserAgent);
        });
    }

    [Fact]
    public async Task The_proxy_in_the_settings_is_used()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var address = new Uri(server.BaseUrl);
        var settings = new Settings { Proxy = new ProxySettings { Mode = ProxyMode.Manual, Kind = ProxyKind.Http, Host = address.Host, Port = address.Port, Bypass = "" } };

        var entries = await ZipPreview.ListAsync(Remote(server.ProxiedOnlyUrl), settings, None);

        Assert.Equal("a.txt", Assert.Single(entries).Name);
        Assert.True(server.ProxiedRequests >= 2);
    }

    [Fact]
    public async Task The_browsers_proxy_is_tried_when_the_own_route_fails()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.ProxiedOnlyUrl);
        item.BrowserProxy = server.BaseUrl;

        var entries = await ZipPreview.ListAsync(item, new Settings(), None);
        Assert.Equal("a.txt", Assert.Single(entries).Name);

        var results = await ZipPreview.ExtractAsync(item, new Settings(), entries, _dir, null, None);
        Assert.Equal(Hello, File.ReadAllBytes(Assert.Single(results).Path!));
    }

    [Fact]
    public async Task When_the_browsers_proxy_fails_too_the_downloads_own_error_stands()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.ForbiddenUrl);
        item.BrowserProxy = "http://127.0.0.1:1";

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ZipPreview.ListAsync(item, new Settings(), Quick, None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, e.StatusCode);
    }

    [Fact]
    public async Task The_browsers_proxy_can_be_turned_off()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var item = Remote(server.ProxiedOnlyUrl);
        item.BrowserProxy = server.BaseUrl;

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ZipPreview.ListAsync(item, new Settings { UseBrowserProxyOnErrors = false }, None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, e.StatusCode);
        Assert.Equal(0, server.ProxiedRequests);
    }

    // Through the manager: the limits a download answers to.

    const int MB = 1024 * 1024;

    (DownloadManager Manager, LockedContext Ui) NewManager()
    {
        var ui = new LockedContext();
        var m = new DownloadManager(Path.Combine(_dir, "state", "state.json"), ui);
        m.State.Settings.DownloadRoot = _dir;
        return (m, ui);
    }

    /// <summary>Makes the download limit hold, as a tick after the limit is passed does.</summary>
    static void ReachLimit(DownloadManager m, LockedContext ui)
    {
        m.State.Settings.QuotaEnabled = true;
        m.State.Settings.QuotaMB = 1;
        m.State.Quota.Add(2 * MB, DateTime.Now, m.State.Settings.QuotaHours);
        ui.Run(() => m.Tick(DateTime.Now));
        Assert.NotNull(m.LimitHoldsUntil);
    }

    [Fact]
    public async Task The_speed_limiter_holds_an_archive_back()
    {
        var content = Random(3 * MB);
        await using var server = await Serve(new ZipBuilder().Add("big.bin", content).Build());
        var (m, _) = NewManager();
        m.Limiter.BytesPerSecond = MB;
        var folder = Path.Combine(_dir, "out");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var entries = await m.ListZipAsync(Remote(server.FileUrl), None);
        var results = await m.ExtractZipAsync(Remote(server.FileUrl), entries, folder, null, None);

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
        Assert.Null(results[0].Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
    }

    [Fact]
    public async Task A_downloads_own_speed_limit_holds_its_archive_back()
    {
        await using var server = await Serve(new ZipBuilder().Add("big.bin", Random(3 * MB)).Build());
        var (m, _) = NewManager();
        var item = Remote(server.FileUrl);
        item.SpeedLimitKBps = 1024;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var entries = await m.ListZipAsync(item, None);
        await m.ExtractZipAsync(item, entries, Path.Combine(_dir, "out"), null, None);

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Waiting_for_the_speed_limit_is_not_a_silent_server()
    {
        var content = Random(96 * 1024);
        await using var server = await Serve(new ZipBuilder().Add("slow.bin", content).Build());
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        // Each read waits far longer for the limiter than the server is allowed to stay silent.
        var quick = new RetryPolicy { FirstBackoff = Quick.FirstBackoff, MaxBackoff = Quick.MaxBackoff, ReadTimeout = TimeSpan.FromMilliseconds(200) };
        var limits = new ReadLimits(new RateLimiter { BytesPerSecond = 32 * 1024 }, new RateLimiter(), _ => { });

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, Path.Combine(_dir, "out"), null, quick, None, limits);

        Assert.Null(results[0].Problem);
        Assert.Equal(content, File.ReadAllBytes(results[0].Path!));
    }

    [Fact]
    public async Task An_archives_bytes_count_toward_the_download_limit()
    {
        var archive = new ZipBuilder().Add("big.bin", Random(2 * MB)).Build();
        await using var server = await Serve(archive);
        var (m, ui) = NewManager();
        m.State.Settings.QuotaEnabled = true;
        m.State.Settings.QuotaMB = 1000;

        var entries = await m.ListZipAsync(Remote(server.FileUrl), None);
        await m.ExtractZipAsync(Remote(server.FileUrl), entries, Path.Combine(_dir, "out"), null, None);
        ui.Run(() => m.Tick(DateTime.Now));

        Assert.True(m.State.Quota.Bytes >= 2 * MB, $"counted {m.State.Quota.Bytes}");
        Assert.True(m.State.Quota.Bytes <= archive.Length * 2L, $"counted {m.State.Quota.Bytes}");
    }

    [Fact]
    public async Task Listing_an_archive_counts_toward_the_download_limit()
    {
        await using var server = await Serve(new ZipBuilder().Add("big.bin", Random(MB)).Build());
        var (m, ui) = NewManager();
        m.State.Settings.QuotaEnabled = true;
        m.State.Settings.QuotaMB = 1000;

        await m.ListZipAsync(Remote(server.FileUrl), None);
        ui.Run(() => m.Tick(DateTime.Now));

        Assert.True(m.State.Quota.Bytes > 0);
    }

    [Fact]
    public async Task Reaching_the_download_limit_leaves_a_read_from_disk_alone()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var path = Path.Combine(_dir, "archive.zip");
        File.WriteAllBytes(path, new ZipBuilder().Add("a.txt", Hello).Build());
        var item = new DownloadItem { Url = "http://127.0.0.1:9/archive.zip", FileName = "archive.zip", Directory = _dir, Status = DownloadStatus.Completed };
        // A scan that takes a while keeps the read going while the limit is reached.
        var scanner = Path.Combine(_dir, "wait.cmd");
        File.WriteAllText(scanner, "@ping -n 3 127.0.0.1 >nul\r\n");
        var (m, ui) = NewManager();
        m.State.Settings.VirusScanner = scanner;
        var entries = await m.ListZipAsync(item, None);

        var taking = m.ExtractZipAsync(item, entries, Path.Combine(_dir, "out"), null, None);
        await Task.Delay(300);
        ReachLimit(m, ui);

        var result = Assert.Single(await taking);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task Reaching_the_download_limit_stops_taking_files_out()
    {
        await using var server = await Serve(new ZipBuilder().Add("big.bin", Random(8 * MB)).Build());
        server.PerRequestKBps = 1024;
        var (m, ui) = NewManager();
        m.State.Settings.QuotaEnabled = true;
        m.State.Settings.QuotaMB = 1;
        var folder = Path.Combine(_dir, "out");
        var entries = await m.ListZipAsync(Remote(server.FileUrl), None);

        var taking = m.ExtractZipAsync(Remote(server.ThrottledUrl), entries, folder, null, None);
        while (!taking.IsCompleted)
        {
            ui.Run(() => m.Tick(DateTime.Now));
            await Task.Delay(50);
        }

        var e = await Assert.ThrowsAsync<ZipPreviewException>(() => taking);
        Assert.StartsWith("The download limit has been reached.", e.Message);
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_limit_that_holds_refuses_to_ask_the_server()
    {
        await using var server = await Serve(new ZipBuilder().Add("a.txt", Hello).Build());
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), None);
        var asked = server.Ranges("/file.bin").Count;
        var (m, ui) = NewManager();
        ReachLimit(m, ui);

        var listing = await Assert.ThrowsAsync<ZipPreviewException>(() => m.ListZipAsync(Remote(server.FileUrl), None));
        var taking = await Assert.ThrowsAsync<ZipPreviewException>(() => m.ExtractZipAsync(Remote(server.FileUrl), entries, Path.Combine(_dir, "out"), null, None));

        Assert.StartsWith("The download limit has been reached.", listing.Message);
        Assert.StartsWith("The download limit has been reached.", taking.Message);
        Assert.Equal(asked, server.Ranges("/file.bin").Count);
    }

    [Fact]
    public async Task A_finished_file_on_disk_answers_to_no_limit()
    {
        var path = Path.Combine(_dir, "archive.zip");
        File.WriteAllBytes(path, new ZipBuilder().Add("big.bin", Random(3 * MB)).Build());
        var item = new DownloadItem { Url = "http://127.0.0.1:9/archive.zip", FileName = "archive.zip", Directory = _dir, Status = DownloadStatus.Completed };
        var (m, ui) = NewManager();
        ReachLimit(m, ui);
        var counted = m.State.Quota.Bytes;
        m.Limiter.BytesPerSecond = MB;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var entries = await m.ListZipAsync(item, None);
        var results = await m.ExtractZipAsync(item, entries, Path.Combine(_dir, "out"), null, None);
        ui.Run(() => m.Tick(DateTime.Now));

        Assert.Null(results[0].Problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
        Assert.Equal(counted, m.State.Quota.Bytes);
    }

    [Fact]
    public async Task Exit_stops_taking_files_out_and_leaves_no_part_file()
    {
        await using var server = await Serve(new ZipBuilder().Add("big.bin", Random(8 * MB)).Build());
        server.PerRequestKBps = 256;
        var (m, _) = NewManager();
        var folder = Path.Combine(_dir, "out");
        var entries = await m.ListZipAsync(Remote(server.FileUrl), None);

        var taking = m.ExtractZipAsync(Remote(server.ThrottledUrl), entries, folder, null, None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "no part file appeared");
            await Task.Delay(20);
        }
        await m.ShutdownAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taking);
        Assert.Empty(Directory.GetFiles(folder));
    }
}
