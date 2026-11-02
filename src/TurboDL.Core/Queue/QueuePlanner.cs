using TurboDL.Core.Model;

namespace TurboDL.Core.Queue;

/// <summary>What the planner needs to know about one download.</summary>
public readonly record struct QueueEntry(long Id, DownloadStatus Status, bool IsRunning);

/// <summary>
/// The queue's rules, kept free of timers and UI so they can be tested:
/// at most N downloads at once (manual ones count too), oldest queued first.
/// </summary>
public static class QueuePlanner
{
    /// <summary>The queued downloads to start now so that no more than <paramref name="maxConcurrent"/> run.</summary>
    public static IReadOnlyList<long> ToStart(IEnumerable<QueueEntry> entries, int maxConcurrent)
    {
        var list = entries as IReadOnlyCollection<QueueEntry> ?? entries.ToList();
        var free = Math.Max(1, maxConcurrent) - list.Count(e => e.IsRunning);
        if (free <= 0) return [];
        return list.Where(e => !e.IsRunning && e.Status == DownloadStatus.Queued)
            .OrderBy(e => e.Id)
            .Take(free)
            .Select(e => e.Id)
            .ToList();
    }

    /// <summary>The queue is done when nothing is waiting and nothing it started is still running.</summary>
    public static bool IsFinished(IEnumerable<QueueEntry> entries, IReadOnlySet<long> startedByQueue) =>
        !entries.Any(e => (e.Status == DownloadStatus.Queued && !e.IsRunning) || (e.IsRunning && startedByQueue.Contains(e.Id)));
}
