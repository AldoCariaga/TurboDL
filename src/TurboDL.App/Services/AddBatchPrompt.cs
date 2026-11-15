using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;

namespace TurboDL.App.Services;

/// <summary>Opens the Add batch window (toolbar, Ctrl+Shift+N, tray) and hands what the user confirmed to the batch service.</summary>
public sealed class AddBatchPrompt(IServiceProvider services)
{
    /// <summary>Shows the dialog with <paramref name="text"/>, else the clipboard's links. Null when cancelled.</summary>
    public BatchViewModel? Show(string? text = null)
    {
        var vm = services.GetRequiredService<AddBatchViewModel>();
        vm.Prefill(text);
        ShowDialog(vm, null);
        return vm.Result is { } request ? services.GetRequiredService<BatchService>().Create(request) : null;
    }

    /// <summary>The same dialog for adding links to a batch that hasn't finished (sidebar menu, batch details).</summary>
    public void ShowForBatch(BatchViewModel batch, Window? owner = null)
    {
        var batches = services.GetRequiredService<BatchService>();
        if (!batches.CanAddTo(batch)) return;
        var vm = services.GetRequiredService<AddBatchViewModel>();
        vm.PrefillForBatch(batch, batches.PartsOf(batch));
        ShowDialog(vm, owner);
        // The batch may have started extracting while the dialog was open; AddTo checks again.
        if (vm.Result is { } request) batches.AddTo(batch, request);
    }

    private static void ShowDialog(AddBatchViewModel vm, Window? owner)
    {
        owner ??= Application.Current.MainWindow;
        var window = new AddBatchWindow(vm)
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        window.ShowDialog();
    }
}
