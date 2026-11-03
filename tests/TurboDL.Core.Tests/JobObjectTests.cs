using System.Diagnostics;
using TurboDL.Core.Video;

namespace TurboDL.Core.Tests;

/// <summary>The kill-on-close job the external tools run in.</summary>
public sealed class JobObjectTests : IDisposable
{
    private readonly FakeToolsFolder _f = new();
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        _f.Dispose();
    }

    [Fact]
    public async Task ClosingTheJob_KillsTheToolAndTheChildItStarted()
    {
        using var job = JobObject.TryCreate();
        Assert.NotNull(job);

        // The fake's /hang starts a grandchild (ffmpeg's stand-in) and then sleeps forever.
        var (url, log) = _f.Url("hang");
        var tool = Start(Path.Combine(_f.Tools, "yt-dlp.exe"), "-J", "--", url);
        Assert.True(job.TryAdd(tool));
        var (_, child) = await ToolAndChildAsync(log);

        Assert.False(tool.HasExited);
        Assert.False(child.HasExited);

        // What Windows does when TurboDL exits, crashes or is killed: its handle to the job closes.
        job.Dispose();

        Assert.True(tool.WaitForExit(TimeSpan.FromSeconds(5)), "the tool is still running");
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), "its child is still running");
    }

    [Fact]
    public async Task KillingTurboDL_KillsTheToolsItRan()
    {
        // JobHost stands in for TurboDL: it runs the fake through ToolProcess, which puts it in JobObject.Tools.
        var (url, log) = _f.Url("hang");
        var host = Start(Path.Combine(AppContext.BaseDirectory, "JobHost.exe"), Path.Combine(_f.Tools, "yt-dlp.exe"), "-J", "--", url);
        var (tool, child) = await ToolAndChildAsync(log);
        Assert.False(tool.HasExited);
        Assert.False(child.HasExited);

        // TerminateProcess on TurboDL alone, like a crash or End task: no cleanup code runs, no tree kill.
        host.Kill(entireProcessTree: false);

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(5)), "the stand-in for TurboDL is still running");
        Assert.True(tool.WaitForExit(TimeSpan.FromSeconds(5)), "the tool outlived TurboDL");
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), "the tool's child outlived TurboDL");
    }

    [Fact]
    public void TryAdd_AnExitedProcess_IsRefused()
    {
        using var job = JobObject.TryCreate();
        Assert.NotNull(job);
        var tool = Start(Path.Combine(_f.Tools, "yt-dlp.exe"), "-U");
        tool.WaitForExit();

        Assert.False(job.TryAdd(tool));
    }

    [Fact]
    public void Tools_IsOneJobForTheWholeProcess()
    {
        Assert.NotNull(JobObject.Tools);
        Assert.Same(JobObject.Tools, JobObject.Tools);
    }

    private Process Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    /// <summary>The fake and the child it started, from the log it writes once that child is running.</summary>
    private async Task<(Process Tool, Process Child)> ToolAndChildAsync(string log)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var run = FakeToolsFolder.ReadLog(log);
                if (run.ChildPid is { } childPid)
                {
                    // Held open, so their exit is seen even after the IDs are reused; and killed in Dispose if they escaped.
                    var tool = Process.GetProcessById(run.Pid);
                    _started.Add(tool);
                    var child = Process.GetProcessById(childPid);
                    _started.Add(child);
                    return (tool, child);
                }
            }
            catch (IOException) { } // not written yet, or still being written
            catch (System.Text.Json.JsonException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("the fake never reported its child");
    }
}
