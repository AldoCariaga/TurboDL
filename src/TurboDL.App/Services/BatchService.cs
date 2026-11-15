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
    private readonly SettingsService settings;
    private readonly ILogger<BatchService> log;

    /// <summary>
    /// Parts waiting for a free slot while the queue isn't running (when it runs, it starts them itself), oldest first.
    /// A part only starts when fewer than "downloads at once" are running, however it came to start.
    /// </summary>
    private readonly List<long> _waiting = [];

    /// <summary>Download id → the timer that retries it. One per download, so a retry is never scheduled twice.</summary>
    private readonly Dictionary<long, DispatcherTimer> _retryTimers = [];

    /// <summary>Batch id → its running extraction's cancel source. Its keys are the batches being extracted.</summary>
    private readonly Dictionary<long, CancellationTokenSource> _extracting = [];

    /// <summary>Batch id → what the extraction is doing right now, for the status line.</summary>
    private readonly Dictionary<long, string> _busyText = [];

    public BatchService(DownloadManager manager, QueueService queue, DownloadStore store, SevenZipService sevenZip,
        CategoryRules rules, SettingsService settings, ILogger<BatchService> log)
    {
        this.manager = manager;
        this.queue = queue;
        this.store = store;
        this.sevenZip = sevenZip;
        this.rules = rules;
        this.settings = settings;
        this.log = log;
        manager.Ended += OnEnded;
        manager.SourceReplaced += OnSourceReplaced;
        manager.BrowserDone += OnBrowserDone;
        manager.LinkChanged += OnLinkChanged;
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
        var name = BatchPlanner.SuggestName(urlNames, DateTime.Now);

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

    /// <summary>Links can be added while the batch is still downloading or waiting for the user, not once it extracts or is done.</summary>
    public bool CanAddTo(BatchViewModel batch) =>
        Batches.Contains(batch) && !_extracting.ContainsKey(batch.Info.Id)
        && batch.Info.Status is BatchStatus.Downloading or BatchStatus.NeedsAttention;

    /// <summary>The batch's downloads, for the Add links dialog's checks.</summary>
    public IReadOnlyList<DownloadItemViewModel> PartsOf(BatchViewModel batch) => ItemsOf(batch);

    /// <summary>
    /// Adds links to a batch that hasn't finished: one download per link in the batch's folder, like <see cref="Create"/>.
    /// Without headers from the dialog they take the ones the batch's parts were added with (not a browser's session).
    /// A batch that was stuck (a part missing) carries on, and extracts once every part is in.
    /// </summary>
    public void AddTo(BatchViewModel batch, BatchRequest request)
    {
        if (!CanAddTo(batch) || request.Urls.Count == 0) return;
        var info = batch.Info;
        var parts = ItemsOf(batch);
        var headers = request.Headers.Count > 0
            ? new Dictionary<string, string>(request.Headers)
            : parts.FirstOrDefault() is { } first ? new Dictionary<string, string>(first.Info.PageHeaders ?? first.Info.Headers) : [];

        foreach (var url in request.Urls)
        {
            var urlName = NameFromUrl(url);
            manager.Add(new DownloadInfo
            {
                Url = url.ToString(),
                FileName = urlName,
                Directory = info.Directory,
                Category = rules.CategoryFor(urlName is "" ? null : urlName),
                Connections = request.Connections,
                Headers = new(headers),
                ExpectFile = true,
                BatchId = info.Id,
                RequestedFileName = urlName,
            }, startNow: false);
        }

        if (info.Status == BatchStatus.NeedsAttention)
        {
            info.Status = BatchStatus.Downloading;
            info.Message = null;
        }
        batch.AddLog(request.Urls.Count == 1 ? "Added 1 link." : $"Added {request.Urls.Count} links.");
        Save(batch);
        if (request.StartNow) queue.Start();
        Evaluate(batch);
    }

    /// <summary>Starts the batch's failed parts again, with a fresh set of automatic retries.</summary>
    public void RetryFailed(BatchViewModel batch)
    {
        var failed = ItemsOf(batch).Where(i => i.Status == DownloadStatus.Failed && !i.IsBusy).ToList();
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
            StartPart(item);
        }
        batch.AddLog(failed.Count == 0 ? "Retry: no failed parts, so the batch is checked again." : $"Retrying {failed.Count} failed part(s).");
        // Nothing to retry (a missing part was got some other way): look again now, or the batch would sit there.
        if (failed.Count == 0) Evaluate(batch);
        batch.Refresh(ItemsOf(batch));
    }

    /// <summary>Pause all: parts waiting for a slot stop waiting. They stay queued, for the queue or a Resume.</summary>
    public void StopWaiting() => _waiting.Clear();

    /// <summary>Resume for batch parts: they start as slots free up, like the rest of the batch, not all at once.</summary>
    public void StartParts(IEnumerable<DownloadItemViewModel> items)
    {
        foreach (var item in items) StartPart(item);
    }

    /// <summary>Starts a part when there's a free slot: through the queue when it runs, otherwise in turn as downloads end.</summary>
    private void StartPart(DownloadItemViewModel item)
    {
        if (item.IsBusy || item.Status == DownloadStatus.Completed) return;
        manager.Requeue(item);
        if (queue.IsRunning)
        {
            queue.Pump();
            return;
        }
        if (!_waiting.Contains(item.Info.Id)) _waiting.Add(item.Info.Id);
        StartWaiting();
    }

    /// <summary>
    /// Starts waiting parts, oldest first, while fewer than "downloads at once" are busy: running, or in their browser
    /// step (a host page being clicked through still holds its slot).
    /// </summary>
    private void StartWaiting()
    {
        var free = Math.Max(1, settings.Current.MaxConcurrent) - manager.Items.Count(i => i.IsBusy);
        while (free > 0 && _waiting.Count > 0)
        {
            var id = _waiting[0];
            _waiting.RemoveAt(0);
            // Gone, started some other way, or paused/failed since: not waiting any more.
            if (manager.Items.FirstOrDefault(i => i.Info.Id == id) is not { IsBusy: false, Status: DownloadStatus.Queued } item)
                continue;
            manager.Start(item);
            free--;
        }
    }

    /// <summary>Extracts now, even if the batch was made with extracting turned off. Does nothing while a part is running.</summary>
    public void ExtractNow(BatchViewModel batch)
    {
        if (_extracting.ContainsKey(batch.Info.Id) || ItemsOf(batch).Any(i => i.IsRunning)) return;
        batch.AddLog("Extract now.");
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

    /// <summary>Any download ending frees a slot for a waiting part.</summary>
    private void OnEnded(DownloadItemViewModel item)
    {
        OnPartEnded(item);
        ResumeBatchesMissing(item);
        StartWaiting();
    }

    private void OnPartEnded(DownloadItemViewModel item)
    {
        if (Find(item.Info.BatchId) is not { } batch) return;

        var info = item.Info;
        if (info.Status == DownloadStatus.Completed && info.ExpectFile)
        {
            var kind = FileSniffer.SniffFile(info.FilePath);
            if (kind == FileKind.Html && !LooksLikePage(info.FileName))
            {
                batch.AddLog($"{info.FileName}: the host sent a web page, not the file; solving it in the browser.");
                manager.MarkFailed(item, DownloadTask.HostPageMessage);
                _ = manager.OpenInBrowserAsync(item);
            }
            else if (!FileSniffer.MatchesName(info.FileName, kind))
            {
                batch.AddLog($"{info.FileName}: downloaded, but it isn't a valid {ArchiveExtension(info.FileName)} file.");
                manager.MarkFailed(item, $"{info.FileName} isn't a valid {ArchiveExtension(info.FileName)} file. " +
                                         "The host may have sent something else; check the link in your browser.");
            }
        }
        else if (info.Status == DownloadStatus.Failed && !item.IsBusy)
        {
            batch.AddLog($"{PartName(item)} failed: {info.Error}");
        }
        else if (info.Status == DownloadStatus.Failed && item.InBrowser)
        {
            batch.AddLog($"{PartName(item)}: {info.Error} Solving it in the browser.");
        }
        Evaluate(batch);
    }

    /// <summary>
    /// A part the browser found a direct link for runs again with a fresh set of automatic retries, so a batch that was
    /// waiting on it goes back to downloading (and extracts when it's done), as after Retry failed parts. The manager
    /// then starts it in the slot its browser step held.
    /// </summary>
    private void OnSourceReplaced(DownloadItemViewModel item)
    {
        if (Find(item.Info.BatchId) is not { } batch) return;
        batch.AddLog($"{PartName(item)}: the browser found the file; downloading it.");
        CancelRetry(item.Info.Id);
        batch.Info.Retries.Remove(item.Info.Id);
        if (batch.Info.Status == BatchStatus.NeedsAttention)
        {
            batch.Info.Status = BatchStatus.Downloading;
            batch.Info.Message = null;
        }
        Save(batch);
        batch.Refresh(ItemsOf(batch));
    }

    /// <summary>
    /// The user gave a part a new link: it gets a fresh set of automatic retries, and a batch that was stuck on it goes
    /// back to downloading. It waits in the queue like the other parts.
    /// </summary>
    private void OnLinkChanged(DownloadItemViewModel item)
    {
        if (Find(item.Info.BatchId) is not { } batch) return;
        batch.AddLog($"{PartName(item)}: link changed to {item.Info.Url}");
        CancelRetry(item.Info.Id);
        batch.Info.Retries.Remove(item.Info.Id);
        if (batch.Info.Status == BatchStatus.NeedsAttention)
        {
            batch.Info.Status = BatchStatus.Downloading;
            batch.Info.Message = null;
        }
        Save(batch);
        Evaluate(batch);
    }

    /// <summary>
    /// A part out of its browser step without a link (closed, paused): its slot is free for the next one, and the batch
    /// is looked at again, since it may now be all failed.
    /// </summary>
    private void OnBrowserDone(DownloadItemViewModel item)
    {
        if (Find(item.Info.BatchId) is { } batch)
        {
            if (!item.IsRunning)
                batch.AddLog($"{PartName(item)}: left the browser without the file ({(item.Status == DownloadStatus.Paused ? "paused" : "closed")}).");
            Evaluate(batch);
        }
        StartWaiting();
    }

    /// <summary>A part's name for the log: the name it was added with, else its file name, else its link.</summary>
    private static string PartName(DownloadItemViewModel item) =>
        item.Info.RequestedFileName is { Length: > 0 } requested ? requested
        : item.Info.FileName.Length > 0 ? item.Info.FileName
        : item.Info.Url;

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

    /// <summary>
    /// The volumes a batch's sets are missing that are in its folder anyway, got some other way (downloaded separately
    /// after the part was removed from the batch, or copied there). Each has to look like the archive its name says,
    /// the same check a downloaded part gets. 7-Zip finds them there by name when it extracts.
    /// </summary>
    private static List<string> VolumesAlreadyInFolder(BatchInfo info, IReadOnlyList<DownloadItemViewModel> items)
    {
        var found = new List<string>();
        foreach (var set in MultipartArchive.Group(items.Select(i => i.Info.FileName)))
        {
            foreach (var number in set.Missing)
            {
                var name = MultipartArchive.VolumeName(set, number);
                var path = Path.Combine(info.Directory, name);
                if (File.Exists(path) && FileSniffer.MatchesName(name, FileSniffer.SniffFile(path))) found.Add(name);
            }
        }
        return found;
    }

    /// <summary>
    /// A download outside any batch finished as a volume a stuck batch in the same folder was missing: the batch picks up
    /// again (and extracts if that was the last gap).
    /// </summary>
    private void ResumeBatchesMissing(DownloadItemViewModel item)
    {
        var info = item.Info;
        if (info.BatchId is not null || info.Status != DownloadStatus.Completed || info.FileName.Length == 0
            || string.IsNullOrWhiteSpace(info.Directory)) return;
        foreach (var batch in Batches.Where(b => b.Info.Status == BatchStatus.NeedsAttention
                                                 && string.Equals(Path.GetFullPath(b.Info.Directory), Path.GetFullPath(info.Directory),
                                                     StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var missing = MultipartArchive.Group(ItemsOf(batch).Select(i => i.Info.FileName))
                .SelectMany(set => set.Missing.Select(n => MultipartArchive.VolumeName(set, n)));
            if (!missing.Contains(info.FileName, StringComparer.OrdinalIgnoreCase)) continue;
            batch.AddLog($"{info.FileName}, which this batch was missing, finished as a separate download; carrying on.");
            batch.Info.Status = BatchStatus.Downloading;
            batch.Info.Message = null;
            Evaluate(batch);
            Save(batch);
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
                i.Status, i.IsBusy, i.Info.ErrorIsTransient, i.Info.Error)).ToList(); // in the browser = not done yet

            IReadOnlyList<BatchStep> steps;
            var autoExtract = info.AutoExtract;
            if (forceExtract) info.AutoExtract = true;
            var inFolder = VolumesAlreadyInFolder(info, items);
            try { steps = BatchPlanner.Decide(info, states, inFolder); }
            finally { info.AutoExtract = autoExtract; }
            if (inFolder.Count > 0 && steps.Any(s => s is BatchStep.Extract or BatchStep.Finish))
                batch.AddLog($"Using {string.Join(", ", inFolder)}, already in the folder (got some other way).");

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
                        batch.AddLog(finish.Message);
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
        batch.AddLog($"{PartName(item)}: retry {retry.Attempt} of {BatchPlanner.RetryDelays.Count} in {Describe(retry.Delay)}.");

        var timer = new DispatcherTimer { Interval = retry.Delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _retryTimers.Remove(id);
            if (item.Status != DownloadStatus.Failed || item.IsBusy || !manager.Items.Contains(item)) return;
            StartPart(item);
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
                batch.AddLog($"Setting up 7-Zip ({SevenZipTools.SetupSizeText}, first time only).");
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
                    batch.AddLog($"Couldn't set up 7-Zip: {ex.Message}");
                    NeedsAttention(batch, $"Couldn't set up 7-Zip: {ex.Message}");
                    return;
                }
            }

            var dir = info.Directory;
            var all = Stopwatch.StartNew();
            // One folder for the whole batch: every set goes into dir\Extracted (or "Extracted (2)" when that one is in
            // use, such as after an extraction cut short by closing the app). 7-Zip renames a name that clashes (-aou).
            var output = ArchiveExtractor.ChooseOutputDirectory(dir, "Extracted");
            batch.AddLog(sets.Count == 1 ? $"Extracting into {output}" : $"Extracting {sets.Count} archives into {output}, one after another.");
            for (var index = 0; index < sets.Count; index++)
            {
                var set = sets[index];
                var label = MultipartArchive.Describe(set);
                // The percentage first: the sidebar is narrow, and the archive's name is the part that can be cut off.
                var of = sets.Count > 1 ? $" · {index + 1} of {sets.Count}" : "";
                batch.SetProgress(0);
                SetBusy(batch, $"0%{of} · {label}");
                if (sets.Count > 1) batch.AddLog($"{label}: extracting.");

                // 7-Zip reports from a background thread; Progress posts here. A late report must not touch the next archive.
                var current = true;
                var progress = new Progress<ExtractProgress>(p =>
                {
                    if (!current) return;
                    batch.SetProgress(p.Percent / 100.0);
                    SetBusy(batch, $"{p.Percent}%{of} · {label}");
                });
                var watch = Stopwatch.StartNew();
                ExtractResult result;
                try
                {
                    result = await ArchiveExtractor.ExtractAsync(sevenZip.SevenZipPath, Path.Combine(dir, set.FirstVolume!),
                        output, null, cts.Token, progress);
                }
                finally
                {
                    current = false;
                }
                if (!result.Ok)
                {
                    // The parts stay, so the user can fix the problem and extract again.
                    batch.AddLog($"{label}: failed after {Took(watch.Elapsed)}. {result.Error}");
                    NeedsAttention(batch, result.Error ?? "Extracting failed.");
                    return;
                }
                batch.AddLog(result.Warnings.Count == 0
                    ? $"{label}: done in {Took(watch.Elapsed)}."
                    : $"{label}: done in {Took(watch.Elapsed)}, with {result.Warnings.Count} warning(s) from 7-Zip: {result.Warnings[0]}");

                if (!info.ExtractedTo.Contains(output, StringComparer.OrdinalIgnoreCase)) info.ExtractedTo.Add(output);
                Save(batch);
                if (info.DeleteParts)
                {
                    manager.RecycleFiles(set.Files.Select(f => Path.Combine(dir, f)));
                    batch.AddLog($"{label}: its {set.Files.Count} part(s) went to the Recycle Bin.");
                }
            }

            MoveLooseFiles(batch, dir, output);

            info.Status = BatchStatus.Extracted;
            info.Message = null;
            info.FinishedUtc = DateTime.UtcNow;
            Save(batch);
            batch.AddLog($"All extracted in {Took(all.Elapsed)}.");
            Notify?.Invoke("Extracted", $"{info.Name} → {output}", () => OpenFolder(output));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Closing the app: the status stays Extracting, so the next start extracts again.
            batch.AddLog("Extracting stopped (TurboDL is closing); it starts again next time.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Extracting batch {Id} failed", info.Id);
            batch.AddLog($"Extracting failed: {ex.Message}");
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
        if (!changed) return;
        batch.AddLog("Needs attention: " + message);
        Notify?.Invoke("Batch needs attention", $"{info.Name}: {message}", WindowService.ShowMain);
    }

    /// <summary>
    /// Moves the batch's finished downloads that aren't archives (a repack's loose .bin files, a readme) from the batch
    /// folder into <paramref name="output"/>, next to what the archives held, so that one folder has everything. A name
    /// that's taken there gets " (2)". Each row is pointed at the file's new place. A file that can't be moved (in use)
    /// stays where it is, and the log says so; it doesn't stop the batch.
    /// </summary>
    private void MoveLooseFiles(BatchViewModel batch, string dir, string output)
    {
        var moved = new List<string>();
        foreach (var item in ItemsOf(batch))
        {
            var info = item.Info;
            if (info.Status != DownloadStatus.Completed || info.FileName.Length == 0
                || MultipartArchive.TryParse(info.FileName, out _, out _, out _)
                || !string.Equals(Path.GetFullPath(info.Directory), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(info.FilePath)) continue;
            try
            {
                Directory.CreateDirectory(output);
                var name = FileNames.MakeUnique(output, info.FileName);
                File.Move(info.FilePath, Path.Combine(output, name));
                info.Directory = output;
                info.FileName = name;
                store.Save(info);
                item.Refresh();
                moved.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                batch.AddLog($"{info.FileName} couldn't be moved into {output}: {ex.Message}");
            }
        }
        if (moved.Count > 0)
            batch.AddLog($"Moved {moved.Count} file(s) that aren't archives into {output}: {string.Join(", ", moved)}.");
    }

    /// <summary>"12 s", "4 min 05 s", "1 h 02 min" for the log.</summary>
    private static string Took(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
        : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";

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
