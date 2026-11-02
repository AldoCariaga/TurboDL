using TurboDL.Core.Catching;
using TurboDL.Core.Model;
using TurboDL.Core.Queue;

namespace TurboDL.Core.Tests;

public class QueuePlannerTests
{
    private static QueueEntry Q(long id) => new(id, DownloadStatus.Queued, false);
    private static QueueEntry Running(long id) => new(id, DownloadStatus.Downloading, true);

    [Fact]
    public void Starts_oldest_queued_up_to_the_limit() =>
        Assert.Equal([2, 3], QueuePlanner.ToStart([Q(5), Q(2), Q(3), Running(1)], maxConcurrent: 3));

    [Fact]
    public void Manual_downloads_use_up_slots() =>
        Assert.Empty(QueuePlanner.ToStart([Running(1), Running(2), Q(3)], maxConcurrent: 2));

    [Fact]
    public void Ignores_paused_failed_and_completed() =>
        Assert.Equal([4], QueuePlanner.ToStart(
        [
            new(1, DownloadStatus.Paused, false),
            new(2, DownloadStatus.Failed, false),
            new(3, DownloadStatus.Completed, false),
            Q(4),
        ], maxConcurrent: 5));

    [Fact]
    public void Finished_only_when_nothing_waits_and_its_own_downloads_are_done()
    {
        var started = new HashSet<long> { 1 };
        Assert.False(QueuePlanner.IsFinished([Running(1)], started));
        Assert.False(QueuePlanner.IsFinished([Q(2)], started));
        Assert.True(QueuePlanner.IsFinished([new(1, DownloadStatus.Completed, false), Running(9)], started));
    }
}

public class ScheduleClockTests
{
    private static readonly TimeOnly TwoAm = new(2, 0);
    private static DateTime At(int day, int h, int m) => new(2026, 11, day, h, m, 0); // Nov 2 2026 is a Monday

    [Fact]
    public void Fires_once_when_the_time_passes()
    {
        Assert.True(ScheduleClock.Crossed(At(2, 1, 59), At(2, 2, 0), TwoAm, null));
        Assert.False(ScheduleClock.Crossed(At(2, 2, 0), At(2, 2, 1), TwoAm, null));
    }

    [Fact]
    public void Catches_a_time_missed_while_asleep() =>
        Assert.True(ScheduleClock.Crossed(At(1, 23, 0), At(2, 6, 0), TwoAm, null));

    [Fact]
    public void Does_not_fire_before_the_time() =>
        Assert.False(ScheduleClock.Crossed(At(2, 0, 30), At(2, 1, 30), TwoAm, null));

    [Fact]
    public void Respects_days_of_week()
    {
        Assert.False(ScheduleClock.Crossed(At(2, 1, 59), At(2, 2, 1), TwoAm, [DayOfWeek.Saturday]));
        Assert.True(ScheduleClock.Crossed(At(2, 1, 59), At(2, 2, 1), TwoAm, [DayOfWeek.Monday]));
    }

    [Fact]
    public void Clock_going_backwards_never_fires() =>
        Assert.False(ScheduleClock.Crossed(At(2, 3, 0), At(2, 1, 0), TwoAm, null));
}

public class LinkMatcherTests
{
    private static readonly string[] Types = ["zip", "msi", "mp4"];

    [Theory]
    [InlineData("https://example.com/files/setup.msi")]
    [InlineData("  http://example.com/a/b/archive.ZIP?token=1  ")]
    [InlineData("https://example.com/videos/my%20clip.mp4")]
    public void Accepts_single_download_links(string text) => Assert.NotNull(LinkMatcher.Match(text, Types));

    [Theory]
    [InlineData("https://example.com/page.html")]
    [InlineData("https://example.com/")]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("see https://example.com/a.zip")]
    [InlineData("https://example.com/a.zip\nhttps://example.com/b.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_everything_else(string? text) => Assert.Null(LinkMatcher.Match(text, Types));
}
