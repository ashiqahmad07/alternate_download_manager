namespace ADM.Core;

/// <summary>
/// Puts the state's file on disk off the UI thread. The state is turned into bytes where it is safe to read, and
/// handed over here: one write runs at a time, and a save still waiting when a newer one comes is dropped. A save
/// that waits for the disk, as at exit, is never overtaken by an older one landing after it.
/// </summary>
public sealed class StateWriter
{
    readonly Action<byte[]> _writeFile;
    // Held while the file is written, and while a save is taken to be written, so the saves land in the order
    // they were made. Taken before _gate.
    readonly object _disk = new();
    readonly object _gate = new();
    byte[]? _waiting;
    bool _draining;
    Task _drain = Task.CompletedTask;
    int _failed;

    public StateWriter(string path) : this(bytes => Store.WriteFile(path, stream => stream.Write(bytes))) { }

    /// <param name="writeFile">Puts the bytes on disk; tests hold it up or make it fail.</param>
    internal StateWriter(Action<byte[]> writeFile) => _writeFile = writeFile;

    /// <summary>Hands the bytes to the background writer and returns at once.</summary>
    public void Post(byte[] bytes)
    {
        lock (_gate)
        {
            _waiting = bytes;
            if (_draining) return;
            _draining = true;
            _drain = Task.Run(Drain);
        }
    }

    /// <summary>Writes the bytes before returning, after a write already under way; throws if they can't be written.</summary>
    public void Write(byte[] bytes)
    {
        // What waited is older than these bytes. A save the writer has already taken is written before them, as
        // it holds _disk from taking it to writing it.
        lock (_gate) _waiting = null;
        lock (_disk) _writeFile(bytes);
    }

    /// <summary>Completes when the background writer has nothing left to write.</summary>
    public Task Idle
    {
        get { lock (_gate) return _drain; }
    }

    /// <summary>True once after a background write failed, so the owner can save again later.</summary>
    public bool TakeFailure() => Interlocked.Exchange(ref _failed, 0) == 1;

    void Drain()
    {
        while (true)
        {
            lock (_disk)
            {
                byte[] bytes;
                lock (_gate)
                {
                    if (_waiting == null)
                    {
                        _draining = false;
                        return;
                    }
                    bytes = _waiting;
                    _waiting = null;
                }
                try
                {
                    _writeFile(bytes);
                }
                catch (Exception e)
                {
                    Log.Error("The state could not be saved.", e);
                    Interlocked.Exchange(ref _failed, 1);
                }
            }
        }
    }
}
