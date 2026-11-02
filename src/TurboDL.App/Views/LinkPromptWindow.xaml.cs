using System.IO;
using System.Windows;
using System.Windows.Threading;
using TurboDL.App.Services;
using TurboDL.Core.Engine;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

/// <summary>
/// The small prompt in the corner when a download link is copied. It doesn't take focus,
/// and goes away by itself after a while unless the mouse is over it.
/// </summary>
public partial class LinkPromptWindow : FluentWindow
{
    private readonly DispatcherTimer _timeout;

    public LinkPromptWindow(Uri link)
    {
        InitializeComponent();
        Link = link;
        var name = FileNames.Sanitize(Uri.UnescapeDataString(Path.GetFileName(link.AbsolutePath)));
        FileName.Text = name;
        FileName.ToolTip = link.ToString();
        Host.Text = link.Host;
        FileIcon.Source = ShellIcons.ForFileLarge(name);

        _timeout = new DispatcherTimer(TimeSpan.FromSeconds(12), DispatcherPriority.Normal, (_, _) => Close(), Dispatcher);
        MouseEnter += (_, _) => _timeout.Stop();
        MouseLeave += (_, _) => _timeout.Start();
        Loaded += (_, _) => PlaceInCorner();
        Closed += (_, _) => _timeout.Stop();
        _timeout.Start();
    }

    public Uri Link { get; }

    /// <summary>True if the user chose Download.</summary>
    public bool Accepted { get; private set; }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
