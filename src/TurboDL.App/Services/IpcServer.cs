using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TurboDL.Core.Ipc;

namespace TurboDL.App.Services;

/// <summary>
/// Serves this user's <c>TurboDL.Ipc.&lt;SID&gt;</c> pipe: second launches and the browser's native host send
/// <c>add</c>, <c>addVideo</c>, <c>show</c> and <c>getConfig</c> here. Requests run on the UI thread; <c>add</c> and
/// <c>addVideo</c> reply at once and open the Add dialog afterwards, so the caller never waits on the user.
/// </summary>
public sealed class IpcServer(AddDownloadPrompt addPrompt, SettingsService settings, ILogger<IpcServer> log)
{
    private IpcPipeServer? _pipe;

    public bool IsRunning => _pipe is not null;

    public void Start(string? pipeName = null)
    {
        if (_pipe is not null) return;
        var pipe = new IpcPipeServer(HandleAsync, pipeName);
        pipe.Error += ex => log.LogWarning(ex, "Pipe request failed");
        try
        {
            pipe.Start();
            _pipe = pipe;
        }
        catch (Exception ex)
        {
            // The app still works; only the browser hand-off and second launches can't reach it.
            log.LogError(ex, "Couldn't open the {Pipe} pipe", pipe.PipeName);
        }
    }

    public void Stop()
    {
        var pipe = _pipe;
        _pipe = null;
        if (pipe is null) return;
        // Called on the UI thread at exit. A request waiting for the UI is cancelled by the dispose, and the
        // wait is bounded either way, so this can't hang the exit.
        Task.Run(() => pipe.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Opens the Add dialog pre-filled from an <c>add</c> request and brings TurboDL to the front.</summary>
    public void OpenAdd(IpcRequest request)
    {
        WindowService.ShowMain();
        addPrompt.Show(request.Url, request.BrowserHeaders(), request.Filename);
    }

    /// <summary>Opens the Add dialog in video mode from an <c>addVideo</c> request (the browser's video menu).</summary>
    public void OpenAddVideo(IpcRequest request)
    {
        WindowService.ShowMain();
        addPrompt.ShowVideo(request.Url!, request.PageUrl, request.BrowserHeaders());
    }

    private Task<IpcReply> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return Task.FromResult(IpcReply.Fail("TurboDL is shutting down."));
        return dispatcher.InvokeAsync(() => HandleOnUiThread(request, dispatcher), DispatcherPriority.Normal, ct).Task;
    }

    private IpcReply HandleOnUiThread(IpcRequest request, Dispatcher dispatcher)
    {
        switch (request.Type)
        {
            case IpcCommands.Add:
                // Queued behind this reply: the dialog is modal and the caller must not wait for the user.
                dispatcher.BeginInvoke(() => OpenAdd(request));
                return IpcReply.Success();
            case IpcCommands.AddVideo:
                dispatcher.BeginInvoke(() => OpenAddVideo(request));
                return IpcReply.Success();
            case IpcCommands.Show:
                WindowService.ShowMain();
                return IpcReply.Success();
            case IpcCommands.GetConfig:
                var current = settings.Current;
                return IpcReply.Config(current.CatchExtensionList(), Math.Max(0, current.MinCatchSizeKB));
            default:
                return IpcReply.Fail($"Unknown command \"{request.Type}\".");
        }
    }
}
