using TurboDL.Core.Archives;
using TurboDL.Core.Model;

namespace TurboDL.Core.Batches;

/// <summary>What the batch needs to know about one of its downloads.</summary>
/// <param name="Id">The download's id.</param>
/// <param name="RequestedName">The name taken from the link when the batch was made ("" when the server picks it).</param>
/// <param name="FileName">The name the file is saved under.</param>
/// <param name="Status">The download's status.</param>
/// <param name="IsRunning">True while the engine is working on it.</param>
/// <param name="ErrorIsTransient">True when a failure is worth retrying (a network error, not a host's web page).</param>
/// <param name="Error">Why it failed, if it did.</param>
public sealed record BatchItemState(long Id, string RequestedName, string FileName, DownloadStatus Status, bool IsRunning,
    bool ErrorIsTransient, string? Error);

/// <summary>Something the caller should do next for a batch.</summary>
public abstract record BatchStep
{
    /// <summary>Retry a failed download after <paramref name="Delay"/>; <paramref name="Attempt"/> is 1-based.</summary>
    public sealed record Retry(long ItemId, int Attempt, TimeSpan Delay) : BatchStep;

    /// <summary>Every part is in; extract these sets.</summary>
    public sealed record Extract(IReadOnlyList<VolumeSet> Sets) : BatchStep;

    /// <summary>Every part is in and there's nothing to extract; the batch is done.</summary>
    public sealed record Finish(string Message) : BatchStep;

    /// <summary>The batch is stuck until the user acts; <paramref name="Message"/> says why.</summary>
    public sealed record Attention(string Message) : BatchStep;
}

/// <summary>Decides what a batch does next from the state of its downloads. Pure: no I/O, no clock, and it changes nothing.</summary>
public static class BatchPlanner
{
    /// <summary>The wait before each automatic retry of a failed part; its length is how many retries a part gets.</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// A new batch's name, which is also its folder's: the base name that sorts first among its archive sets, or
    /// "Batch yyyy-MM-dd HH.mm" when no link names an archive. The add dialog and the service both use it, so they agree.
    /// </summary>
    public static string SuggestName(IEnumerable<string> fileNames, DateTime now) =>
        MultipartArchive.Group(fileNames.Where(n => n.Length > 0)).FirstOrDefault()?.BaseName
        ?? "Batch " + now.ToString("yyyy-MM-dd HH.mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The next steps for a batch in the Downloading state. Empty = wait. Retry steps may come with nothing else;
    /// Extract/Finish/Attention only once no part is still going or due a retry.
    /// <paramref name="alreadyInFolder"/>: volumes no part of the batch downloads that are in its folder anyway (got some
    /// other way, and checked by the caller); they fill gaps in a set, since 7-Zip finds volumes there by name.
    /// </summary>
    public static IReadOnlyList<BatchStep> Decide(BatchInfo batch, IReadOnlyList<BatchItemState> items,
        IReadOnlyCollection<string>? alreadyInFolder = null)
    {
        if (batch.Status != BatchStatus.Downloading || items.Count == 0) return [];

        var retries = new List<BatchStep>();
        foreach (var item in items)
        {
            if (item.Status != DownloadStatus.Failed || item.IsRunning || !item.ErrorIsTransient) continue;
            var used = batch.Retries.GetValueOrDefault(item.Id);
            if (used < RetryDelays.Count) retries.Add(new BatchStep.Retry(item.Id, used + 1, RetryDelays[used]));
        }

        var busy = items.Any(i => i.IsRunning
            || i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused);
        if (retries.Count > 0 || busy) return retries;

        var failed = items.Where(i => i.Status == DownloadStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            var first = failed[0];
            var head = failed.Count == 1 ? $"{first.FileName} failed." : $"{failed.Count} of {items.Count} parts failed.";
            return [new BatchStep.Attention(string.IsNullOrWhiteSpace(first.Error) ? head : $"{head} {first.Error}")];
        }

        var renamed = items.FirstOrDefault(i => i.RequestedName.Length > 0
            && MultipartArchive.TryParse(i.RequestedName, out _, out _, out _)
            && !string.Equals(i.RequestedName, i.FileName, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null)
        {
            return [new BatchStep.Attention($"{renamed.RequestedName} was saved as {renamed.FileName} because that name was already taken, " +
                "so the archive can't be extracted. Move the old file away and use Retry, or download into an empty folder.")];
        }

        var sets = MultipartArchive.Group(items.Select(i => i.FileName).Concat(alreadyInFolder ?? []));
        var incomplete = sets.Where(s => !s.IsComplete).ToList();
        if (incomplete.Count > 0)
        {
            return [new BatchStep.Attention(string.Join(" ", incomplete.Select(s => s.Missing.Count == 1
                ? $"{MultipartArchive.Describe(s)}: part {s.Missing[0]} is missing."
                : $"{MultipartArchive.Describe(s)}: parts {string.Join(", ", s.Missing)} are missing.")))];
        }

        if (batch.AutoExtract && sets.Count > 0) return [new BatchStep.Extract(sets)];
        return [new BatchStep.Finish(sets.Count == 0 ? "All files downloaded." : "All parts downloaded.")];
    }
}
