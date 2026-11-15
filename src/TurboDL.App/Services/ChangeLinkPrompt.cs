using System.Windows;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;

namespace TurboDL.App.Services;

/// <summary>Opens the Change link dialog (row menu, Details window) and points the download at what the user saved.</summary>
public sealed class ChangeLinkPrompt(DownloadManager manager, QueueService queue)
{
    public void Show(DownloadItemViewModel item, Window? owner = null)
    {
        if (!DownloadManager.CanChangeLink(item)) return;
        var vm = new ChangeLinkViewModel(item, manager.Items.ToList());
        owner ??= Application.Current.MainWindow;
        var window = new ChangeLinkWindow(vm)
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        window.ShowDialog();

        // It may have started (the queue) while the dialog was open; ChangeLink leaves a running download alone.
        if (vm.Result is not { } request || !manager.Items.Contains(item) || !DownloadManager.CanChangeLink(item)) return;
        manager.ChangeLink(item, request.Url, request.FileName);
        queue.Pump(); // a running queue picks it up; a stopped one leaves it Queued
    }
}
