using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using TurboDL.Installer;

namespace TurboDL.Setup;

/// <summary>
/// The machine half of an install or uninstall (Program Files and Installed apps), run in a separate copy of Setup
/// started with <c>/worker=install|uninstall</c>: elevated when that needs admin rights. It reports through a log file
/// the UI reads: <c>progress|done|total|text</c>, then <c>ok</c> or <c>error|message</c>.
/// </summary>
public static class Worker
{
    /// <summary>Runs in the worker process. Returns its exit code.</summary>
    public static int Run(SetupContext context)
    {
        var log = context.Args.Log;
        void Write(string line)
        {
            if (log is not null) File.AppendAllText(log, line.ReplaceLineEndings(" ") + Environment.NewLine);
        }

        try
        {
            var progress = new InlineProgress<InstallProgress>(p => Write($"progress|{p.Done}|{p.Total}|{p.Text}"));
            if (context.Args.Worker == "install")
                context.Engine.InstallMachine(SetupContext.Payload(), SetupContext.ExtensionId, SetupContext.Version, progress);
            else
                context.Engine.UninstallMachine(progress);
            Write("ok");
            return 0;
        }
        catch (Exception ex)
        {
            Write("error|" + ex.Message);
            return 1;
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Whether the machine half needs admin rights: always for the real Installed apps entry (HKLM); in a test run
    /// only when the install folder isn't writable.
    /// </summary>
    public static bool NeedsElevation(SetupContext context) =>
        !IsElevated() && (!context.IsTest || !CanWrite(context.Locations.InstallDir));

    private static bool CanWrite(string dir)
    {
        var existing = dir;
        while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing is null) return false;
        var probe = Path.Combine(existing, $".turbodl-write-test-{Environment.ProcessId}");
        try
        {
            File.Create(probe).Dispose();
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the worker (through UAC when needed), relays its progress and waits for it. Null = it worked; otherwise
    /// why not. <see cref="Declined"/> when the user said no to the Windows prompt.
    /// </summary>
    public static async Task<string?> RunAsync(SetupContext context, string verb, IProgress<InstallProgress>? progress)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Setup is running from.");
        var log = Path.Combine(Path.GetTempPath(), $"TurboDL-Setup-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, "");
        var elevate = NeedsElevation(context);

        var start = new ProcessStartInfo(self) { UseShellExecute = elevate, CreateNoWindow = !elevate };
        if (elevate) start.Verb = "runas";
        start.ArgumentList.Add($"/worker={verb}");
        start.ArgumentList.Add($"/dir={context.Locations.InstallDir}");
        if (context.Args.TestRoot is { } root) start.ArgumentList.Add($"/testroot={root}");
        start.ArgumentList.Add($"/log={log}");

        try
        {
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("Setup's worker didn't start.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return Declined;
            }

            using (process)
            {
                var read = 0;
                string? outcome = null;
                void Drain()
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(log); }
                    catch (IOException) { return; }
                    for (; read < lines.Length; read++)
                    {
                        var parts = lines[read].Split('|', 4);
                        if (parts[0] == "progress" && parts.Length == 4)
                            progress?.Report(new InstallProgress(int.Parse(parts[1]), int.Parse(parts[2]), parts[3]));
                        else if (parts[0] is "ok" or "error")
                            outcome = lines[read];
                    }
                }

                while (!process.HasExited)
                {
                    Drain();
                    await Task.Delay(150);
                }
                Drain();
                if (outcome == "ok" && process.ExitCode == 0) return null;
                return outcome?.StartsWith("error|") == true ? outcome[6..] : $"Setup's worker stopped (exit code {process.ExitCode}).";
            }
        }
        finally
        {
            try { File.Delete(log); }
            catch (IOException) { }
        }
    }

    public const string Declined = "Setup needs administrator rights to install into this folder, and the Windows prompt was declined.";

    private const int ErrorCancelled = 1223;
}

/// <summary>An IProgress that reports on the caller's thread, in order (Progress&lt;T&gt; posts to a context).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
