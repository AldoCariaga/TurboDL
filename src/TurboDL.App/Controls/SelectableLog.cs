using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TurboDL.App.Controls;

/// <summary>
/// A log the user can select and copy from: one read-only text box over a live list of lines. New lines are appended and
/// the oldest taken off the top in place, so a selection (and the scroll position) survives the log moving on. It stays
/// at its end while the user is there, and stops following once they scroll up to read.
/// </summary>
public sealed class SelectableLog : TextBox
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IEnumerable), typeof(SelectableLog),
        new PropertyMetadata(null, (d, e) => ((SelectableLog)d).OnLinesChanged((IEnumerable?)e.OldValue)));

    private INotifyCollectionChanged? _subscribed;

    public SelectableLog()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        // Subscribed only while on screen: the list (a download's log) outlives the window.
        Loaded += (_, _) =>
        {
            Subscribe();
            Rebuild();
            ScrollToEnd();
        };
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>The lines to show, oldest first; usually an ObservableCollection&lt;string&gt;.</summary>
    public IEnumerable? Lines
    {
        get => (IEnumerable?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    private void OnLinesChanged(IEnumerable? old)
    {
        if (old is INotifyCollectionChanged previous && previous == _subscribed) Unsubscribe();
        if (IsLoaded) Subscribe();
        Rebuild();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = Lines as INotifyCollectionChanged;
        if (_subscribed is not null) _subscribed.CollectionChanged += OnCollectionChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.CollectionChanged -= OnCollectionChanged;
        _subscribed = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var atEnd = VerticalOffset + ViewportHeight >= ExtentHeight - 24;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex < 0 || e.NewStartingIndex == CountLines() - e.NewItems!.Count:
                foreach (var line in e.NewItems!) AppendText((Text.Length > 0 ? Environment.NewLine : "") + line);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0:
                for (var i = 0; i < e.OldItems!.Count; i++) RemoveFirstLine();
                break;
            default:
                Rebuild();
                break;
        }
        if (atEnd) ScrollToEnd();
    }

    private int CountLines()
    {
        var count = 0;
        foreach (var _ in Lines ?? Array.Empty<string>()) count++;
        return count;
    }

    /// <summary>Takes the oldest line off the top without touching the rest, and moves the selection with the text.</summary>
    private void RemoveFirstLine()
    {
        var text = Text;
        if (text.Length == 0) return;
        var newline = text.IndexOf('\n');
        var length = newline < 0 ? text.Length : newline + 1;
        var start = SelectionStart;
        var end = start + SelectionLength;

        Select(0, length);
        SelectedText = "";

        var newStart = Math.Max(0, start - length);
        var newEnd = Math.Max(0, end - length);
        Select(newStart, newEnd - newStart);
    }

    private void Rebuild()
    {
        var lines = Lines?.Cast<object?>().Select(l => l?.ToString() ?? "") ?? [];
        Text = string.Join(Environment.NewLine, lines);
    }
}
