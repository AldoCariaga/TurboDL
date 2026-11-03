using System.ComponentModel;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class AddBatchWindow : FluentWindow
{
    private readonly AddBatchViewModel _vm;

    public AddBatchWindow(AddBatchViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        vm.CloseRequested += Close;
        vm.PropertyChanged += OnVmChanged;
        Loaded += (_, _) =>
        {
            LinksBox.Focus();
            LinksBox.SelectAll();
            ShowSummary();
        };
        Closed += (_, _) =>
        {
            vm.CloseRequested -= Close;
            vm.PropertyChanged -= OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddBatchViewModel.SummaryKind)) ShowSummary();
    }

    private void ShowSummary() =>
        SummaryText.SetResourceReference(ForegroundProperty, _vm.SummaryKind switch
        {
            NoteKind.Error => "SystemFillColorCriticalBrush",
            NoteKind.Warning => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        });
}
