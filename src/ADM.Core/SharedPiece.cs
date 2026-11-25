namespace ADM.Core;

/// <summary>
/// A byte range of a file fetched into memory, by its own connection and by free ones. Each connection fills a
/// span of it; a connection with nothing left to start takes the back half of the span with the most left, so
/// the last parts of a download don't leave connections idle while one of them finishes alone.
/// </summary>
internal sealed class SharedPiece
{
    /// <summary>The least either half of a split may have: below it, a new request costs more than it saves.</summary>
    public const int MinSplitBytes = 512 * 1024;

    readonly object _gate = new();
    readonly List<Span> _spans;
    TaskCompletionSource _released = NewSignal();
    bool _abandoned;

    public SharedPiece(long offset, int length)
    {
        Offset = offset;
        Data = new byte[length];
        _spans = [new Span(0, length)];
    }

    /// <summary>Where <see cref="Data"/> starts in the file.</summary>
    public long Offset { get; }

    public byte[] Data { get; }

    /// <summary>A part of the piece that one connection fills. A split moves its end back; guarded by the piece.</summary>
    public sealed class Span(int start, int end)
    {
        internal int Position { get; set; } = start;
        /// <summary>Exclusive.</summary>
        internal int End { get; set; } = end;
        internal bool Taken { get; set; }
        internal int Left => End - Position;
    }

    public bool IsComplete
    {
        get { lock (_gate) return _spans.All(s => s.Left <= 0); }
    }

    /// <summary>The most any one span has left: which piece a free connection helps first.</summary>
    public int MostLeft
    {
        get { lock (_gate) return _spans.Max(s => s.Left); }
    }

    /// <summary>
    /// A span no connection is filling, now the caller's; else the back half of the taken span with the most
    /// left; else null. <paramref name="released"/> then completes when a span is next let go, or at once if
    /// the piece is complete.
    /// </summary>
    public Span? Take(out Task released)
    {
        lock (_gate)
        {
            released = _spans.All(s => s.Left <= 0) ? Task.CompletedTask : _released.Task;
            var open = _spans.FirstOrDefault(s => !s.Taken && s.Left > 0);
            if (open != null)
            {
                open.Taken = true;
                return open;
            }
            var fullest = _spans.Where(s => s.Taken).MaxBy(s => s.Left);
            if (fullest == null || fullest.Left < 2 * MinSplitBytes) return null;
            int at = fullest.Position + fullest.Left / 2;
            var back = new Span(at, fullest.End) { Taken = true };
            fullest.End = at;
            _spans.Add(back);
            return back;
        }
    }

    /// <summary>What <paramref name="span"/> still asks for, as the file's inclusive range.</summary>
    public (long From, long To) RangeOf(Span span)
    {
        lock (_gate) return (Offset + span.Position, Offset + span.End - 1);
    }

    public int LeftOf(Span span)
    {
        lock (_gate) return span.Left;
    }

    /// <summary>
    /// Keeps what arrived for <paramref name="span"/>, up to its end, which a split may have moved meanwhile; the
    /// rest is another span's. Returns the bytes kept, none once the piece is abandoned.
    /// </summary>
    public int Store(Span span, ReadOnlySpan<byte> arrived)
    {
        lock (_gate)
        {
            if (_abandoned) return 0;
            int kept = Math.Min(arrived.Length, Math.Max(0, span.Left));
            arrived[..kept].CopyTo(Data.AsSpan(span.Position));
            span.Position += kept;
            return kept;
        }
    }

    /// <summary>The caller stops filling <paramref name="span"/>; whatever it has left goes to whoever takes it next.</summary>
    public void Release(Span span)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            span.Taken = false;
            signal = _released;
            _released = NewSignal();
        }
        signal.TrySetResult();
    }

    /// <summary>The piece will never be kept: from now on nothing arriving counts. Returns the bytes it had.</summary>
    public long Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            return Data.Length - _spans.Sum(s => (long)Math.Max(0, s.Left));
        }
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
