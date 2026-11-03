using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TurboDL.Core.Video;

/// <summary>
/// A Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: when its last handle closes, every process in it
/// is killed. The handle closes when it's disposed, or when the process that owns it exits, crashes or is killed,
/// so tools put in the job can't outlive TurboDL. Processes they start afterwards (ffmpeg, deno) join the job too.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    private JobObject(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// The job the external tools run in, created on first use and kept open for the life of this process.
    /// Null when there isn't one (not Windows, or Windows refused to create it).
    /// </summary>
    public static JobObject? Tools => LazyTools.Value;

    private static readonly Lazy<JobObject?> LazyTools = new(TryCreate);

    /// <summary>A new, empty kill-on-close job, or null (with a trace warning) if Windows won't create one.</summary>
    public static JobObject? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            Warn("Couldn't create the job object for external tools", Marshal.GetLastPInvokeError());
            handle.Dispose();
            return null;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Warn("Couldn't make the external tools' job kill on close", Marshal.GetLastPInvokeError());
            handle.Dispose();
            return null;
        }
        return new JobObject(handle);
    }

    /// <summary>
    /// Puts a running process in the job. False (with a trace warning) if Windows refused, for example because the
    /// process has already exited, or it's in a job of its own that doesn't allow nesting.
    /// </summary>
    public bool TryAdd(Process process)
    {
        bool added;
        int error;
        try
        {
            added = AssignProcessToJobObject(_handle, process.SafeHandle);
            error = Marshal.GetLastPInvokeError();
        }
        catch (InvalidOperationException)
        {
            Trace.TraceWarning("TurboDL: a process with no handle (not started, or disposed) can't join the external tools' job.");
            return false;
        }
        if (!added) Warn($"Couldn't put process {process.Id} in the external tools' job", error);
        return added;
    }

    /// <summary>Closes the job's handle; if it was the last one, everything still in the job is killed.</summary>
    public void Dispose() => _handle.Dispose();

    // Trace, not Debug, so it's there in Release builds too; it reaches the same debugger output as the app's logger.
    private static void Warn(string what, int win32Error) =>
        Trace.TraceWarning($"TurboDL: {what} (Win32 error {win32Error}). Such a tool may outlive TurboDL if it crashes.");

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
