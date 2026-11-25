using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// A part file that holds writes in memory, as the Windows cache does, until <see cref="Flush"/> puts them
/// on disk. Closing it without a flush loses them. After <see cref="TryPowerLoss"/> the file takes nothing more.
/// </summary>
sealed class LossyPartFile : PartFile
{
    readonly object _gate = new();
    // Held for a whole flush, so a power cut falls either before or after one, never inside it.
    readonly object _flushing = new();
    readonly List<(long Offset, byte[] Data)> _pending = new();
    bool _dead;
    long _flushed;

    public LossyPartFile(string path) : base(path) { }

    /// <summary>Bytes that reached the disk through <see cref="Flush"/>.</summary>
    public long FlushedBytes => Interlocked.Read(ref _flushed);

    /// <summary>Once this many bytes have been written, every further write fails as on a full disk.</summary>
    public long? DiskFullAfter { get; init; }
    public int DiskFullErrors => Volatile.Read(ref _diskFullErrors);
    int _diskFullErrors;
    long _written;

    public override ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_written + data.Length > DiskFullAfter)
            {
                Interlocked.Increment(ref _diskFullErrors);
                // ERROR_DISK_FULL, as Windows reports it.
                return ValueTask.FromException(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
            }
            _written += data.Length;
            if (!_dead) _pending.Add((offset, data.ToArray()));
        }
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        int read = await base.ReadAsync(offset, buffer, ct);
        // Reads see cached writes, as they do through the real cache.
        lock (_gate)
            foreach (var (at, data) in _pending)
            {
                long from = Math.Max(at, offset), to = Math.Min(at + data.Length, offset + read);
                if (from < to) data.AsSpan((int)(from - at), (int)(to - from)).CopyTo(buffer.Span[(int)(from - offset)..]);
            }
        return read;
    }

    public override void SetLength(long length)
    {
        lock (_gate)
            if (_dead) return;
        base.SetLength(length);
    }

    public override void Flush()
    {
        lock (_flushing)
        {
            List<(long Offset, byte[] Data)> writes;
            lock (_gate)
            {
                if (_dead) return;
                writes = _pending.ToList();
                _pending.Clear();
            }
            foreach (var (offset, data) in writes)
                base.WriteAsync(offset, data, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            base.Flush();
            Interlocked.Add(ref _flushed, writes.Sum(w => (long)w.Data.Length));
        }
    }

    /// <summary>
    /// The power goes, provided the cache holds at least <paramref name="minUnflushed"/> bytes for it to lose:
    /// <paramref name="atThatMoment"/> runs with no flush able to start, then the unflushed writes are gone
    /// and nothing more reaches the disk. False, and nothing happens, while too little is unflushed.
    /// </summary>
    public bool TryPowerLoss(long minUnflushed, Action atThatMoment)
    {
        lock (_flushing)
        {
            lock (_gate)
                if (_pending.Sum(w => (long)w.Data.Length) < minUnflushed) return false;
            atThatMoment();
            lock (_gate)
            {
                _dead = true;
                _pending.Clear();
            }
            return true;
        }
    }
}
