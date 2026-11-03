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

        var owner = Application.Current.MainWindow;
        var window = new AddBatchWindow(vm)
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        window.ShowDialog();

        return vm.Result is { } request ? services.GetRequiredService<BatchService>().Create(request) : null;
    }
}
