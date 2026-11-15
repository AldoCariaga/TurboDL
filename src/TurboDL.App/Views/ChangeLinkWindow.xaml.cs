using System.ComponentModel;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

public partial class ChangeLinkWindow : FluentWindow
{
    private readonly ChangeLinkViewModel _vm;

    public ChangeLinkWindow(ChangeLinkViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        vm.CloseRequested += Close;
        vm.PropertyChanged += OnVmChanged;
        Loaded += (_, _) =>
        {
            LinkBox.Focus();
            LinkBox.SelectAll();
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
        if (e.PropertyName == nameof(ChangeLinkViewModel.NoteKind)) ShowNote();
    }

    private void ShowNote() =>
        NoteText.SetResourceReference(ForegroundProperty, _vm.NoteKind switch
        {
            NoteKind.Error => "SystemFillColorCriticalBrush",
            NoteKind.Warning => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        });
}
