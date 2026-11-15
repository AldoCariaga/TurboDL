using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
        UpdateAddButton();
    }

    /// <summary>Add links is there while the batch is still downloading or waiting for the user.</summary>
    private void OnBatchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchViewModel.StatusText) or nameof(BatchViewModel.IsBusy)) UpdateAddButton();
    }

    private void UpdateAddButton() => AddLinks.IsEnabled = _batches.CanAddTo(_batch);

    private void OnAddLinks(object sender, RoutedEventArgs e)
    {
        _addPrompt.ShowForBatch(_batch, this);
        UpdateAddButton();
    }

    /// <summary>Where the batch was last extracted to, else its download folder.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _batch.Info.ExtractedTo.LastOrDefault() ?? _batch.Info.Directory;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
