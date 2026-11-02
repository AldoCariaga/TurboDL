using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace TurboDL.App.Views;

/// <summary>
/// "Shutting down in 60 seconds" with Cancel as the default button, so a sleep or shutdown
/// after the queue never happens without a chance to stop it.
/// </summary>
public partial class CountdownWindow : FluentWindow
{
    private readonly string _action;
    private readonly DispatcherTimer _timer;
    private int _seconds;

    public CountdownWindow(string action, int seconds = 60)
    {
        InitializeComponent();
        _action = action;
        _seconds = seconds;
        NowButton.Content = $"{action} now";
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Update();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>True when the countdown ran out or the user chose to go ahead now.</summary>
    public bool Proceed { get; private set; }

    private void Tick()
    {
        _seconds--;
        if (_seconds <= 0)
        {
            Proceed = true;
            Close();
            return;
        }
        Update();
    }

    private void Update() =>
        Message.Text = $"{_action} in {_seconds} second{(_seconds == 1 ? "" : "s")}, as set in Settings.";

    private void OnNow(object sender, RoutedEventArgs e)
    {
        Proceed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
