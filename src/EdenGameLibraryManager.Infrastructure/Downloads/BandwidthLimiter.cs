using System.Diagnostics;

namespace EdenGameLibraryManager.Infrastructure.Downloads;

/// <summary>Token-bucket limiter shared by all transfers so the speed limit is global.</summary>
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesPerSecond;
    private double _available;
    private double _lastSeconds;

    /// <summary>0 disables limiting.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            lock (_lock)
            {
                _bytesPerSecond = Math.Max(0, value);
                _available = 0;
                _lastSeconds = _clock.Elapsed.TotalSeconds;
            }
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var rate = _bytesPerSecond;
                if (rate <= 0) return;

                var now = _clock.Elapsed.TotalSeconds;
                _available = Math.Min(rate, _available + (now - _lastSeconds) * rate);
                _lastSeconds = now;

                // A chunk larger than one second's budget is allowed once the bucket is full; the
                // resulting debt is repaid by later waits.
                if (_available >= bytes || _available >= rate)
                {
                    _available -= bytes;
                    return;
                }
                delay = TimeSpan.FromSeconds(Math.Clamp((Math.Min(bytes, rate) - _available) / rate, 0.01, 1.0));
            }
            await Task.Delay(delay, cancellationToken);
        }
    }
}
