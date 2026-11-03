namespace TurboDL.Core.Engine;

/// <summary>
/// Shared by every download, so TurboDL stays polite to each host however many links it has from it. New downloads from
/// the same host send their first request at least <see cref="StartInterval"/> apart, instead of all at once. When a host
/// answers "too many requests" (429, or 503), every request to it waits: as long as its Retry-After says (up to
/// <see cref="MaxBackoff"/>), or else <see cref="FirstBackoff"/>, doubling each time it happens again, until a request
/// gets through. Thread-safe.
/// </summary>
public sealed class HostThrottle(TimeProvider? time = null)
{
    /// <summary>The least time between two downloads starting on the same host.</summary>
    public TimeSpan StartInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The first wait after a 429 without Retry-After; it doubles with each one after.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest any host is waited for, even when its Retry-After asks for more.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class HostState
    {
        public DateTimeOffset NextStart;
        public DateTimeOffset PausedUntil;
        public int Strikes;
    }

    /// <summary>Waits until a new download may send its first request to this host: its turn, and not while the host is backing off.</summary>
    public Task WaitToStartAsync(string url, CancellationToken ct) => WaitAsync(url, start: true, ct);

    /// <summary>Waits while the host is backing off. For the requests a running download makes.</summary>
    public Task WaitIfBackingOffAsync(string url, CancellationToken ct) => WaitAsync(url, start: false, ct);

    /// <summary>
    /// The host said "too many requests": every request to it now waits. Returns how long. 429s that arrive while the host
    /// is already being waited for are the same burst, so they don't make the wait longer.
    /// </summary>
    public TimeSpan Backoff(string url, TimeSpan? retryAfter)
    {
        if (HostOf(url) is not { } host) return TimeSpan.Zero;
        lock (_lock)
        {
            var state = Get(host);
            var now = _time.GetUtcNow();
            if (state.PausedUntil > now) return state.PausedUntil - now;

            state.Strikes++;
            var grown = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Min(state.Strikes - 1, 20)));
            var wait = retryAfter is { Ticks: > 0 } asked ? (asked < MaxBackoff ? asked : MaxBackoff) : grown;
            state.PausedUntil = now + wait;
            return wait;
        }
    }

    /// <summary>A request to the host was answered: the next backoff starts small again.</summary>
    public void Succeeded(string url)
    {
        if (HostOf(url) is not { } host) return;
        lock (_lock)
        {
            if (_hosts.TryGetValue(host, out var state)) state.Strikes = 0;
        }
    }

    private async Task WaitAsync(string url, bool start, CancellationToken ct)
    {
        if (HostOf(url) is not { } host) return;
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var state = Get(host);
                var now = _time.GetUtcNow();
                var slot = state.PausedUntil > now ? state.PausedUntil : now;
                if (start)
                {
                    if (state.NextStart > slot) slot = state.NextStart;
                    state.NextStart = slot + StartInterval; // taken now, so the downloads waiting go one after another
                }
                wait = slot - now;
            }
            if (wait <= TimeSpan.Zero) return;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);

            lock (_lock)
            {
                // A 429 came in while this one waited: wait again (and, for a start, take a new turn after it).
                if (Get(host).PausedUntil <= _time.GetUtcNow()) return;
            }
        }
    }

    private HostState Get(string host)
    {
        if (!_hosts.TryGetValue(host, out var state)) _hosts[host] = state = new HostState();
        return state;
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;
}
