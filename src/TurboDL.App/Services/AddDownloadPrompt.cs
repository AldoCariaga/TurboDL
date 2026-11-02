using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TurboDL.App.ViewModels;
using TurboDL.App.Views;

namespace TurboDL.App.Services;

/// <summary>
/// Opens the Add download window. Called from the toolbar today; the clipboard watcher and the browser
/// hand-off will call it too, passing their URL and headers.
/// </summary>
public sealed class AddDownloadPrompt(IServiceProvider services)
{
    public DownloadItemViewModel? Show(string? url = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var vm = services.GetRequiredService<AddDownloadViewModel>();
        vm.Prefill(url, headers);

        var owner = Application.Current.MainWindow;
        var window = new AddDownloadWindow(vm)
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        window.ShowDialog();

        return vm.Result is { } info
            ? services.GetRequiredService<DownloadManager>().Add(info, vm.StartImmediately)
            : null;
    }
}
