using System.Windows;
using Microsoft.Win32;
using TurboDL.Installer;

namespace TurboDL.Setup;

/// <summary>
/// Install: Welcome (folder) → Options → Installing → Done. Uninstall (/uninstall): Confirm → Removing → Done.
/// Either can end on the error page, which offers Retry.
/// </summary>
public partial class MainWindow
{
    private enum Page { Welcome, Options, Confirm, Progress, Done, Error }

    private SetupContext _context;
    private readonly bool _uninstall;
    private readonly bool _upgrade;
    private readonly bool _reinstall;
    private Page _page;

    public int ExitCode { get; private set; } = App.ExitFailed;

    public MainWindow(SetupContext context)
    {
        InitializeComponent();
        _context = context;
        _uninstall = context.Args.Uninstall;

        VersionText.Text = $"TurboDL {SetupContext.FullVersion}{(context.IsTest ? " · test run" : "")}";
        FolderBox.Text = context.Locations.InstallDir;

        var existing = context.Engine.Existing();
        _upgrade = existing is not null;
        _reinstall = existing?.Version == SetupContext.Version;
        WelcomeTitle.Text = _reinstall ? $"Reinstall TurboDL {SetupContext.Version}"
            : _upgrade ? $"Update TurboDL to {SetupContext.Version}"
            : $"Install TurboDL {SetupContext.Version}";
        // Only when Windows will actually ask (not when Setup already runs as admin, nor in a test run).
        AdminNote.Visibility = Visible(Worker.NeedsElevation(context));
        if (existing is { } e)
        {
            UpgradeNote.Message = $"TurboDL {e.Version} is installed in {e.InstallDir}. Setup updates it and keeps your options, downloads and settings.";
            UpgradeNote.IsOpen = true;
        }

        var options = context.Engine.CurrentOptions();
        StartMenuBox.IsChecked = options.StartMenuShortcut;
        DesktopBox.IsChecked = options.DesktopShortcut;
        StartupBox.IsChecked = options.StartWithWindows;

        Title = TitleBar.Title = _uninstall ? "Uninstall TurboDL" : "TurboDL Setup";
        Show(_uninstall ? Page.Confirm : Page.Welcome);
        Closing += (_, args) => args.Cancel = _page == Page.Progress;
    }

    private void Show(Page page)
    {
        _page = page;
        WelcomePage.Visibility = Visible(page == Page.Welcome);
        OptionsPage.Visibility = Visible(page == Page.Options);
        ConfirmPage.Visibility = Visible(page == Page.Confirm);
        ProgressPage.Visibility = Visible(page == Page.Progress);
        DonePage.Visibility = Visible(page == Page.Done);
        ErrorPage.Visibility = Visible(page == Page.Error);

        BackButton.Visibility = Visible(page == Page.Options);
        CancelButton.Visibility = Visible(page is not Page.Done);
        CancelButton.IsEnabled = NextButton.IsEnabled = page != Page.Progress;
        CancelButton.Content = page == Page.Error ? "Close" : "Cancel";
        NextButton.Content = page switch
        {
            Page.Welcome => "Next",
            Page.Options => _reinstall ? "Reinstall" : _upgrade ? "Update" : "Install",
            Page.Confirm => "Uninstall",
            Page.Error => "Retry",
            Page.Done => _uninstall ? "Close" : "Finish",
            _ => NextButton.Content,
        };
        NextButton.Visibility = Visible(page != Page.Progress);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Install TurboDL in", Multiselect = false };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) != true) return;
        var chosen = dialog.FolderName;
        // Picking "Program Files" means a TurboDL folder in it, as installers usually do.
        FolderBox.Text = string.Equals(Path.GetFileName(chosen.TrimEnd('\\')), Product.Name, StringComparison.OrdinalIgnoreCase)
            ? chosen
            : Path.Combine(chosen, Product.Name);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Show(Page.Welcome);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome:
                var folder = FolderBox.Text.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(folder) || File.Exists(folder))
                {
                    MessageBox.Show(this, "Enter a full folder path, like C:\\Program Files\\TurboDL.", "TurboDL Setup",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _context = _context.WithInstallDir(Path.GetFullPath(folder));
                Show(Page.Options);
                break;
            case Page.Options:
            case Page.Confirm:
            case Page.Error:
                await RunAsync();
                break;
            case Page.Done:
                if (!_uninstall && LaunchBox.IsChecked == true) LaunchApp();
                Close();
                break;
        }
    }

    private async Task RunAsync()
    {
        ProgressTitle.Text = _uninstall ? "Uninstalling TurboDL" : _reinstall ? "Reinstalling TurboDL"
            : _upgrade ? "Updating TurboDL" : "Installing TurboDL";
        StepText.Text = "Getting ready";
        Bar.Value = 0;
        Bar.IsIndeterminate = true;
        Show(Page.Progress);

        var progress = new Progress<InstallProgress>(p =>
        {
            StepText.Text = p.Text;
            Bar.Maximum = p.Total;
            Bar.Value = p.Done;
            // Moving until the first step is done, so the bar never sits empty while TurboDL closes or files copy.
            Bar.IsIndeterminate = p.Done == 0;
        });
        var flow = new SetupFlow(_context);
        FlowResult result;
        try
        {
            result = _uninstall
                ? await flow.UninstallAsync(RemoveDataBox.IsChecked == true, progress)
                : await flow.InstallAsync(new InstallOptions(StartMenuBox.IsChecked == true, DesktopBox.IsChecked == true,
                    StartupBox.IsChecked == true), progress);
        }
        catch (Exception ex)
        {
            result = new FlowResult(Outcome.Failed, ex.Message);
        }

        if (result.Outcome == Outcome.Done)
        {
            ExitCode = 0;
            DoneTitle.Text = _uninstall ? "TurboDL was removed" : _reinstall ? "TurboDL is reinstalled"
                : _upgrade ? "TurboDL is updated" : "TurboDL is installed";
            DoneText.Text = _uninstall
                ? RemoveDataBox.IsChecked == true
                    ? "Your downloads list and settings were removed too. Files you downloaded are still where you saved them."
                    : "Your downloads list and settings are kept, in case you install TurboDL again."
                : "To catch downloads in Chrome or Edge, add the TurboDL extension to the browser.";
            LaunchBox.Visibility = Visible(!_uninstall);
            Show(Page.Done);
            return;
        }

        ExitCode = result.Outcome switch
        {
            Outcome.AppRunning => App.ExitAppRunning,
            Outcome.Declined => App.ExitDeclined,
            _ => App.ExitFailed,
        };
        ErrorTitle.Text = result.Outcome == Outcome.AppRunning ? "Close TurboDL first" : "Setup couldn't finish";
        ErrorText.Text = result.Message ?? "Something went wrong.";
        Show(Page.Error);
    }

    private void LaunchApp()
    {
        try
        {
            new SetupFlow(_context).Launch();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(this, $"TurboDL is installed, but didn't start: {ex.Message}", "TurboDL Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
