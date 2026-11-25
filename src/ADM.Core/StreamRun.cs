namespace ADM.Core;

/// <summary>
/// A stream's run as the progress window shows it: the connections fetching its parts, each with what it has
/// brought and what it is doing; and, for a stream with an end, its parts laid end to end, by their bytes where
/// every part's size is known, else one unit each. A recording has connections and no parts. Its own lock guards it.
/// </summary>
internal sealed class StreamRun
{
    /// <summary>A part of the stream: the file it is kept in, its size if known, and whether it is kept already.</summary>
    public readonly record struct Part(string File, long? Size, bool Kept);

    readonly object _gate = new();
    readonly Dictionary<int, Line> _byWorker = new();
    // In the order they first had work, as the list numbers them.
    readonly List<Line> _lines = new();
    readonly Dictionary<string, int> _places = new(StringComparer.OrdinalIgnoreCase);
    readonly long[] _start;
    readonly long[] _length;
    readonly long[] _arrived;
    readonly bool[] _kept;
    readonly bool _bySize;
    readonly long _total;

    public StreamRun(IReadOnlyList<Part> parts)
    {
        _start = new long[parts.Count];
        _length = new long[parts.Count];
        _arrived = new long[parts.Count];
        _kept = new bool[parts.Count];
        _bySize = parts.All(p => p.Size > 0);
        for (int i = 0; i < parts.Count; i++)
        {
            _places[parts[i].File] = i;
            _start[i] = _total;
            _length[i] = _bySize ? parts[i].Size!.Value : 1;
            _kept[i] = parts[i].Kept;
            _total += _length[i];
        }
    }

    /// <summary>A recording's run: its parts keep coming, so only its connections are shown.</summary>
    public static StreamRun Recording() => new([]);

    /// <summary>The line of the connection <paramref name="worker"/>, numbered when it first has work.</summary>
    public Line LineFor(int worker)
    {
        lock (_gate)
        {
            if (!_byWorker.TryGetValue(worker, out var line))
            {
                _byWorker[worker] = line = new Line(this);
                _lines.Add(line);
            }
            return line;
        }
    }

    /// <summary>No connection is at work now: the run, or a recording's batch, is over.</summary>
    public void Rest()
    {
        lock (_gate)
            foreach (var line in _lines) (line.Stage, line.Place) = (ConnectionStage.Idle, -1);
    }

    /// <summary>How many connections are at work.</summary>
    public int AtWork
    {
        get { lock (_gate) return _lines.Count(l => AtWorkOn(l)); }
    }

    static bool AtWorkOn(Line line) => line.Stage is not (ConnectionStage.Idle or ConnectionStage.Complete);

    public IReadOnlyList<ConnectionInfo> Connections()
    {
        lock (_gate) return _lines.Select(l => new ConnectionInfo(l.Downloaded, l.Stage)).ToList();
    }

    /// <summary>
    /// Kept parts side by side as one stretch; a part arriving as its own, marked where it starts, and live while a
    /// connection is at work on it. Null for a recording.
    /// </summary>
    public Strip? StripNow()
    {
        lock (_gate)
        {
            if (_total == 0) return null;
            var live = _lines.Where(l => l.Place >= 0 && AtWorkOn(l)).Select(l => l.Place).ToHashSet();
            var pieces = new List<StripPiece>();
            long keptFrom = -1;
            for (int i = 0; i < _kept.Length; i++)
            {
                if (_kept[i])
                {
                    if (keptFrom < 0) keptFrom = _start[i];
                    continue;
                }
                if (keptFrom >= 0) pieces.Add(KeptStretch(keptFrom, _start[i]));
                keptFrom = -1;
                if (live.Contains(i) || _arrived[i] > 0)
                    pieces.Add(new StripPiece(_start[i], _length[i], _bySize ? Math.Min(_arrived[i], _length[i]) : 0, live.Contains(i), true));
            }
            if (keptFrom >= 0) pieces.Add(KeptStretch(keptFrom, _total));
            return new Strip(_total, pieces);
        }
    }

    static StripPiece KeptStretch(long from, long to) => new(from, to - from, to - from, false, false);

    /// <summary>
    /// One connection of the run. It tells what it is doing and what arrives, and the part it is on takes the bytes;
    /// a file the run doesn't lay out, as a recording's, takes none.
    /// </summary>
    public sealed class Line(StreamRun run)
    {
        // Guarded by the run.
        internal ConnectionStage Stage { get; set; } = ConnectionStage.Connecting;
        internal int Place { get; set; } = -1;
        internal long Downloaded { get; private set; }

        /// <summary>The connection starts on the part kept in <paramref name="file"/>, its own or one it helps with.</summary>
        public void On(string file)
        {
            lock (run._gate) (Place, Stage) = (run._places.TryGetValue(file, out var place) ? place : -1, ConnectionStage.Connecting);
        }

        public void Asking() => Now(ConnectionStage.Asking);
        public void Receiving() => Now(ConnectionStage.Receiving);
        public void Waiting() => Now(ConnectionStage.Waiting);

        /// <summary>Nothing is left for it to start.</summary>
        public void Done()
        {
            lock (run._gate) (Stage, Place) = (ConnectionStage.Complete, -1);
        }

        public void Received(long bytes)
        {
            lock (run._gate)
            {
                Downloaded += bytes;
                if (Place >= 0) run._arrived[Place] += bytes;
            }
        }

        /// <summary>Bytes of its part that won't be kept after all, as when a try fails.</summary>
        public void Lost(long bytes)
        {
            lock (run._gate)
                if (Place >= 0) run._arrived[Place] = Math.Max(0, run._arrived[Place] - bytes);
        }

        /// <summary>Its part is on disk.</summary>
        public void Kept()
        {
            lock (run._gate)
                if (Place >= 0) (run._kept[Place], run._arrived[Place]) = (true, 0);
        }

        void Now(ConnectionStage stage)
        {
            lock (run._gate) Stage = stage;
        }
    }
}
