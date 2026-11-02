using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TurboDL.Core.Catching;

namespace TurboDL.App.Services;

/// <summary>
/// Listens for clipboard changes on the main window's handle and reports copied download links that match
/// the watched file types. Each link is offered once, and never if it is already in the list.
/// </summary>
public sealed partial class ClipboardWatcher(SettingsService settings, DownloadManager manager) : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private HwndSource? _source;
    private string? _lastOffered;
    private string? _ignoreOnce;

    /// <summary>A copied link worth offering, raised on the UI thread.</summary>
    public event Action<Uri>? LinkCopied;

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
        AddClipboardFormatListener(handle);
    }

    /// <summary>TurboDL is about to put this text on the clipboard itself (Copy link); don't offer it back.</summary>
    public void Ignore(string text) => _ignoreOnce = text;

    public void Dispose()
    {
        if (_source is null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source = null;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
        return 0;
    }

    /// <summary>Visible for testing: what happens when text lands on the clipboard.</summary>
    public void OnText(string? text)
    {
        if (text is not null && text == _ignoreOnce)
        {
            _ignoreOnce = null;
            return;
        }
        if (!settings.Current.WatchClipboard) return;

        var link = LinkMatcher.Match(text, settings.Current.CatchExtensionList());
        if (link is null) return;
        var url = link.ToString();
        if (url == _lastOffered) return;
        if (manager.Items.Any(i => string.Equals(i.Info.Url, url, StringComparison.OrdinalIgnoreCase))) return;

        _lastOffered = url;
        LinkCopied?.Invoke(link);
    }

    private void OnClipboardChanged()
    {
        if (!settings.Current.WatchClipboard) return;
        // The app that just wrote may still hold the clipboard; try a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                OnText(Clipboard.ContainsText() ? Clipboard.GetText() : null);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint hwnd);
}
