using TurboDL.Setup;

namespace TurboDL.Installer.Tests;

public sealed class SetupArgsTests
{
    [Fact]
    public void Parses_every_switch()
    {
        var args = SetupArgs.Parse(["/uninstall", "/S", @"/dir=""C:\Program Files\TurboDL""", @"/testroot=C:\scratch\t1",
            @"/log=C:\scratch\t1.log", "/removedata", "/relocated", "/startmenu=0", "/desktop=1", "/startup"]);

        Assert.True(args.Uninstall);
        Assert.True(args.Silent);
        Assert.Equal(@"C:\Program Files\TurboDL", args.InstallDir);
        Assert.Equal(@"C:\scratch\t1", args.TestRoot);
        Assert.Equal(@"C:\scratch\t1.log", args.Log);
        Assert.True(args.RemoveData);
        Assert.True(args.Relocated);
        Assert.False(args.StartMenu);
        Assert.True(args.Desktop);
        Assert.True(args.Startup);
        Assert.Equal("install", SetupArgs.Parse(["/worker=install"]).Worker);
        Assert.Null(SetupArgs.Parse([]).StartMenu);
    }

    [Theory]
    [InlineData("/testroot=", "/testroot needs a folder")]
    [InlineData("/testroot", "/testroot needs a folder")]
    [InlineData("/dir=  ", "/dir needs a folder")]
    [InlineData("/log=", "/log needs a file")]
    [InlineData("/worker=repair", "/worker needs install or uninstall")]
    [InlineData("/startup=maybe", "use 0 or 1")]
    [InlineData("/frobnicate", "Unknown argument")]
    [InlineData(@"C:\x", "Unknown argument")]
    public void Mistakes_are_named(string arg, string expected) =>
        Assert.Contains(expected, Assert.Throws<ArgumentException>(() => SetupArgs.Parse([arg])).Message);

    [Theory]
    [InlineData(true, "/S", "/testroot=")]
    [InlineData(true, "/worker=install", "/bogus")]
    [InlineData(true, "/silent")]
    [InlineData(false, "/uninstall", "/testroot=")]
    [InlineData(false, "/Startup")]
    public void Silent_and_worker_runs_are_recognised_even_when_unparseable(bool expected, params string[] args) =>
        Assert.Equal(expected, SetupArgs.AsksForNoWindow(args));

    [Fact]
    public void The_log_is_found_in_unparseable_arguments()
    {
        Assert.Equal(@"C:\s\x.log", SetupArgs.RawLog(["/S", "/testroot=", @"/log=""C:\s\x.log"""]));
        Assert.Null(SetupArgs.RawLog(["/S", "/log="]));
    }
}
