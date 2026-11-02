using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Icon = AppIcon.Render(32);
        vm.CloseRequested += Close;
        Closed += (_, _) => vm.CloseRequested -= Close;
    }
}
