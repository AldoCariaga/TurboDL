using System.ComponentModel;
using System.Windows;
using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using TurboDL.Core.Model;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class DetailsWindow : FluentWindow
{
    private readonly DetailsViewModel _vm;

    private readonly ChangeLinkPrompt _changeLink;

    public DetailsWindow(DetailsViewModel vm, ChangeLinkPrompt changeLink)
    {
        _vm = vm;
        _changeLink = changeLink;
        DataContext = vm;
        InitializeComponent();
        Icon = AppIcon.Window;

        vm.Item.PropertyChanged += OnItemChanged;
        Closed += (_, _) =>
        {
            vm.Item.PropertyChanged -= OnItemChanged;
            vm.Dispose();
        };
        UpdateButton();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItemViewModel.IsRunning) or nameof(DownloadItemViewModel.Status)) UpdateButton();
    }

    private void UpdateButton()
    {
        var item = _vm.Item;
        PauseResume.Content = item.IsRunning ? "Pause" : "Resume";
        PauseResume.Visibility = item.Status == DownloadStatus.Completed ? Visibility.Collapsed : Visibility.Visible;
        StatusValue.SetResourceReference(ForegroundProperty, item.Status switch
        {
            DownloadStatus.Downloading => "AccentTextFillColorPrimaryBrush",
            DownloadStatus.Paused => "SystemFillColorCautionBrush",
            DownloadStatus.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorPrimaryBrush",
        });
    }

    private void OnChangeLink(object sender, RoutedEventArgs e) => _changeLink.Show(_vm.Item, this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
