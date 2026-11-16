using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TurboDL.App.Services;
using TurboDL.App.ViewModels;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

/// <summary>A batch's status, extraction progress and activity log. One per batch; it follows the batch live.</summary>
public partial class BatchDetailsWindow : FluentWindow
{
    private readonly BatchViewModel _batch;
    private readonly BatchService _batches;
    private readonly AddBatchPrompt _addPrompt;

    public BatchDetailsWindow(BatchViewModel batch, BatchService batches, AddBatchPrompt addPrompt)
    {
        _batch = batch;
        _batches = batches;
        _addPrompt = addPrompt;
        DataContext = batch;
        InitializeComponent();
        Icon = AppIcon.Window;

        batch.PropertyChanged += OnBatchChanged;
        Closed += (_, _) => batch.PropertyChanged -= OnBatchChanged;
        UpdateButtons();
    }

    /// <summary>Add links is there while the batch is still downloading or waiting for the user; the extract options until it extracts.</summary>
    private void OnBatchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchViewModel.StatusText) or nameof(BatchViewModel.IsBusy)) UpdateButtons();
    }

    private void UpdateButtons()
    {
        AddLinks.IsEnabled = _batches.CanAddTo(_batch);
        var canChange = !_batches.IsExtracting(_batch);
        ChangeExtractTo.IsEnabled = canChange;
        DefaultExtractTo.IsEnabled = canChange && _batch.Info.ExtractTo is not null;
        DeletePartsSwitch.IsEnabled = canChange;
    }

    private void OnChangeExtractTo(object sender, RoutedEventArgs e)
    {
        var start = _batch.Info.ExtractTo ?? _batch.Info.Directory;
        var dialog = new OpenFolderDialog { Title = "Choose where to extract", InitialDirectory = Directory.Exists(start) ? start : null };
        if (dialog.ShowDialog(this) == true) _batches.ExtractTo(_batch, dialog.FolderName);
        UpdateButtons();
    }

    private void OnDefaultExtractTo(object sender, RoutedEventArgs e)
    {
        _batches.SetExtractOptions(_batch, null, _batch.Info.DeleteParts);
        UpdateButtons();
    }

    private void OnDeletePartsClick(object sender, RoutedEventArgs e)
    {
        _batches.SetExtractOptions(_batch, _batch.Info.ExtractTo, DeletePartsSwitch.IsChecked == true);
        // Refused (extracting): show the setting as it is.
        DeletePartsSwitch.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _batch.Info.DeleteParts);
        UpdateButtons();
    }

    private void OnAddLinks(object sender, RoutedEventArgs e)
    {
        _addPrompt.ShowForBatch(_batch, this);
        UpdateButtons();
    }

    /// <summary>Where the batch was last extracted to, else its download folder.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _batch.Info.ExtractedTo.LastOrDefault() ?? _batch.Info.Directory;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
