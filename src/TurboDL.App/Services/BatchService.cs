using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TurboDL.App.ViewModels;
using TurboDL.Core.Archives;
using TurboDL.Core.Batches;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Model;
using TurboDL.Core.Storage;
using TurboDL.Core.Video;

namespace TurboDL.App.Services;

/// <summary>What the Add batch dialog asks for.</summary>
public sealed record BatchRequest(IReadOnlyList<Uri> Urls, string Folder, IReadOnlyDictionary<string, string> Headers,
    bool AutoExtract, bool DeleteParts, bool StartNow, int Connections);

/// <summary>
/// Runs batches: a group of links added together whose files are checked when they finish, retried a few times when the
/// network let them down, and extracted with 7-Zip once every part is in. The decisions come from
/// <see cref="BatchPlanner"/>; this class carries them out. UI thread only.
/// </summary>
public sealed class BatchService
{
    private readonly DownloadManager manager;
    private readonly QueueService queue;
    private readonly DownloadStore store;
    private readonly SevenZipService sevenZip;
    private readonly CategoryRules rules;
    private readonly ILogger<BatchService> log;

    /// <summary>Download id → the timer that retries it. One per download, so a retry is never scheduled twice.</summary>
    private readonly Dictionary<long, DispatcherTimer> _retryTimers = [];

    /// <summary>Batch id → its running extraction's cancel source. Its keys are the batches being extracted.</summary>
    private readonly Dictionary<long, CancellationTokenSource> _extracting = [];

    /// <summary>Batch id → what the extraction is doing right now, for the status line.</summary>
    private readonly Dictionary<long, string> _busyText = [];

    public BatchService(DownloadManager manager, QueueService queue, DownloadStore store, SevenZipService sevenZip,
        CategoryRules rules, ILogger<BatchService> log)
    {
        this.manager = manager;
        this.queue = queue;
        this.store = store;
        this.sevenZip = sevenZip;
        this.rules = rules;
        this.log = log;
        manager.Ended += OnEnded;
        manager.Items.CollectionChanged += OnItemsChanged;
    }

    public ObservableCollection<BatchViewModel> Batches { get; } = [];

    /// <summary>Raised for the tray toast: title, text, and what clicking the toast does (null = show the window).</summary>
    public event Action<string, string, Action?>? Notify;

    /// <summary>Loads the saved batches. Call after the download list is loaded and the queue initialised.</summary>
    public void Load()
    {
        foreach (var info in store.LoadBatches())
        {
            var batch = new BatchViewModel(info);
            if (ItemsOf(batch).Count == 0)
            {
                store.DeleteBatch(info.Id);
                continue;
            }
            Batches.Add(batch);

            // An extraction cut short by closing the app starts over (into a fresh folder).
            if (info.Status == BatchStatus.Extracting) info.Status = BatchStatus.Downloading;
            Evaluate(batch);
        }
    }

    /// <summary>Makes a batch and adds one download per link, saved under the name the link gives (or the server's).</summary>
    public BatchViewModel Create(BatchRequest request)
    {
        var urlNames = request.Urls.Select(NameFromUrl).ToList();
        var name = MultipartArchive.Group(urlNames.Where(n => n.Length > 0)).FirstOrDefault()?.BaseName
                   ?? "Batch " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");

        var info = new BatchInfo
        {
            Name = name,
            Directory = request.Folder,
            AutoExtract = request.AutoExtract,
            DeleteParts = request.DeleteParts,
        };
        store.AddBatch(info);
        var batch = new BatchViewModel(info);
        Batches.Add(batch);

        for (var i = 0; i < request.Urls.Count; i++)
        {
            var urlName = urlNames[i];
            manager.Add(new DownloadInfo
            {
                Url = request.Urls[i].ToString(),
                FileName = urlName,
                Directory = request.Folder,
                Category = rules.CategoryFor(urlName is "" ? null : urlName),
                Connections = request.Connections,
                Headers = new(request.Headers),
                ExpectFile = true,
                BatchId = info.Id,
                RequestedFileName = urlName,
            }, startNow: false);
        }

        // Otherwise the parts wait in the queue, which picks them up whenever it runs.
        if (request.StartNow) queue.Start();
        batch.Refresh(ItemsOf(batch));
        return batch;
    }

    /// <summary>Starts the batch's failed parts again, with a fresh set of automatic retries.</summary>
    public void RetryFailed(BatchViewModel batch)
    {
        var failed = ItemsOf(batch).Where(i => i.Status == DownloadStatus.Failed && !i.IsRunning).ToList();
        foreach (var item in failed) batch.Info.Retries.Remove(item.Info.Id);
        if (batch.Info.Status == BatchStatus.NeedsAttention)
        {
            batch.Info.Status = BatchStatus.Downloading;
            batch.Info.Message = null;
        }
        Save(batch);

        foreach (var item in failed)
        {
            CancelRetry(item.Info.Id);
            manager.Requeue(item);
            if (queue.IsRunning) queue.Pump();
            else manager.Start(item);
        }
        batch.Refresh(ItemsOf(batch));
    }

    /// <summary>Extracts now, even if the batch was made with extracting turned off. Does nothing while a part is running.</summary>
    public void ExtractNow(BatchViewModel batch)
    {
        if (_extracting.ContainsKey(batch.Info.Id) || ItemsOf(batch).Any(i => i.IsRunning)) return;
        batch.Info.Status = BatchStatus.Downloading;
        Evaluate(batch, forceExtract: true);
        Save(batch);
    }

    /// <summary>Forgets the batch. Its downloads stay in the list as normal downloads.</summary>
    public void Remove(BatchViewModel batch)
    {
        if (_extracting.TryGetValue(batch.Info.Id, out var cts)) cts.Cancel();
        _busyText.Remove(batch.Info.Id);
        Batches.Remove(batch);
        store.DeleteBatch(batch.Info.Id);
        foreach (var item in ItemsOf(batch))
        {
            CancelRetry(item.Info.Id);
            item.Info.BatchId = null;
            store.Save(item.Info);
        }
    }

    /// <summary>Stops the retry timers and any extraction. A cut-short extraction runs again on the next start.</summary>
    public void Shutdown()
    {
        foreach (var timer in _retryTimers.Values) timer.Stop();
        _retryTimers.Clear();
        foreach (var cts in _extracting.Values) cts.Cancel();
    }

    private void OnEnded(DownloadItemViewModel item)
    {
        if (Find(item.Info.BatchId) is not { } batch) return;

        var info = item.Info;
        if (info.Status == DownloadStatus.Completed && info.ExpectFile)
        {
            var kind = FileSniffer.SniffFile(info.FilePath);
            if (kind == FileKind.Html && !LooksLikePage(info.FileName))
                manager.MarkFailed(item, DownloadTask.HostPageMessage);
            else if (!FileSniffer.MatchesName(info.FileName, kind))
                manager.MarkFailed(item, $"{info.FileName} isn't a valid {ArchiveExtension(info.FileName)} file. " +
                                         "The host may have sent something else; check the link in your browser.");
        }
        Evaluate(batch);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)) return;
        var removedIds = e.OldItems?.OfType<DownloadItemViewModel>().Select(i => i.Info.Id).ToList() ?? [];
        foreach (var id in removedIds) CancelRetry(id);

        foreach (var batch in Batches.ToList())
        {
            if (ItemsOf(batch).Count == 0)
            {
                if (_extracting.TryGetValue(batch.Info.Id, out var cts)) cts.Cancel();
                Batches.Remove(batch);
                store.DeleteBatch(batch.Info.Id);
            }
            else
            {
                Evaluate(batch);
            }
        }
    }

    /// <summary>Asks the planner what's next for a downloading batch and does it; refreshes the status line either way.</summary>
    private void Evaluate(BatchViewModel batch, bool forceExtract = false)
    {
        var info = batch.Info;
        var items = ItemsOf(batch);
        if (info.Status == BatchStatus.Downloading)
        {
            foreach (var item in items) item.Refresh();
            var states = items.Select(i => new BatchItemState(i.Info.Id, i.Info.RequestedFileName ?? "", i.Info.FileName,
                i.Status, i.IsRunning, i.Info.ErrorIsTransient, i.Info.Error)).ToList();

            IReadOnlyList<BatchStep> steps;
            var autoExtract = info.AutoExtract;
            if (forceExtract) info.AutoExtract = true;
            try { steps = BatchPlanner.Decide(info, states); }
            finally { info.AutoExtract = autoExtract; }

            foreach (var step in steps)
            {
                switch (step)
                {
                    case BatchStep.Retry retry:
                        ScheduleRetry(batch, items.First(i => i.Info.Id == retry.ItemId), retry);
                        break;
                    case BatchStep.Extract extract:
                        _ = ExtractAsync(batch, extract.Sets);
                        break;
                    case BatchStep.Finish finish:
                        info.Status = BatchStatus.Done;
                        info.Message = finish.Message;
                        info.FinishedUtc = DateTime.UtcNow;
                        Save(batch);
                        Notify?.Invoke("Batch finished", $"{info.Name}: {finish.Message}", () => OpenFolder(info.Directory));
                        break;
                    case BatchStep.Attention attention:
                        NeedsAttention(batch, attention.Message);
                        break;
                }
            }
        }
        Refresh(batch);
    }

    private void ScheduleRetry(BatchViewModel batch, DownloadItemViewModel item, BatchStep.Retry retry)
    {
        var id = item.Info.Id;
        if (_retryTimers.ContainsKey(id)) return;

        batch.Info.Retries[id] = retry.Attempt;
        Save(batch);
        item.AddLog($"Batch: retry {retry.Attempt} of {BatchPlanner.RetryDelays.Count} in {Describe(retry.Delay)}");

        var timer = new DispatcherTimer { Interval = retry.Delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _retryTimers.Remove(id);
            if (item.Status != DownloadStatus.Failed || item.IsRunning || !manager.Items.Contains(item)) return;
            manager.Requeue(item);
            if (queue.IsRunning) queue.Pump();
            else manager.Start(item);
        };
        _retryTimers[id] = timer;
        timer.Start();
    }

    private void CancelRetry(long id)
    {
        if (!_retryTimers.Remove(id, out var timer)) return;
        timer.Stop();
    }

    private async Task ExtractAsync(BatchViewModel batch, IReadOnlyList<VolumeSet> sets)
    {
        var info = batch.Info;
        if (_extracting.ContainsKey(info.Id)) return;
        var cts = new CancellationTokenSource();
        _extracting[info.Id] = cts;
        info.Status = BatchStatus.Extracting;
        Save(batch);
        Refresh(batch);

        try
        {
            if (!sevenZip.IsInstalled)
            {
                SetBusy(batch, $"Setting up 7-Zip ({SevenZipTools.SetupSizeText}, once)…");
                try
                {
                    await sevenZip.EnsureAsync(
                        new Progress<ToolSetupProgress>(p => SetBusy(batch, "Setting up 7-Zip: " + VideoToolsService.Describe(p))),
                        cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Setting up 7-Zip failed");
                    NeedsAttention(batch, $"Couldn't set up 7-Zip: {ex.Message}");
                    return;
                }
            }

            var dir = info.Directory;
            foreach (var set in sets)
            {
                var output = ArchiveExtractor.ChooseOutputDirectory(dir, FileNames.Sanitize(set.BaseName));
                SetBusy(batch, $"Extracting {MultipartArchive.Describe(set)}…");
                var result = await ArchiveExtractor.ExtractAsync(sevenZip.SevenZipPath, Path.Combine(dir, set.FirstVolume!),
                    output, null, cts.Token);
                if (!result.Ok)
                {
                    // The parts stay, so the user can fix the problem and extract again.
                    NeedsAttention(batch, result.Error ?? "Extracting failed.");
                    return;
                }

                info.ExtractedTo.Add(output);
                Save(batch);
                if (info.DeleteParts) manager.RecycleFiles(set.Files.Select(f => Path.Combine(dir, f)));
            }

            info.Status = BatchStatus.Extracted;
            info.Message = null;
            info.FinishedUtc = DateTime.UtcNow;
            Save(batch);
            var folder = info.ExtractedTo[0];
            Notify?.Invoke("Extracted", $"{info.Name} → {folder}", () => OpenFolder(folder));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Closing the app: the status stays Extracting, so the next start extracts again.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Extracting batch {Id} failed", info.Id);
            NeedsAttention(batch, ex.Message);
        }
        finally
        {
            _extracting.Remove(info.Id);
            _busyText.Remove(info.Id);
            cts.Dispose();
            Refresh(batch);
        }
    }

    private void NeedsAttention(BatchViewModel batch, string message)
    {
        var info = batch.Info;
        var changed = info.Status != BatchStatus.NeedsAttention || info.Message != message;
        info.Status = BatchStatus.NeedsAttention;
        info.Message = message;
        Save(batch);
        if (changed) Notify?.Invoke("Batch needs attention", $"{info.Name}: {message}", WindowService.ShowMain);
    }

    private void SetBusy(BatchViewModel batch, string text)
    {
        _busyText[batch.Info.Id] = text;
        Refresh(batch);
    }

    private void Refresh(BatchViewModel batch) => batch.Refresh(ItemsOf(batch), _busyText.GetValueOrDefault(batch.Info.Id));

    /// <summary>Saves the batch unless it was removed meanwhile (an extraction can outlive its batch).</summary>
    private void Save(BatchViewModel batch)
    {
        if (Batches.Contains(batch)) store.SaveBatch(batch.Info);
    }

    private BatchViewModel? Find(long? id) => id is null ? null : Batches.FirstOrDefault(b => b.Info.Id == id);

    private List<DownloadItemViewModel> ItemsOf(BatchViewModel batch) =>
        manager.Items.Where(i => i.Info.BatchId == batch.Info.Id).ToList();

    /// <summary>The file name a link gives, or "" (let the server name it) when its last segment has no extension.</summary>
    private static string NameFromUrl(Uri uri)
    {
        var last = Uri.UnescapeDataString(uri.Segments[^1]).TrimEnd('/');
        return last.Length == 0 || !Path.HasExtension(last) ? "" : FileNames.Sanitize(last);
    }

    /// <summary>".rar" for x.part01.rar, ".7z" for x.7z.001, ".zip" for x.zip.</summary>
    private static string ArchiveExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && ext[1..].All(char.IsAsciiDigit) ? Path.GetExtension(Path.GetFileNameWithoutExtension(fileName)) : ext;
    }

    /// <summary>A page the user asked for by name (.htm/.html), which the engine also lets through.</summary>
    private static bool LooksLikePage(string fileName) =>
        fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} s";

    private static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        else WindowService.ShowMain();
    }
}
