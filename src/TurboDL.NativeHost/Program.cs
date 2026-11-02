using TurboDL.NativeHost;

// Chrome starts this with the caller's origin (chrome-extension://<id>/) and, on Windows, --parent-window=<hwnd>.
// stdout carries framed replies only: nothing else may ever be written to it.
var log = new HostLog(HostLog.DefaultPath, Console.Error);
log.Write($"Started by {(args.Length > 0 ? args[0] : "(no caller)")}.");

try
{
    var launcher = new AppLauncher(AppLauncher.ResolveAppPath(AppContext.BaseDirectory), log);
    var loop = new NativeHostLoop(launcher, log);
    await using var stdin = Console.OpenStandardInput();
    await using var stdout = Console.OpenStandardOutput();
    return await loop.RunAsync(stdin, stdout);
}
catch (Exception ex)
{
    log.Write($"Host crashed: {ex}");
    return 2;
}
