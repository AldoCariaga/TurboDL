using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TurboDL.App.ViewModels;
using TurboDL.Core.Model;
using TurboDL.Core.Queue;
using TurboDL.Core.Settings;

namespace TurboDL.App.Services;

/// <summary>
/// Runs the download queue: keeps up to "downloads at once" going, oldest first, and follows the schedule.
/// The rules themselves live in <see cref="QueuePlanner"/> and <see cref="ScheduleClock"/>.
/// </summary>
public sealed partial class QueueService : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settings;
    private readonly HashSet<long> _startedByQueue = [];
    private readonly HashSet<long> _stopping = [];
    private readonly DispatcherTimer _clock;
    private DateTime _lastCheck = DateTime.Now;
    private bool _startedAnything;

    public QueueService(DownloadManager manager, SettingsService settings)
    {
        _manager = manager;
        _settings = settings;
        manager.Ended += OnEnded;
        manager.Items.CollectionChanged += OnItemsChanged;
        settings.Changed += _ => Pump();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckSchedule(DateTime.Now),
            Dispatcher.CurrentDispatcher);
    }

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    /// <summary>The queue ran something and has now emptied. Carries the "when done" action to perform.</summary>
    public event Action<WhenQueueDone>? Finished;

    /// <summary>Starts the schedule clock, and the queue itself if it was running when the app last closed.</summary>
    public void Initialize()
    {
        _clock.Start();
        if (_settings.Current.QueueRunning) Start();
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _startedAnything = false;
        Remember(true);
        Pump();
    }

    /// <summary>Stops the queue. Downloads it started are paused and go back to waiting in the queue.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Remember(false);
        foreach (var item in _manager.Items.Where(i => i.IsRunning && _startedByQueue.Contains(i.Info.Id)).ToList())
        {
            _stopping.Add(item.Info.Id);
            _manager.Pause(item);
        }
    }

    /// <summary>Visible for testing: one scheduler check at the given time.</summary>
    public void CheckSchedule(DateTime now)
    {
        var schedule = _settings.Current.Schedule;
        var last = _lastCheck;
        _lastCheck = now;
        if (schedule.StartEnabled && ScheduleClock.Crossed(last, now, schedule.StartAt, schedule.Days)) Start();
        if (schedule.StopEnabled && ScheduleClock.Crossed(last, now, schedule.StopAt, schedule.Days)) Stop();
    }

    public void Pump()
    {
        if (!IsRunning) return;
        var entries = Entries();
        foreach (var id in QueuePlanner.ToStart(entries, _settings.Current.MaxConcurrent))
        {
            var item = _manager.Items.FirstOrDefault(i => i.Info.Id == id);
            if (item is null) continue;
            _startedByQueue.Add(id);
            _startedAnything = true;
            _manager.Start(item);
        }

        if (QueuePlanner.IsFinished(Entries(), _startedByQueue))
        {
            IsRunning = false;
            Remember(false);
            _startedByQueue.Clear();
            // Starting an empty queue (by hand or by schedule) must not shut the PC down.
            if (_startedAnything) Finished?.Invoke(_settings.Current.Schedule.WhenDone);
        }
    }

    private List<QueueEntry> Entries() =>
        _manager.Items.Select(i => new QueueEntry(i.Info.Id, i.Status, i.IsRunning)).ToList();

    private void OnEnded(DownloadItemViewModel item)
    {
        var id = item.Info.Id;
        if (_stopping.Remove(id) && item.Status == DownloadStatus.Paused) _manager.Requeue(item);
        if (!item.IsRunning && item.Status != DownloadStatus.Queued) _startedByQueue.Remove(id);
        Pump();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Pump();
    }

    private void Remember(bool running)
    {
        if (_settings.Current.QueueRunning == running) return;
        _settings.Current.QueueRunning = running;
        _settings.Save();
    }
}
