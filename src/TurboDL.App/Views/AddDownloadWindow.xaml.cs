using System.ComponentModel;
using System.Windows;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class AddDownloadWindow : FluentWindow
{
    private readonly AddDownloadViewModel _vm;

    public AddDownloadWindow(AddDownloadViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        vm.CloseRequested += Close;
        vm.PropertyChanged += OnVmChanged;
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
            ShowNote();
        };
        Closed += (_, _) =>
        {
            vm.CloseRequested -= Close;
            vm.PropertyChanged -= OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddDownloadViewModel.NoteKind)) ShowNote();
    }

    private void ShowNote() =>
        NoteText.SetResourceReference(ForegroundProperty, _vm.NoteKind switch
        {
            NoteKind.Error => "SystemFillColorCriticalBrush",
            NoteKind.Warning => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        });
}
