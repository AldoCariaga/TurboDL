using System.Diagnostics;

namespace TurboDL.Core.Engine;

/// <summary>
/// Token bucket shared by any number of connections. Readers take bytes first and then sleep off
/// any debt, so the combined rate settles on <see cref="BytesPerSecond"/> however many readers there are.
/// </summary>
public sealed class RateLimiter(long bytesPerSecond = 0)
{
    private const double BurstSeconds = 0.25;

    private readonly Lock _gate = new();
    private long _bytesPerSecond = Math.Max(0, bytesPerSecond);
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();

    /// <summary>0 = unlimited. Can be changed while downloads are running.</summary>
    public long BytesPerSecond
    {
        get => Volatile.Read(ref _bytesPerSecond);
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _tokens = 0;
                _last = Stopwatch.GetTimestamp();
            }
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var bps = _bytesPerSecond;
            if (bps <= 0) return;

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            _tokens = Math.Min(bps * BurstSeconds, _tokens + elapsed * bps) - bytes;
            if (_tokens >= 0) return;
            delay = TimeSpan.FromSeconds(-_tokens / bps);
        }

        await Task.Delay(delay, ct);
    }
}
