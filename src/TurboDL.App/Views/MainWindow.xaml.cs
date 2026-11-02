using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TurboDL.App.ViewModels;
using Wpf.Ui.Appearance;
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

        // Follow Windows light/dark live; keep TurboDL's own accent (see TurboTheme).
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
            _vm.Start();
        };
        Closed += (_, _) => _vm.Stop();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(List.SelectedItems.Cast<DownloadItemViewModel>().ToList());

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is System.Windows.Controls.ListViewItem { DataContext: DownloadItemViewModel item })
        {
            _vm.OpenCommand.Execute(item);
        }
    }
}
