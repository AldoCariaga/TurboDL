using System.Collections.Specialized;
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

    public BatchDetailsWindow(BatchViewModel batch)
    {
        _batch = batch;
        DataContext = batch;
        InitializeComponent();
        Icon = AppIcon.Window;

        batch.Log.CollectionChanged += OnLogChanged;
        Closed += (_, _) => batch.Log.CollectionChanged -= OnLogChanged;
        Loaded += (_, _) => LogScroll.ScrollToEnd();
    }

    /// <summary>New lines keep the log at its end, unless the user has scrolled up to read.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 24) LogScroll.ScrollToEnd();
    }

    /// <summary>Where the batch was last extracted to, else its download folder.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = _batch.Info.ExtractedTo.LastOrDefault() ?? _batch.Info.Directory;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
