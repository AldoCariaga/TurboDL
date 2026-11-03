// Stands in for TurboDL in JobObjectTests: runs a tool the way TurboDL does (through ToolProcess, so it joins the
// kill-on-close job) and waits for it. The test kills this process hard and checks that the tool went with it.
// Usage: JobHost <tool.exe> [tool arguments...]
using TurboDL.Core.Video;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: JobHost <tool.exe> [tool arguments...]");
    return 2;
}

var result = await ToolProcess.RunAsync(args[0], args[1..], onStdOut: null, onStdErr: null, timeout: null, CancellationToken.None);
return result.ExitCode;
