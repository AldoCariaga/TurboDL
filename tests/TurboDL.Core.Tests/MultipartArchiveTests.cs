using TurboDL.Core.Archives;

namespace TurboDL.Core.Tests;

public sealed class MultipartArchiveTests
{
    [Theory]
    [InlineData(new[] { "game.part01.rar", "game.part02.rar", "game.part04.rar" }, 3, "game.part03.rar")]
    [InlineData(new[] { "game.part1.rar", "game.part2.rar", "game.part10.rar" }, 3, "game.part3.rar")]
    [InlineData(new[] { "Game.Part001.RAR", "Game.Part003.RAR" }, 2, "Game.Part002.RAR")]
    [InlineData(new[] { "data.7z.001", "data.7z.003" }, 2, "data.7z.002")]
    [InlineData(new[] { "data.zip.0001", "data.zip.0003" }, 2, "data.zip.0002")]
    public void VolumeName_FollowsTheSetsOwnNaming(string[] present, int number, string expected)
    {
        var set = Assert.Single(MultipartArchive.Group(present));

        Assert.Equal(expected, MultipartArchive.VolumeName(set, number));
    }

    private static IEnumerable<string> Parts(string baseName, int count, params int[] skip) =>
        Enumerable.Range(1, count).Where(i => !skip.Contains(i)).Select(i => $"{baseName}.part{i:00}.rar");

    [Theory]
    [InlineData("data.part1.rar")]
    [InlineData("data.part01.rar")]
    [InlineData("data.part001.rar")]
    public void RarPartPadding_AllParseToOne(string name)
    {
        Assert.True(MultipartArchive.TryParse(name, out var baseName, out var scheme, out var number));
        Assert.Equal("data", baseName);
        Assert.Equal(VolumeScheme.RarParts, scheme);
        Assert.Equal(1, number);
    }

    [Fact]
    public void FiftyParts_AreOneCompleteSet()
    {
        var set = Assert.Single(MultipartArchive.Group(Parts("data", 50).Reverse()));

        Assert.Equal("data", set.BaseName);
        Assert.Equal(VolumeScheme.RarParts, set.Scheme);
        Assert.Equal(50, set.Count);
        Assert.True(set.IsComplete);
        Assert.Equal("data.part01.rar", set.FirstVolume);
        Assert.Equal(Parts("data", 50), set.Files);
    }

    [Fact]
    public void Part17Missing_IsReported()
    {
        var set = Assert.Single(MultipartArchive.Group(Parts("data", 50, skip: 17)));

        Assert.Equal([17], set.Missing);
        Assert.False(set.IsComplete);
        Assert.Equal(50, set.Count);
    }

    [Fact]
    public void Volume1Missing_HasNoFirstVolume()
    {
        var set = Assert.Single(MultipartArchive.Group(Parts("data", 3, skip: 1)));

        Assert.Null(set.FirstVolume);
        Assert.Contains(1, set.Missing);
    }

    [Fact]
    public void TwoSetsInOneList_AreTwoSets()
    {
        var sets = MultipartArchive.Group(["b.7z.002", "a.part1.rar", "b.7z.001", "a.part2.rar"]);

        Assert.Equal(2, sets.Count);
        Assert.Equal(("a", VolumeScheme.RarParts), (sets[0].BaseName, sets[0].Scheme));
        Assert.Equal(["a.part1.rar", "a.part2.rar"], sets[0].Files);
        Assert.Equal(("b", VolumeScheme.Split), (sets[1].BaseName, sets[1].Scheme));
        Assert.Equal(["b.7z.001", "b.7z.002"], sets[1].Files);
        Assert.All(sets, s => Assert.True(s.IsComplete));
    }

    [Fact]
    public void MixedCase_IsOneSet()
    {
        var set = Assert.Single(MultipartArchive.Group(["DATA.PART01.RAR", "data.part02.rar"]));

        Assert.Equal(2, set.Count);
        Assert.True(set.IsComplete);
        Assert.Equal("DATA.PART01.RAR", set.FirstVolume);
    }

    [Fact]
    public void SameNameTwice_CountsOnce()
    {
        var set = Assert.Single(MultipartArchive.Group(["data.part01.rar", "data.part02.rar", "data.part01.rar", @"C:\x\data.part02.rar"]));
        Assert.Equal(["data.part01.rar", "data.part02.rar"], set.Files);
    }

    [Fact]
    public void NonArchives_AreIgnored()
    {
        Assert.Empty(MultipartArchive.Group(["readme.txt", "movie.mkv", "data.r00", "data.r01"]));
        Assert.False(MultipartArchive.TryParse("data.r00", out _, out _, out _));
    }

    [Fact]
    public void SingleZip_IsSingle_AndComplete()
    {
        var set = Assert.Single(MultipartArchive.Group(["single.zip"]));

        Assert.Equal("single", set.BaseName);
        Assert.Equal(VolumeScheme.Single, set.Scheme);
        Assert.Equal(1, set.Count);
        Assert.True(set.IsComplete);
        Assert.Equal("single.zip", set.FirstVolume);
    }

    [Fact]
    public void SplitZip_IsOneSplitSet()
    {
        var set = Assert.Single(MultipartArchive.Group(["testdata.zip.003", "testdata.zip.001", "testdata.zip.002"]));

        Assert.Equal("testdata", set.BaseName);
        Assert.Equal(VolumeScheme.Split, set.Scheme);
        Assert.Equal(3, set.Count);
        Assert.True(set.IsComplete);
        Assert.Equal("testdata.zip.001", set.FirstVolume);
    }

    [Fact]
    public void Split7zAndSplitZip_WithTheSameBase_AreSeparate()
    {
        var sets = MultipartArchive.Group(["a.7z.001", "a.zip.001"]);

        Assert.Equal(2, sets.Count);
        Assert.All(sets, s => Assert.Equal("a", s.BaseName));
        Assert.Equal(["a.7z.001", "a.zip.001"], sets.Select(s => s.FirstVolume).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Describe_EachScheme()
    {
        Assert.Equal("data.part01–50.rar", MultipartArchive.Describe(MultipartArchive.Group(Parts("data", 50)).Single()));
        Assert.Equal("data.7z.001–003", MultipartArchive.Describe(MultipartArchive.Group(["data.7z.001", "data.7z.002", "data.7z.003"]).Single()));
        Assert.Equal("testdata.zip.001–003", MultipartArchive.Describe(MultipartArchive.Group(["testdata.zip.001", "testdata.zip.002", "testdata.zip.003"]).Single()));
        Assert.Equal("data.rar", MultipartArchive.Describe(MultipartArchive.Group(["data.rar"]).Single()));
        Assert.Equal("data.part1.rar", MultipartArchive.Describe(MultipartArchive.Group(["data.part1.rar"]).Single()));
    }
}
