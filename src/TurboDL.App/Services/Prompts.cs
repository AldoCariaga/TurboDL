using System.Windows;
using TurboDL.App.ViewModels;
using TurboDL.Core.Model;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace TurboDL.App.Services;

/// <summary>Confirmation dialogs, in the Fluent message box style.</summary>
public sealed class Prompts : IUserPrompts, IDeletePrompt
{
    /// <summary>Owned by the main window only while it is on screen; from the tray the dialog stands alone.</summary>
    private static Window? VisibleMain() => Application.Current.MainWindow is { IsVisible: true } w ? w : null;

    public async Task<bool?> ConfirmDeleteAsync(IReadOnlyList<DownloadItemViewModel> items)
    {
        var anyFinished = items.Any(i => i.Status == DownloadStatus.Completed);
        var box = new MessageBox
        {
            Title = items.Count == 1 ? $"Delete {items[0].Name}?" : $"Delete {items.Count} downloads?",
            Content = anyFinished
                ? "Remove from the list only, or also send the downloaded files to the Recycle Bin."
                : "Partly downloaded data is deleted too, so this can't be resumed later.",
            PrimaryButtonText = anyFinished ? "Remove from list" : "Delete",
            SecondaryButtonText = anyFinished ? "Delete files too" : "",
            IsSecondaryButtonEnabled = anyFinished,
            CloseButtonText = "Cancel",
            Owner = VisibleMain(),
        };
        return await box.ShowDialogAsync() switch
        {
            MessageBoxResult.Primary => false,
            MessageBoxResult.Secondary => true,
            _ => null,
        };
    }

    public async Task<bool> ConfirmRestartAsync(string fileName)
    {
        var box = new MessageBox
        {
            Title = "The file on the server changed",
            Content = $"{fileName} is different on the server than when this download started. "
                      + "Start over to get the new version?",
            PrimaryButtonText = "Start over",
            CloseButtonText = "Not now",
            Owner = VisibleMain(),
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
