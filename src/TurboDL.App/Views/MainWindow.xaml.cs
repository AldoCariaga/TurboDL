using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _vm;

    public MainWindow(MainWindowViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Icon = AppIcon.Render(32);

        // The handle exists even while the window stays hidden in the tray, so the clipboard listener
        // and theme changes work from startup.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(handle).AddHook(WndProc);

        vm.Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueService.IsRunning)) UpdateQueueButton();
        };
        UpdateQueueButton();
        vm.Start();
        Closed += (_, _) => vm.Stop();
    }

    private void UpdateQueueButton()
    {
        var running = _vm.Queue.IsRunning;
        QueueLabel.Text = running ? "Stop queue" : "Start queue";
        QueueIcon.Symbol = running ? SymbolRegular.Stop24 : SymbolRegular.TextBulletListLtr24;
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != 0)
        {
            TurboTheme.OnSettingChange(System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam));
        }
        return 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(List.SelectedItems.Cast<DownloadItemViewModel>().ToList());

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is System.Windows.Controls.ListViewItem { DataContext: DownloadItemViewModel item })
        {
            _vm.ActivateCommand.Execute(item);
        }
    }
}
