using TurboDL.Core.Categories;
using TurboDL.Core.Settings;

namespace TurboDL.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("turbodl-settings-").FullName;
    private string PathIn => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(PathIn).Load();
        Assert.Equal(3, s.MaxConcurrent);
        Assert.Contains(s.Categories, c => c.Name == "Video");
    }

    [Fact]
    public void Round_trips_and_clamps()
    {
        var store = new SettingsStore(PathIn);
        var s = new AppSettings { MaxConcurrent = 99, SpeedLimitKBps = 512, Theme = ThemeMode.Dark };
        s.Schedule.StartEnabled = true;
        s.Schedule.StartAt = new TimeOnly(1, 30);
        s.Schedule.Days = [DayOfWeek.Friday];
        store.Save(s);

        var back = store.Load();
        Assert.Equal(20, back.MaxConcurrent);
        Assert.Equal(512, back.SpeedLimitKBps);
        Assert.Equal(ThemeMode.Dark, back.Theme);
        Assert.Equal(new TimeOnly(1, 30), back.Schedule.StartAt);
        Assert.Equal([DayOfWeek.Friday], back.Schedule.Days);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_keeps_a_copy()
    {
        File.WriteAllText(PathIn, "{ not json");
        Assert.Equal(3, new SettingsStore(PathIn).Load().MaxConcurrent);
        Assert.True(File.Exists(PathIn + ".bad"));
    }

    [Fact]
    public void Category_rules_update_from_settings()
    {
        var rules = new CategoryRules(@"C:\D");
        var changed = 0;
        rules.Changed += () => changed++;
        rules.Update(@"E:\Down", [("Books", ["epub", ".mobi"]), ("Other", ["zip"])]);

        Assert.Equal(1, changed);
        Assert.Equal(["Books", "Other"], rules.Categories);
        Assert.Equal("Books", rules.CategoryFor("x.MOBI"));
        Assert.Equal("Other", rules.CategoryFor("x.zip"));
        Assert.Equal(@"E:\Down\Books", rules.FolderFor("Books"));
    }

    [Fact]
    public void Extension_lists_accept_spaces_commas_and_dots() =>
        Assert.Equal(["zip", "rar", "7z"], new CategoryRule { Extensions = ".zip, rar;7z  ZIP" }.ExtensionList());
}
