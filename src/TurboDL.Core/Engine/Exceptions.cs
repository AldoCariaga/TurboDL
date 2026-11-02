namespace TurboDL.Core.Engine;

/// <summary>A download stopped for a reason that retrying won't fix.</summary>
public class DownloadFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The file on the server is not the one we started downloading (size, ETag or Last-Modified changed).</summary>
public sealed class RemoteFileChangedException(string message) : DownloadFailedException(message);

/// <summary>Server answered with a status that is worth retrying (408, 429, 5xx).</summary>
public sealed class TransientHttpException(int statusCode, TimeSpan? retryAfter = null)
    : Exception($"Server returned HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>429/503: the server is saying "too many connections", not "broken".</summary>
    public bool IsBusy => StatusCode is 429 or 503;

    public static TransientHttpException From(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        TimeSpan? after = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return new TransientHttpException((int)response.StatusCode, after is { Ticks: > 0 } ? after : null);
    }
}
