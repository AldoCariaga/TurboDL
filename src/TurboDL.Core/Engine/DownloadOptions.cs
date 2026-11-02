using TurboDL.Core.Model;

namespace TurboDL.Core.Engine;

public sealed record DownloadOptions
{
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>Smallest segment worth its own connection. A running segment is split only if at least twice this is left.</summary>
    public long MinSegmentSize { get; init; } = 1024 * 1024;

    /// <summary>Failed attempts in a row per segment before the whole download fails. The count resets once bytes arrive again.</summary>
    public int MaxRetries { get; init; } = 10;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No bytes for this long on a connection = stalled; reconnect.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A part whose connection has gone silent this long is taken over by an idle connection (if there is one).
    /// Reads return as soon as any bytes arrive, so silence this long on a live transfer is a real stall
    /// (typically TCP loss recovery when many connections share a saturated line).
    /// </summary>
    public TimeSpan StealAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Extra time a freshly claimed part gets for connect + TLS + redirects + first byte before it can be taken over.</summary>
    public TimeSpan ConnectGrace { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record DownloadSnapshot(
    long Downloaded,
    long? Total,
    double BytesPerSecond,
    int ActiveConnections,
    IReadOnlyList<SegmentState> Segments)
{
    public double? Fraction => Total is > 0 ? (double)Downloaded / Total.Value : null;

    public TimeSpan? Remaining =>
        Total is long t && BytesPerSecond > 1 ? TimeSpan.FromSeconds((t - Downloaded) / BytesPerSecond) : null;
}
