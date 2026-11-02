using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace TurboDL.App.Services;

/// <summary>
/// The notification-area icon, on Shell_NotifyIcon directly so it can also raise notifications
/// (Windows 11 shows them as toasts). The right-click menu is an ordinary WPF ContextMenu, so it gets the Fluent style.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private nint _icon;      // small icon, for the notification area
    private nint _largeIcon; // large icon, for notifications
    private string _tooltip = "TurboDL";
    private bool _added;

    public TrayIcon()
    {
        // An invisible top-level window: it receives the icon's callbacks and the TaskbarCreated broadcast.
        _window = new HwndSource(new HwndSourceParameters("TurboDL tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        LoadIcons();
    }

    public ContextMenu? Menu { get; set; }

    /// <summary>True once Windows has accepted the icon.</summary>
    public bool IsShown => _added;

    /// <summary>Left click on the icon.</summary>
    public event Action? Click;

    /// <summary>The user clicked the most recent notification.</summary>
    public event Action? NotificationClick;

    public void Show()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    public void SetTooltip(string text)
    {
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tooltip) return;
        _tooltip = text;
        if (!_added) return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a notification from the tray icon (a toast on Windows 11).</summary>
    public void Notify(string title, string message)
    {
        if (!_added) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _largeIcon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _window.RemoveHook(WndProc);
        _window.Dispose();
        AppIcon.Destroy(_icon);
        AppIcon.Destroy(_largeIcon);
        _icon = _largeIcon = 0;
    }

    /// <summary>Loads the icons at the sizes Windows uses for the current DPI (16 px small at 100%, 24 at 150%, ...).</summary>
    private void LoadIcons()
    {
        var dpi = GetDpiForWindow(_window.Handle);
        var small = AppIcon.CreateHIcon(GetSystemMetricsForDpi(SM_CXSMICON, dpi));
        var large = AppIcon.CreateHIcon(GetSystemMetricsForDpi(SM_CXICON, dpi));
        AppIcon.Destroy(_icon);
        AppIcon.Destroy(_largeIcon);
        _icon = small;
        _largeIcon = large;
    }

    private NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted (the icon has to be added again) or the DPI changed (the icon needs another size).
            LoadIcons();
            Show();
            if (!_added)
            {
                var data = NewData(NIF_ICON);
                _added = Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
        }
        else if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Click?.Invoke();
                    handled = true;
                    break;
                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    OpenMenu();
                    handled = true;
                    break;
                case NIN_BALLOONUSERCLICK:
                    NotificationClick?.Invoke();
                    handled = true;
                    break;
            }
        }
        return 0;
    }

    private void OpenMenu()
    {
        if (Menu is null) return;
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
        // Bring the menu's own window forward, or it won't close when the user clicks elsewhere.
        if (PresentationSource.FromVisual(Menu) is HwndSource source) SetForegroundWindow(source.Handle);
    }

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int WM_CONTEXTMENU = 0x7B, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405;
    private const int SM_CXICON = 11, SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);
}
