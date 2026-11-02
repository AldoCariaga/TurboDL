using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TurboDL.Core.Settings;

namespace TurboDL.App.Services;

/// <summary>The "Start with Windows" entry under HKCU\...\Run.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TurboDL";

    /// <summary>What gets registered: this exe, starting quietly in the tray.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}

/// <summary>Carries out "when the queue finishes". Behind an interface so tests never put the PC to sleep.</summary>
public interface IPowerActions
{
    void Sleep();
    void ShutDown();
}

public sealed partial class PowerActions : IPowerActions
{
    public void Sleep() => SetSuspendState(false, false, false);

    public void ShutDown() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}

public static class WhenDoneText
{
    public static string Describe(WhenQueueDone action) => action switch
    {
        WhenQueueDone.Sleep => "Sleep",
        WhenQueueDone.ShutDown => "Shut down",
        WhenQueueDone.ExitApp => "Close TurboDL",
        _ => "Do nothing",
    };
}
