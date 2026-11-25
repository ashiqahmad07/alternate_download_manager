using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;

namespace ADM.Core;

/// <param name="Packed">The bytes it takes up in the archive.</param>
/// <param name="Size">The bytes it unpacks to.</param>
/// <param name="Modified">The archive's local time; <see cref="DateTime.MinValue"/> when it holds no valid one.</param>
/// <param name="Method">How it is packed: 0 is stored, 8 is Deflate.</param>
/// <param name="HeaderOffset">Where its local header starts in the archive.</param>
public sealed record ZipEntryInfo(string Name, long Packed, long Size, DateTime Modified, int Method, bool Encrypted, uint Crc32, long HeaderOffset)
{
    /// <summary>A folder's own entry, which holds no data.</summary>
    public bool IsFolder => Name.EndsWith('/') || Name.EndsWith('\\');
}

/// <param name="Path">Where the entry landed; null when it wasn't taken out.</param>
/// <param name="Problem">Why it wasn't taken out; null when it was.</param>
/// <param name="ScanResult">What the virus scan reported of a file it left in place; null when it found nothing, or there was none.</param>
public sealed record ZipExtractResult(ZipEntryInfo Entry, string? Path, string? Problem, string? ScanResult = null);

/// <summary>Why an archive can't be previewed, or an entry can't be taken out, in words for the user.</summary>
public sealed class ZipPreviewException(string message) : Exception(message);

/// <summary>What reading an archive from a server answers to, as a download does.</summary>
/// <param name="Overall">The speed limiter for all downloads.</param>
/// <param name="Download">The download's own speed limit.</param>
/// <param name="Counted">Told the bytes read, for the download limit; may be called from any thread.</param>
internal sealed record ReadLimits(RateLimiter Overall, RateLimiter Download, Action<int> Counted);

/// <summary>
/// Lists a ZIP archive and takes files out of it without downloading the rest. A finished download is read from
/// disk; any other asks for byte ranges through its segment source, so HTTP, HTTPS and FTP all work, with the
/// download's cookies, login and proxy. The archive's content is the server's to choose, so nothing it says is
/// trusted: names can't leave the chosen folder, and sizes and CRCs are checked before a file lands.
/// </summary>
public static class ZipPreview
{
    internal const string NoRanges = "The server can't send parts of this file, so it can't be previewed.";
    internal const string NoAnswer = "The server stopped answering.";
    internal const string NotAnArchive = "This isn't a ZIP archive, or the archive is damaged.";
    internal const string DirectoryTooLarge = "This archive's list of files is too large to preview.";
    internal const string SplitArchive = "This archive is split across several files, so it can't be previewed.";
    internal const string UnsafePath = "Its name points outside the chosen folder, or can't be a file name in Windows.";
    internal const string EntryEncrypted = "It is encrypted.";
    internal const string LargerThanDeclared = "It unpacks to more than the archive declares.";
    internal const string SizeMismatch = "It unpacks to less than the archive declares.";
    internal const string CrcMismatch = "It failed its CRC-32 check.";
    internal const string DamagedData = "Its packed data is damaged.";

    const uint LocalSignature = 0x04034b50, CentralSignature = 0x02014b50, EndSignature = 0x06054b50;
    const uint Zip64RecordSignature = 0x06064b50, Zip64LocatorSignature = 0x07064b50;
    const int LocalHeaderBytes = 30, CentralHeaderBytes = 46, EndRecordBytes = 22, Zip64LocatorBytes = 20, Zip64RecordBytes = 56;
    const int MethodStored = 0, MethodDeflate = 8;
    const int FlagEncrypted = 1, FlagUtf8 = 0x800;
    /// <summary>The end record with the longest comment it can have, which also holds an ordinary archive's ZIP64 records.</summary>
    const int TailBytes = 64 * 1024 + EndRecordBytes;
    const int MaxDirectoryBytes = 64 * 1024 * 1024;
    const int BufferSize = 64 * 1024;

    static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>The archive's entries, in the order of its directory.</summary>
    /// <exception cref="ZipPreviewException">It can't be previewed; the message says why.</exception>
    public static Task<IReadOnlyList<ZipEntryInfo>> ListAsync(DownloadItem item, Settings settings, CancellationToken ct) =>
        ListAsync(item, settings, RetryPolicy.For(settings), ct);

    internal static async Task<IReadOnlyList<ZipEntryInfo>> ListAsync(DownloadItem item, Settings settings, RetryPolicy retry, CancellationToken ct, ReadLimits? limits = null)
    {
        using var archive = await ArchiveReader.OpenAsync(item, settings, retry, limits, ct);
        return await ReadDirectoryAsync(archive, ct);
    }

    /// <summary>
    /// Takes these entries out into <paramref name="folder"/>, one at a time. An entry that can't be taken out
    /// doesn't stop the others; each result says where its entry landed, or why it didn't.
    /// </summary>
    /// <param name="progress">The bytes written so far by this call; goes back when an entry starts again after a lost connection.</param>
    public static Task<IReadOnlyList<ZipExtractResult>> ExtractAsync(
        DownloadItem item, Settings settings, IEnumerable<ZipEntryInfo> entries, string folder, IProgress<long>? progress, CancellationToken ct) =>
        ExtractAsync(item, settings, entries, folder, progress, RetryPolicy.For(settings), ct);

    internal static async Task<IReadOnlyList<ZipExtractResult>> ExtractAsync(
        DownloadItem item, Settings settings, IEnumerable<ZipEntryInfo> entries, string folder, IProgress<long>? progress, RetryPolicy retry, CancellationToken ct, ReadLimits? limits = null)
    {
        using var archive = await ArchiveReader.OpenAsync(item, settings, retry, limits, ct);
        var results = new List<ZipExtractResult>();
        long done = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = await ExtractEntryAsync(archive, entry, folder, item, settings, written => progress?.Report(done + written), ct);
                done += entry.Size;
                results.Add(await ScanAsync(entry, path, settings, ct));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                results.Add(new ZipExtractResult(entry, null, e.Message));
            }
        }
        return results;
    }

    /// <summary>A finished download whose file is there is read from disk, and needs no network.</summary>
    internal static bool ReadsFromDisk(DownloadItem item) => item.Status == DownloadStatus.Completed && File.Exists(item.FullPath);

    /// <summary>
    /// The scanner the user chose sees each file once it has its name, as it does a download's. A scan stopped
    /// partway takes the file with it: nothing will report that file, and nothing has checked it.
    /// </summary>
    static async Task<ZipExtractResult> ScanAsync(ZipEntryInfo entry, string path, Settings settings, CancellationToken ct)
    {
        if (entry.IsFolder || string.IsNullOrWhiteSpace(settings.VirusScanner)) return new ZipExtractResult(entry, path, null);
        ScanOutcome outcome;
        try
        {
            outcome = await VirusScan.RunAsync(settings.VirusScanner, settings.VirusScannerArguments ?? "", path, ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }
        return outcome.FileRemoved
            ? new ZipExtractResult(entry, null, VirusScan.RemovedFile)
            : new ZipExtractResult(entry, path, null, VirusScan.Finding(outcome));
    }

    static async Task<List<ZipEntryInfo>> ReadDirectoryAsync(ArchiveReader archive, CancellationToken ct)
    {
        if (archive.Length < EndRecordBytes) throw new ZipPreviewException(NotAnArchive);
        var tail = await archive.ReadTailAsync(TailBytes, ct);
        int end = FindEndRecord(tail);
        if (end < 0) throw new ZipPreviewException(NotAnArchive);

        // In a ZIP64 archive these two may be saturated, and its locator says how many disks there are.
        if (U16(tail, end + 4) is not (0 or ushort.MaxValue) || U16(tail, end + 6) is not (0 or ushort.MaxValue))
            throw new ZipPreviewException(SplitArchive);

        long size = U32(tail, end + 12), offset = U32(tail, end + 16);
        if (size == uint.MaxValue || offset == uint.MaxValue || U16(tail, end + 10) == ushort.MaxValue)
        {
            long endAt = archive.Length - tail.Length + end;
            if (await ReadZip64Async(archive, endAt, ct) is { } big) (size, offset) = big;
            // Exactly 65535 entries saturate the count with no ZIP64 records behind it; the other two can't be real.
            else if (size == uint.MaxValue || offset == uint.MaxValue) throw new ZipPreviewException(NotAnArchive);
        }

        if (size > MaxDirectoryBytes) throw new ZipPreviewException(DirectoryTooLarge);
        if (size < 0 || offset < 0 || offset > archive.Length - size) throw new ZipPreviewException(NotAnArchive);
        return ParseDirectory(await archive.ReadAsync(offset, (int)size, ct));
    }

    /// <summary>The last end record whose comment fits in what follows it; a file can have bytes after its comment.</summary>
    static int FindEndRecord(ReadOnlySpan<byte> tail)
    {
        for (int at = tail.Length - EndRecordBytes; at >= 0; at--)
            if (U32(tail, at) == EndSignature && at + EndRecordBytes + U16(tail, at + 20) <= tail.Length) return at;
        return -1;
    }

    /// <summary>The directory's size and place from the ZIP64 records; null when there are none.</summary>
    static async Task<(long Size, long Offset)?> ReadZip64Async(ArchiveReader archive, long endAt, CancellationToken ct)
    {
        if (endAt < Zip64LocatorBytes) return null;
        var locator = await archive.ReadAsync(endAt - Zip64LocatorBytes, Zip64LocatorBytes, ct);
        if (U32(locator, 0) != Zip64LocatorSignature) return null;
        if (U32(locator, 16) > 1) throw new ZipPreviewException(SplitArchive);

        long recordAt = I64(locator, 8);
        if (recordAt < 0 || recordAt > archive.Length - Zip64RecordBytes) throw new ZipPreviewException(NotAnArchive);
        var record = await archive.ReadAsync(recordAt, Zip64RecordBytes, ct);
        if (U32(record, 0) != Zip64RecordSignature) throw new ZipPreviewException(NotAnArchive);
        return (I64(record, 40), I64(record, 48));
    }

    /// <summary>Every entry the directory holds. Its own count isn't used: it wraps at 65536 in archives without ZIP64 records.</summary>
    static List<ZipEntryInfo> ParseDirectory(ReadOnlySpan<byte> directory)
    {
        var entries = new List<ZipEntryInfo>();
        // The ZIP format's code page for names not marked as UTF-8.
        var legacy = CodePagesEncodingProvider.Instance.GetEncoding(437) ?? Encoding.Latin1;
        int at = 0;
        while (at < directory.Length)
        {
            if (directory.Length - at < CentralHeaderBytes || U32(directory, at) != CentralSignature) throw new ZipPreviewException(NotAnArchive);
            int flags = U16(directory, at + 8);
            int nameLength = U16(directory, at + 28), extraLength = U16(directory, at + 30), commentLength = U16(directory, at + 32);
            int next = at + CentralHeaderBytes + nameLength + extraLength + commentLength;
            if (next > directory.Length) throw new ZipPreviewException(NotAnArchive);

            long packed = U32(directory, at + 20), size = U32(directory, at + 24), offset = U32(directory, at + 42);
            if (packed == uint.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
                ReadZip64Extra(directory.Slice(at + CentralHeaderBytes + nameLength, extraLength), ref size, ref packed, ref offset);

            var name = directory.Slice(at + CentralHeaderBytes, nameLength);
            entries.Add(new ZipEntryInfo(
                ((flags & FlagUtf8) != 0 ? Encoding.UTF8 : legacy).GetString(name),
                packed,
                size,
                DosTime(U16(directory, at + 14), U16(directory, at + 12)),
                U16(directory, at + 10),
                (flags & FlagEncrypted) != 0,
                (uint)U32(directory, at + 16),
                offset));
            at = next;
        }
        return entries;
    }

    /// <summary>Replaces each saturated value with its 64-bit one. The field holds only those, in this order.</summary>
    static void ReadZip64Extra(ReadOnlySpan<byte> extra, ref long size, ref long packed, ref long offset)
    {
        while (extra.Length >= 4)
        {
            int id = U16(extra, 0), length = U16(extra, 2);
            if (length > extra.Length - 4) break;
            if (id == 1)
            {
                var fields = extra.Slice(4, length);
                size = NextZip64Field(ref fields, size);
                packed = NextZip64Field(ref fields, packed);
                offset = NextZip64Field(ref fields, offset);
                return;
            }
            extra = extra[(4 + length)..];
        }
        throw new ZipPreviewException(NotAnArchive);
    }

    static long NextZip64Field(ref ReadOnlySpan<byte> fields, long value)
    {
        if (value != uint.MaxValue) return value;
        if (fields.Length < 8) throw new ZipPreviewException(NotAnArchive);
        long full = I64(fields, 0);
        if (full < 0) throw new ZipPreviewException(NotAnArchive);
        fields = fields[8..];
        return full;
    }

    /// <summary>A date and time as ZIP holds them, in no particular zone; <see cref="DateTime.MinValue"/> if they aren't a real one.</summary>
    static DateTime DosTime(int date, int time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31;
        int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && hour < 24 && minute < 60 && second < 60
            ? new DateTime(year, month, day, hour, minute, second)
            : DateTime.MinValue;
    }

    /// <summary>
    /// The entry's path under the chosen folder, with "\" as a folder separator. Null if its name is rooted, has a
    /// drive or a stream, climbs with "..", or is something Windows would change or take for a device.
    /// </summary>
    static string? RelativePath(string name)
    {
        var path = name.Replace('\\', '/');
        if (path.StartsWith('/')) return null;
        var kept = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            // Windows drops trailing dots and spaces, so ".. " would mean "..".
            if (part.EndsWith('.') || part.EndsWith(' ')) return null;
            if (part.IndexOfAny(InvalidNameChars) >= 0 || IsDeviceName(part)) return null;
            kept.Add(part);
        }
        return kept.Count > 0 ? Path.Combine(kept.ToArray()) : null;
    }

    static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
    }

    static async Task<string> ExtractEntryAsync(ArchiveReader archive, ZipEntryInfo entry, string folder, DownloadItem item, Settings settings, Action<long> written, CancellationToken ct)
    {
        if (RelativePath(entry.Name) is not { } relative) throw new ZipPreviewException(UnsafePath);
        var root = Path.GetFullPath(folder);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        // RelativePath already keeps it inside; this is what must never be wrong, so it is checked as well.
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ZipPreviewException(UnsafePath);

        if (entry.IsFolder)
        {
            Directory.CreateDirectory(target);
            return target;
        }
        if (entry.Encrypted) throw new ZipPreviewException(EntryEncrypted);
        if (entry.Method is not (MethodStored or MethodDeflate))
            throw new ZipPreviewException($"It is packed with a method ADM can't unpack (method {entry.Method}).");

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        PartFile.EnsureSpace(directory, entry.Size, PartFile.FreeSpaceIn(directory));
        long dataAt = entry.Packed > 0 ? await FindDataAsync(archive, entry, ct) : 0;

        // A part file already here may be a running download's, so this one gets a name of its own.
        var part = FileNames.Unique(target + ".admpart");
        var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
        try
        {
            await using (file)
            {
                await archive.TryAsync(limit => UnpackAsync(archive, entry, dataAt, file, written, limit, ct), ct);
            }
            // Before it has its name, so nothing can find it unmarked. The stream moves with the file.
            if (settings.MarkDownloads) ZoneMark.Write(part, item.Url, item.Referrer);
            // After the mark, whose writing counts as a change to the file.
            if (entry.Modified != DateTime.MinValue) File.SetLastWriteTime(part, entry.Modified);
            var final = FileNames.Unique(target);
            File.Move(part, final);
            return final;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>Where the entry's data starts. The local header's own lengths count: its extra field needn't be the directory's.</summary>
    static async Task<long> FindDataAsync(ArchiveReader archive, ZipEntryInfo entry, CancellationToken ct)
    {
        if (entry.HeaderOffset > archive.Length - LocalHeaderBytes) throw new ZipPreviewException(NotAnArchive);
        var header = await archive.ReadAsync(entry.HeaderOffset, LocalHeaderBytes, ct);
        if (U32(header, 0) != LocalSignature) throw new ZipPreviewException(NotAnArchive);
        long dataAt = entry.HeaderOffset + LocalHeaderBytes + U16(header, 26) + U16(header, 28);
        if (entry.Packed > archive.Length - dataAt) throw new ZipPreviewException(NotAnArchive);
        return dataAt;
    }

    /// <summary>One try at the whole entry, from its first byte: a Deflate stream can't carry on from the middle.</summary>
    static async Task<bool> UnpackAsync(ArchiveReader archive, ZipEntryInfo entry, long dataAt, FileStream file, Action<long> progress, CancellationTokenSource limit, CancellationToken ct)
    {
        file.SetLength(0);
        file.Position = 0;
        uint crc = Crc32.Initial;
        long written = 0;
        if (entry.Packed > 0)
        {
            using var body = await archive.OpenAsync(dataAt, entry.Packed, limit, ct);
            var packed = new PackedStream(body.Stream, entry.Packed, read => archive.TookAsync(read, limit));
            await using Stream data = entry.Method == MethodDeflate ? new DeflateStream(packed, CompressionMode.Decompress) : packed;
            var buffer = new byte[BufferSize];
            try
            {
                while (true)
                {
                    limit.CancelAfter(archive.ReadTimeout);
                    int read = await data.ReadAsync(buffer, limit.Token);
                    if (read == 0) break;
                    // Checked before writing, so what an archive declares is the most it can put on the disk.
                    if (read > entry.Size - written) throw new ZipPreviewException(LargerThanDeclared);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    crc = Crc32.Update(crc, buffer.AsSpan(0, read));
                    written += read;
                    progress(written);
                }
            }
            catch (InvalidDataException)
            {
                throw new ZipPreviewException(DamagedData);
            }
        }
        if (written != entry.Size) throw new ZipPreviewException(SizeMismatch);
        if (Crc32.Final(crc) != entry.Crc32) throw new ZipPreviewException(CrcMismatch);
        return true;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static int U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    static long U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    static long I64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);

    /// <summary>An archive's bytes, with the deadlines and retries of each read.</summary>
    sealed class ArchiveReader : IDisposable
    {
        readonly ISegmentSource _source;
        readonly RetryPolicy _retry;
        readonly ReadLimits? _limits;
        byte[] _tail = Array.Empty<byte>();

        ArchiveReader(ISegmentSource source, RetryPolicy retry, ReadLimits? limits, long length)
        {
            _source = source;
            _retry = retry;
            _limits = limits;
            Length = length;
        }

        public long Length { get; }

        public TimeSpan ReadTimeout => _retry.ReadTimeout;

        public static async Task<ArchiveReader> OpenAsync(DownloadItem item, Settings settings, RetryPolicy retry, ReadLimits? limits, CancellationToken ct)
        {
            try
            {
                if (ReadsFromDisk(item)) return await AskAsync(new LocalFileSource(item.FullPath), retry, null, ct);
                return await AskRemoteAsync(item, settings, retry, limits, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ZipPreviewException(NoAnswer);
            }
        }

        /// <summary>As a download's first request does, this tries the browser's proxy once when its own route fails; if that fails too, the first error stands.</summary>
        static async Task<ArchiveReader> AskRemoteAsync(DownloadItem item, Settings settings, RetryPolicy retry, ReadLimits? limits, CancellationToken ct)
        {
            var asking = RangedCopy(item);
            try
            {
                return await AskAsync(SegmentSources.For(asking, settings, Net.ClientFor(settings), settings.Proxy), retry, limits, ct);
            }
            catch (Exception e) when (BrowserProxy.RouteAfter(e, item, settings, settings.Proxy, ct) is { } route)
            {
                try
                {
                    return await AskAsync(SegmentSources.For(asking, settings, Net.ClientFor(route), route), retry, limits, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    ExceptionDispatchInfo.Throw(e);
                    throw;
                }
            }
        }

        /// <summary>What the source needs to ask as the download does, for ranges whatever the download has seen so far. The download itself stays as it is.</summary>
        static DownloadItem RangedCopy(DownloadItem item) => new()
        {
            Url = item.Url,
            Referrer = item.Referrer,
            Cookies = item.Cookies,
            UserAgent = item.UserAgent,
            UserName = item.UserName,
            ProtectedPassword = item.ProtectedPassword,
            ResumeSupported = true,
        };

        static async Task<ArchiveReader> AskAsync(ISegmentSource source, RetryPolicy retry, ReadLimits? limits, CancellationToken ct)
        {
            try
            {
                var probe = await retry.WithRetryAsync(source, async () =>
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(retry.ReadTimeout);
                    return await source.ProbeAsync(limit.Token);
                }, ct);
                if (!probe.RangeSupported) throw new ZipPreviewException(NoRanges);
                return new ArchiveReader(source, retry, limits, probe.Total);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <summary>Runs <paramref name="action"/> with its retries, each with a deadline of its own to keep extending.</summary>
        public async Task<T> TryAsync<T>(Func<CancellationTokenSource, Task<T>> action, CancellationToken ct)
        {
            try
            {
                return await _retry.WithRetryAsync(_source, async () =>
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    return await action(limit);
                }, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ZipPreviewException(NoAnswer);
            }
        }

        /// <summary>
        /// The bytes from <paramref name="from"/> on. Read <paramref name="count"/> of them, then dispose it: an FTP
        /// transfer has no end to ask for, and goes on until it is closed.
        /// </summary>
        public async Task<SegmentBody> OpenAsync(long from, long count, CancellationTokenSource limit, CancellationToken ct)
        {
            await Task.Delay(_source.CloseSettleTime, ct);
            limit.CancelAfter(_retry.ReadTimeout);
            try
            {
                return await _source.OpenRangeAsync(from, from + count - 1, limit.Token);
            }
            catch (RangesRefusedException)
            {
                throw new ZipPreviewException(NoRanges);
            }
        }

        /// <summary>Counts bytes that arrived, and holds to the speed limits. Waiting here isn't the server going silent, so the deadline starts again after it.</summary>
        public async ValueTask TookAsync(int bytes, CancellationTokenSource deadline)
        {
            if (_limits == null) return;
            _limits.Counted(bytes);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            await _limits.Overall.WaitAsync(bytes, deadline.Token);
            await _limits.Download.WaitAsync(bytes, deadline.Token);
            deadline.CancelAfter(_retry.ReadTimeout);
        }

        /// <summary>The file's last bytes, kept: later reads inside them need no request.</summary>
        public async Task<byte[]> ReadTailAsync(int most, CancellationToken ct)
        {
            int count = (int)Math.Min(Length, most);
            _tail = await ReadAsync(Length - count, count, ct);
            return _tail;
        }

        public async Task<byte[]> ReadAsync(long from, int count, CancellationToken ct)
        {
            if (count == 0) return Array.Empty<byte>();
            long inTail = from - (Length - _tail.Length);
            if (inTail >= 0 && inTail + count <= _tail.Length) return _tail.AsSpan((int)inTail, count).ToArray();
            return await TryAsync(async limit =>
            {
                using var body = await OpenAsync(from, count, limit, ct);
                var bytes = new byte[count];
                int got = 0;
                while (got < count)
                {
                    limit.CancelAfter(_retry.ReadTimeout);
                    int read = await body.Stream.ReadAsync(bytes.AsMemory(got), limit.Token);
                    if (read == 0) throw new IOException("The connection ended before all of the data arrived.");
                    got += read;
                    await TookAsync(read, limit);
                }
                return bytes;
            }, ct);
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>A finished download's file, as a source of byte ranges.</summary>
    sealed class LocalFileSource(string path) : ISegmentSource
    {
        public string Address => path;

        public Task<ProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ProbeResult(new FileInfo(path).Length, true, Path.GetFileName(path), null, null, null));

        public Task<(ProbeResult Probe, SegmentBody First)> OpenAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<SegmentBody> OpenRangeAsync(long from, long to, CancellationToken ct)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous) { Position = from };
            return Task.FromResult(new SegmentBody(stream, stream));
        }

        public TimeSpan CloseSettleTime => TimeSpan.Zero;
        public bool IsRefusal(Exception e) => false;
        public bool IsConnectionFailure(Exception e, CancellationToken ct) => false;
        public bool IsTransient(Exception e, CancellationToken ct) => false;
        public void Dispose() { }
    }

    /// <summary>An entry's packed bytes: never more than the directory declares, and an error if they stop short.</summary>
    sealed class PackedStream(Stream inner, long length, Func<int, ValueTask> took) : Stream
    {
        long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left == 0 || buffer.Length == 0) return 0;
            int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
            if (read == 0) throw new IOException("The connection ended before all of the data arrived.");
            _left -= read;
            await took(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
