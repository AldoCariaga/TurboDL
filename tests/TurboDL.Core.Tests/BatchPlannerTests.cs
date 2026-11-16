using TurboDL.Core.Batches;
using TurboDL.Core.Model;

namespace TurboDL.Core.Tests;

public sealed class BatchPlannerTests
{
    private static BatchInfo Batch(bool autoExtract = true) => new() { Name = "data", Directory = @"C:\dl\data", AutoExtract = autoExtract };

    private static BatchItemState Done(long id, string name, string? requested = null) =>
        new(id, requested ?? name, name, DownloadStatus.Completed, false, false, null);

    private static List<BatchItemState> RarSet(string baseName, int count, params int[] skip) =>
        [.. Enumerable.Range(1, count).Where(i => !skip.Contains(i)).Select(i => Done(i, $"{baseName}.part{i:00}.rar"))];

    private static BatchItemState Failed(long id, string name, bool transient, string error = "Connection reset.") =>
        new(id, name, name, DownloadStatus.Failed, false, transient, error);

    [Fact]
    public void AllComplete_FullRarSet_Extracts()
    {
        var steps = BatchPlanner.Decide(Batch(), RarSet("data", 3));

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(steps));
        var set = Assert.Single(extract.Sets);
        Assert.Equal("data.part01.rar", set.FirstVolume);
        Assert.Equal(3, set.Count);
    }

    [Fact]
    public void OneStillDownloading_Waits()
    {
        var items = RarSet("data", 3);
        items[1] = items[1] with { Status = DownloadStatus.Downloading, IsRunning = true };
        Assert.Empty(BatchPlanner.Decide(Batch(), items));
    }

    [Theory]
    [InlineData(DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Queued)]
    public void OnePausedOrQueued_Waits(DownloadStatus status)
    {
        var items = RarSet("data", 3);
        items[2] = items[2] with { Status = status };
        Assert.Empty(BatchPlanner.Decide(Batch(), items));
    }

    [Theory]
    [InlineData(0, 1, 30)]
    [InlineData(1, 2, 120)]
    [InlineData(2, 3, 300)]
    public void TransientFailure_WithRetriesLeft_Retries(int used, int attempt, int delaySeconds)
    {
        var batch = Batch();
        if (used > 0) batch.Retries[2] = used;
        var items = RarSet("data", 3);
        items[1] = Failed(2, "data.part02.rar", transient: true);

        var steps = BatchPlanner.Decide(batch, items);

        Assert.Equal([new BatchStep.Retry(2, attempt, TimeSpan.FromSeconds(delaySeconds))], steps);
        Assert.Equal(used, batch.Retries.GetValueOrDefault(2));
    }

    [Fact]
    public void TransientFailure_WithNoRetriesLeft_NeedsAttention()
    {
        var batch = Batch();
        batch.Retries[2] = 3;
        var items = RarSet("data", 3);
        items[1] = Failed(2, "data.part02.rar", transient: true, "Gave up after 10 retries: Connection reset.");

        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(BatchPlanner.Decide(batch, items)));
        Assert.Equal("data.part02.rar failed. Gave up after 10 retries: Connection reset.", attention.Message);
    }

    [Fact]
    public void NonTransientFailure_NeedsAttention_WithTheError_AndNoRetry()
    {
        var items = RarSet("data", 3);
        items[2] = Failed(3, "data.part03.rar", transient: false, "The host sent a web page instead of the file. Open the link in your browser.");

        var steps = BatchPlanner.Decide(Batch(), items);

        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(steps));
        Assert.Contains("The host sent a web page instead of the file.", attention.Message);
        Assert.DoesNotContain(steps, s => s is BatchStep.Retry);
    }

    [Fact]
    public void SeveralFailures_CountsThem_AndGivesTheFirstError()
    {
        var items = RarSet("data", 4);
        items[1] = Failed(2, "data.part02.rar", transient: false, "Sign-in page.");
        items[3] = Failed(4, "data.part04.rar", transient: false, "Not found.");

        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(BatchPlanner.Decide(Batch(), items)));
        Assert.Equal("2 of 4 parts failed. Sign-in page.", attention.Message);
    }

    [Fact]
    public void TransientFailure_PlusOneRunning_GivesOnlyTheRetry()
    {
        var items = RarSet("data", 3);
        items[0] = Failed(1, "data.part01.rar", transient: true);
        items[2] = items[2] with { Status = DownloadStatus.Downloading, IsRunning = true };

        Assert.Equal([new BatchStep.Retry(1, 1, TimeSpan.FromSeconds(30))], BatchPlanner.Decide(Batch(), items));
    }

    [Fact]
    public void Part17Missing_NeedsAttention()
    {
        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(BatchPlanner.Decide(Batch(), RarSet("data", 50, skip: 17))));
        Assert.Equal("data.part01–50.rar: part 17 is missing.", attention.Message);
    }

    [Fact]
    public void MissingPart_AlreadyInFolder_Extracts()
    {
        // part 3 was deleted from the batch and downloaded separately into the same folder.
        var steps = BatchPlanner.Decide(Batch(), RarSet("data", 10, skip: 3), alreadyInFolder: ["data.part03.rar"]);

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(steps));
        var set = Assert.Single(extract.Sets);
        Assert.True(set.IsComplete);
        Assert.Equal("data.part01.rar", set.FirstVolume);
    }

    [Fact]
    public void OnlySomeMissingParts_InFolder_StillNeedsAttention_ForTheRest()
    {
        var steps = BatchPlanner.Decide(Batch(), RarSet("data", 5, skip: [2, 4]), alreadyInFolder: ["data.part02.rar"]);

        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(steps));
        Assert.Equal("data.part01–05.rar: part 4 is missing.", attention.Message);
    }

    [Fact]
    public void SeveralPartsMissing_ListsThem()
    {
        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(BatchPlanner.Decide(Batch(), RarSet("data", 5, skip: [2, 4]))));
        Assert.Equal("data.part01–05.rar: parts 2, 4 are missing.", attention.Message);
    }

    [Fact]
    public void RenamedVolume_NeedsAttention_AndDoesNotExtract()
    {
        var items = RarSet("x", 3);
        items[1] = Done(2, "x.part02 (1).rar", requested: "x.part02.rar");

        var steps = BatchPlanner.Decide(Batch(), items);

        var attention = Assert.IsType<BatchStep.Attention>(Assert.Single(steps));
        Assert.Equal("x.part02.rar was saved as x.part02 (1).rar because that name was already taken, so the archive can't be extracted. " +
            "Move the old file away and use Retry, or download into an empty folder.", attention.Message);
        Assert.DoesNotContain(steps, s => s is BatchStep.Extract);
    }

    [Fact]
    public void RenameCheck_IgnoresCase_AndNonVolumeNames()
    {
        List<BatchItemState> items =
        [
            Done(1, "X.part01.rar", requested: "x.part01.rar"),
            Done(2, "x.part02.rar"),
            Done(3, "notes (1).txt", requested: "notes.txt"),
        ];
        Assert.IsType<BatchStep.Extract>(Assert.Single(BatchPlanner.Decide(Batch(), items)));
    }

    [Fact]
    public void EmptyRequestedName_SkipsTheRenameCheck()
    {
        var items = RarSet("x", 2);
        items[1] = Done(2, "x.part02.rar", requested: "");
        items.Add(Done(3, "y.part01.rar", requested: ""));

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(BatchPlanner.Decide(Batch(), items)));
        Assert.Equal(2, extract.Sets.Count);
    }

    [Fact]
    public void AutoExtractOff_Finishes()
    {
        var finish = Assert.IsType<BatchStep.Finish>(Assert.Single(BatchPlanner.Decide(Batch(autoExtract: false), RarSet("data", 3))));
        Assert.Equal("All parts downloaded.", finish.Message);
    }

    [Fact]
    public void NoArchives_Finishes()
    {
        var finish = Assert.IsType<BatchStep.Finish>(Assert.Single(BatchPlanner.Decide(Batch(), [Done(1, "a.mkv"), Done(2, "b.mkv")])));
        Assert.Equal("All files downloaded.", finish.Message);
    }

    [Theory]
    [InlineData(BatchStatus.Extracting)]
    [InlineData(BatchStatus.Extracted)]
    [InlineData(BatchStatus.Done)]
    [InlineData(BatchStatus.NeedsAttention)]
    public void NotDownloading_DoesNothing(BatchStatus status)
    {
        var batch = Batch();
        batch.Status = status;
        Assert.Empty(BatchPlanner.Decide(batch, RarSet("data", 3)));
    }

    [Fact]
    public void NoItems_DoesNothing()
    {
        Assert.Empty(BatchPlanner.Decide(Batch(), []));
    }

    [Fact]
    public void TwoCompleteSets_ExtractBoth()
    {
        List<BatchItemState> items = [.. RarSet("a", 2), Done(10, "b.7z.001"), Done(11, "b.7z.002"), Done(12, "readme.txt")];

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(BatchPlanner.Decide(Batch(), items)));
        Assert.Equal(["a.part01.rar", "b.7z.001"], extract.Sets.Select(s => s.FirstVolume));
    }

    [Fact]
    public void SetAlreadyExtracted_IsLeftOut()
    {
        List<BatchItemState> items = [.. RarSet("a", 2), Done(10, "b.7z.001"), Done(11, "b.7z.002")];
        var batch = Batch();
        batch.ExtractedSets.Add("A.PART01.RAR"); // any case

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(BatchPlanner.Decide(batch, items)));
        Assert.Equal(["b.7z.001"], extract.Sets.Select(s => s.FirstVolume));
    }

    [Fact]
    public void EverySetAlreadyExtracted_ExtractsNothingButStillExtracts()
    {
        var batch = Batch();
        batch.ExtractedSets.Add("data.part01.rar");

        var extract = Assert.IsType<BatchStep.Extract>(Assert.Single(BatchPlanner.Decide(batch, RarSet("data", 3))));
        Assert.Empty(extract.Sets);
    }

    private static readonly DateTime Now = new(2026, 11, 8, 14, 5, 0);

    [Fact]
    public void SuggestName_OneSet_IsItsBaseName() =>
        Assert.Equal("Movie", BatchPlanner.SuggestName(["Movie.part1.rar", "Movie.part2.rar", ""], Now));

    [Fact]
    public void SuggestName_TwoSets_FirstByBaseNameWins() =>
        Assert.Equal("alpha", BatchPlanner.SuggestName(["zeta.7z.001", "alpha.part01.rar", "zeta.7z.002"], Now));

    [Fact]
    public void SuggestName_NoArchives_IsTheDate() =>
        Assert.Equal("Batch 2026-11-08 14.05", BatchPlanner.SuggestName(["notes.txt", "", "photo.jpg"], Now));
}
