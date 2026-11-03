using TurboDL.Core.Categories;

namespace TurboDL.Core.Tests;

public class CategoryRulesTests
{
    private readonly CategoryRules _rules = new(@"C:\Downloads\TurboDL");

    [Theory]
    [InlineData("movie.MKV", "Video")]
    [InlineData("song.mp3", "Music")]
    [InlineData("PowerShell-7.4.6-win-x64.msi", "Programs")]
    [InlineData("backup.tar.gz", "Compressed")]
    [InlineData("100MB.bin", "Other")]
    [InlineData("no-extension", "Other")]
    [InlineData("", "Other")]
    public void Picks_category_from_extension(string fileName, string expected) =>
        Assert.Equal(expected, _rules.CategoryFor(fileName));

    [Theory]
    [InlineData("testdata.zip.001", "Compressed")]
    [InlineData("Backup.7Z.003", "Compressed")]
    [InlineData("testdata.part02.rar", "Compressed")]
    [InlineData("movie.001", "Other")]
    [InlineData("movie.mkv.001", "Other")]
    public void Split_volumes_go_by_their_archive_extension(string fileName, string expected) =>
        Assert.Equal(expected, _rules.CategoryFor(fileName));

    [Fact]
    public void Split_volumes_follow_a_custom_rule_for_their_archive_extension()
    {
        var rules = new CategoryRules(@"C:\Downloads\TurboDL", [("Backups", ["zip"]), ("Compressed", ["7z", "rar"])]);

        Assert.Equal("Backups", rules.CategoryFor("testdata.zip.002"));
        Assert.Equal("Compressed", rules.CategoryFor("testdata.7z.001"));
    }

    [Fact]
    public void Other_is_always_last() => Assert.Equal(CategoryRules.Other, _rules.Categories[^1]);

    [Fact]
    public void Folder_is_under_root() =>
        Assert.Equal(@"C:\Downloads\TurboDL\Video", _rules.FolderFor("Video"));
}
