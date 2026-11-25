using System.Diagnostics;

namespace ADM.Core;

/// <summary>Global token bucket shared by every connection; zero means unlimited.</summary>
public sealed class RateLimiter
{
    readonly object _gate = new();
    double _tokens;
    long _lastTicks = Stopwatch.GetTimestamp();

    public long BytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = BytesPerSecond;
        if (rate <= 0) return;

        double deficit;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(rate, _tokens + rate * (now - _lastTicks) / (double)Stopwatch.Frequency);
            _lastTicks = now;
            _tokens -= bytes;
            deficit = -_tokens;
        }
        if (deficit > 0)
            await Task.Delay(TimeSpan.FromSeconds(deficit / rate), ct);
    }
}
