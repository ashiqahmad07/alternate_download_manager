namespace ADM.Core.Tests;

/// <summary>
/// Stands in for the app's UI thread: the manager's callbacks run on the thread pool, but one at a time,
/// under <see cref="Gate"/>. A test that calls the manager while transfers run takes the same lock, so
/// the two never touch the manager at once.
/// </summary>
sealed class LockedContext : SynchronizationContext
{
    public object Gate { get; } = new();

    public override void Post(SendOrPostCallback d, object? state) =>
        ThreadPool.QueueUserWorkItem(_ => { lock (Gate) d(state); });

    public void Run(Action action)
    {
        lock (Gate) action();
    }
}
