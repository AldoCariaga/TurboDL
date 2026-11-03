using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;
using TurboDL.Core.Model;

namespace TurboDL.Core.Engine;

/// <summary>
/// Runs one HTTP download with several connections. Each connection owns a byte range and writes it
/// straight into a pre-sized "&lt;name&gt;.turbodl" file at its own offset, so there is no merge step.
/// When a connection finishes early it takes the second half of the biggest range still running
/// (dynamic splitting); when nothing is big enough to split it stays around and takes over any part
/// that has stopped making progress, so one stuck connection can't hold up the end of the download.
///
/// Cancelling the token pauses: progress is saved in <see cref="DownloadInfo.Segments"/> and the next
/// <see cref="RunAsync"/> continues from there.
/// </summary>
public sealed class DownloadTask
{
    private sealed class Segment
    {
        public long Start;
        public long End;
        public long Position;

        /// <summary>Worker id that owns this part; 0 = nobody. Only the owner may advance <see cref="Position"/>.</summary>
        public int Owner;
        public CancellationTokenSource? OwnerCts;
        public long LastProgress; // Stopwatch timestamp of the last write (or of claiming it)
        public bool GotBytes;     // has the current owner received anything yet?

        public bool OpenEnded => End == long.MaxValue;
        public bool IsComplete => Position > End;
        public long Remaining => OpenEnded ? long.MaxValue : End - Position + 1;
    }

    private readonly DownloadInfo _info;
    private readonly HttpClient _http;
    private readonly DownloadOptions _opt;
    private readonly RateLimiter? _globalLimiter;
    private readonly RateLimiter _localLimiter;

    private readonly Lock _gate = new();
    private List<Segment> _segments = [];
    private bool _segmentsLoaded;
    private Exception? _failure;
    private long _transferred;
    private int _activeConnections;
    private int _liveWorkers; // worker loops still running; guarded by _gate
    private double _speed;

    public DownloadTask(DownloadInfo info, HttpClient http, DownloadOptions? options = null, RateLimiter? globalLimiter = null)
    {
        _info = info;
        _http = http;
        _opt = options ?? new DownloadOptions();
        // A split must never land inside a buffer that is being written (see AcquireWork).
        if (_opt.MinSegmentSize < _opt.BufferSize * 2L)
            _opt = _opt with { MinSegmentSize = _opt.BufferSize * 2L };
        _globalLimiter = globalLimiter;
        _localLimiter = new RateLimiter(info.SpeedLimit ?? 0);
    }

    public DownloadInfo Info => _info;

    /// <summary>Raised about once a second, and on pause/finish/failure, with <see cref="Info"/> up to date. Save it here.</summary>
    public event Action<DownloadInfo>? Checkpoint;

    /// <summary>Human-readable diagnostics: retries, splits. Shown in the Details window.</summary>
    public event Action<string>? Log;

    /// <summary>Change this download's own speed cap while it runs (0 = unlimited).</summary>
    public void SetSpeedLimit(long bytesPerSecond)
    {
        _info.SpeedLimit = bytesPerSecond > 0 ? bytesPerSecond : null;
        _localLimiter.BytesPerSecond = bytesPerSecond;
    }

    public DownloadSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_segmentsLoaded)
                return new DownloadSnapshot(_info.Downloaded, _info.TotalSize, 0, 0, _info.Segments.ToList());

            var segs = _segments.Select(ToState).ToList();
            var downloaded = segs.Sum(s => s.Position - s.Start);
            return new DownloadSnapshot(downloaded, _info.TotalSize, Volatile.Read(ref _speed), _activeConnections, segs);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _info.Status = DownloadStatus.Paused;
            SyncState();
            Checkpoint?.Invoke(_info);
            throw;
        }
        catch (Exception ex)
        {
            _info.Status = DownloadStatus.Failed;
            _info.Error = ex.Message;
            SyncState();
            _info.ErrorIsTransient = IsTransientFailure(ex);
            Checkpoint?.Invoke(_info);
            if (ex is DownloadFailedException) ExceptionDispatchInfo.Throw(ex);
            throw new DownloadFailedException(ex.Message, ex);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        _failure = null;
        _info.Error = null;
        _info.ErrorIsTransient = false;
        _info.Status = DownloadStatus.Downloading;

        await PrepareAsync(ct);

        lock (_gate)
        {
            _segments = _info.Segments
                .Select(s => new Segment { Start = s.Start, End = s.End, Position = s.Position })
                .ToList();
            _segmentsLoaded = true;
        }

        using (var handle = File.OpenHandle(_info.TempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                   FileShare.Read, FileOptions.Asynchronous))
        {
            if (_info.TotalSize is long size && RandomAccess.GetLength(handle) != size)
                RandomAccess.SetLength(handle, size);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var monitorCts = new CancellationTokenSource();

            var workerCount = _info.SupportsRanges ? Math.Clamp(_info.Connections, 1, 32) : 1;
            lock (_gate) _liveWorkers = workerCount;
            var workers = Enumerable.Range(1, workerCount).Select(id => WorkerAsync(id, handle, linked)).ToArray();
            var monitor = MonitorAsync(monitorCts.Token);

            await Task.WhenAll(workers);
            monitorCts.Cancel();
            await monitor;
        }

        SyncState();
        if (_failure is not null) ExceptionDispatchInfo.Throw(_failure);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_segments.Any(s => !s.IsComplete))
                throw new DownloadFailedException("Download ended before all parts were complete.");
            if (_info.TotalSize is null)
                _info.TotalSize = _segments.Sum(s => s.Position - s.Start);
        }

        var temp = _info.TempPath;
        var target = File.Exists(_info.FilePath) ? FileNames.MakeUnique(_info.Directory, _info.FileName) : _info.FileName;
        File.Move(temp, Path.Combine(_info.Directory, target));
        _info.FileName = target;
        _info.Status = DownloadStatus.Completed;
        _info.CompletedUtc = DateTime.UtcNow;
        Volatile.Write(ref _speed, 0);
        Checkpoint?.Invoke(_info);
    }

    /// <summary>Probe the URL, then either validate a resume or lay out fresh segments.</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        var probe = await ProbeWithRetryAsync(ct);
        if (_info.ExpectFile && UrlProbe.IsWebPage(probe.ContentType) && !LooksLikePageName(_info.FileName, probe.FileName))
            throw new HostReturnedPageException(HostPageMessage);

        if (string.IsNullOrWhiteSpace(_info.Directory))
            throw new DownloadFailedException("No download folder was set.");
        Directory.CreateDirectory(_info.Directory);

        var resuming = _info.Segments.Count > 0 && !string.IsNullOrEmpty(_info.FileName) && File.Exists(_info.TempPath);
        if (resuming)
        {
            var changed =
                (_info.TotalSize is not null && probe.Length != _info.TotalSize)
                || (_info.ETag is not null && probe.ETag is not null && _info.ETag != probe.ETag)
                || (_info.LastModified is not null && probe.LastModified is not null && _info.LastModified != probe.LastModified);
            if (changed)
                throw new RemoteFileChangedException("The file on the server has changed since this download started.");

            // Without range support a partial file is useless; start over.
            if (!probe.SupportsRanges || !_info.SupportsRanges) resuming = false;
        }

        if (!resuming && _info.Segments.Count > 0 && !string.IsNullOrEmpty(_info.FileName) && File.Exists(_info.TempPath))
            File.Delete(_info.TempPath); // our own leftover from an earlier attempt

        _info.FinalUrl = probe.FinalUrl.ToString();
        _info.TotalSize = probe.Length;
        _info.SupportsRanges = probe.SupportsRanges && probe.Length is not null;
        _info.ETag = probe.ETag;
        _info.LastModified = probe.LastModified;

        if (!resuming)
        {
            var name = string.IsNullOrWhiteSpace(_info.FileName) ? probe.FileName : FileNames.Sanitize(_info.FileName);
            _info.FileName = FileNames.MakeUnique(_info.Directory, name);
            _info.Segments = CreateInitialSegments();
        }
    }

    /// <summary>The error shown when <see cref="DownloadInfo.ExpectFile"/> is set and the host sends a web page.</summary>
    public const string HostPageMessage =
        "The host sent a web page instead of the file. The link may have expired, or the site wants you to sign in " +
        "or solve a CAPTCHA. Open the link in your browser, then add it again with your session cookie.";

    /// <summary>True when the requested name (or, if none was given, the server's name) is itself a web page.</summary>
    private static bool LooksLikePageName(string requested, string probed)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? probed : requested;
        return name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProbeResult> ProbeWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                try
                {
                    return await UrlProbe.ProbeAsync(_http, _info.Url, _info.Headers, ct);
                }
                catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
                {
                    // HttpClient reports a connect timeout as a cancellation.
                    throw new TimeoutException("Could not connect to the server.", oce);
                }
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && attempt <= _opt.MaxRetries)
            {
                Log?.Invoke($"Probe: {ex.GetBaseException().Message} Retry {attempt}.");
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }
    }

    private TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromTicks(Math.Min(_opt.RetryMaxDelay.Ticks, _opt.RetryBaseDelay.Ticks * (1L << Math.Min(attempt - 1, 20))));

    private List<SegmentState> CreateInitialSegments()
    {
        if (_info.TotalSize is not long size)
            return [new SegmentState { Start = 0, End = long.MaxValue, Position = 0 }];
        if (size == 0) return [];
        if (!_info.SupportsRanges)
            return [new SegmentState { Start = 0, End = size - 1, Position = 0 }];

        var count = (int)Math.Clamp(size / _opt.MinSegmentSize, 1, Math.Clamp(_info.Connections, 1, 32));
        var chunk = size / count;
        var list = new List<SegmentState>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * chunk;
            var end = i == count - 1 ? size - 1 : start + chunk - 1;
            list.Add(new SegmentState { Start = start, End = end, Position = start });
        }
        return list;
    }

    private enum Outcome
    {
        /// <summary>Part finished; look for more work.</summary>
        Done,
        /// <summary>Server limits connections; this worker stops for good.</summary>
        Yielded,
        /// <summary>Another worker took this part over (it had stalled); look for more work.</summary>
        Lost,
    }

    private async Task WorkerAsync(int id, SafeFileHandle handle, CancellationTokenSource run)
    {
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested();
                var (seg, claim, finished, stolenFrom) = AcquireWork(id, run.Token);
                if (finished) return;

                if (stolenFrom is not null)
                {
                    try { stolenFrom.Cancel(); } catch (ObjectDisposedException) { /* owner already let go */ }
                    Log?.Invoke($"Part @{seg!.Start:N0} stalled; another connection took it over.");
                }

                if (seg is null)
                {
                    // Nothing free right now, but parts are still running: stay around to split or rescue them.
                    await Task.Delay(IdlePoll, run.Token);
                    continue;
                }

                Outcome outcome;
                Interlocked.Increment(ref _activeConnections);
                try
                {
                    outcome = await DownloadSegmentAsync(seg, id, handle, claim!.Token, run.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeConnections);
                    lock (_gate)
                    {
                        if (seg.Owner == id)
                        {
                            seg.Owner = 0;
                            seg.OwnerCts = null;
                        }
                    }
                    claim!.Dispose();
                }

                if (outcome == Outcome.Yielded) return;
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate) _failure ??= ex;
            run.Cancel();
        }
    }

    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Picks work for an idle worker, in order: an unclaimed part; the back half of the biggest running part;
    /// a part that has stalled (taken over). Returns finished=true when every part is complete.
    /// </summary>
    private (Segment? Seg, CancellationTokenSource? Claim, bool Finished, CancellationTokenSource? StolenFrom)
        AcquireWork(int id, CancellationToken run)
    {
        lock (_gate)
        {
            var pending = _segments.FirstOrDefault(s => s.Owner == 0 && !s.IsComplete);
            if (pending is not null) return (pending, Claim(pending, id, run), false, null);

            if (!_segments.Any(s => !s.IsComplete))
            {
                _liveWorkers--;
                return (null, null, true, null);
            }

            // Without ranges there is one connection and nothing to share.
            if (!_info.SupportsRanges) return (null, null, false, null);

            var running = _segments.Where(s => s.Owner != 0 && !s.IsComplete).ToList();

            var biggest = running.MaxBy(s => s.Remaining);
            if (biggest is not null && biggest.Remaining >= 2 * _opt.MinSegmentSize)
            {
                // mid is at least MinSegmentSize (>= 2 buffers) past Position, so the writer of `biggest`
                // can never be in the middle of writing past the new End.
                var mid = biggest.Position + biggest.Remaining / 2;
                var piece = new Segment { Start = mid, Position = mid, End = biggest.End };
                biggest.End = mid - 1;
                _segments.Insert(_segments.IndexOf(biggest) + 1, piece);
                return (piece, Claim(piece, id, run), false, null);
            }

            var now = Stopwatch.GetTimestamp();
            var stalled = running
                .Where(s => Stopwatch.GetElapsedTime(s.LastProgress, now)
                            >= (s.GotBytes ? _opt.StealAfter : _opt.StealAfter + _opt.ConnectGrace))
                .MaxBy(s => s.Remaining);
            if (stalled is not null)
            {
                var old = stalled.OwnerCts;
                return (stalled, Claim(stalled, id, run), false, old);
            }

            return (null, null, false, null);
        }
    }

    // Caller holds _gate.
    private static CancellationTokenSource Claim(Segment seg, int id, CancellationToken run)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(run);
        seg.Owner = id;
        seg.OwnerCts = cts;
        seg.LastProgress = Stopwatch.GetTimestamp();
        seg.GotBytes = false;
        return cts;
    }

    /// <summary>Downloads one part, retrying network errors, until it's done, given up, or taken over.</summary>
    private async Task<Outcome> DownloadSegmentAsync(
        Segment seg, int id, SafeFileHandle handle, CancellationToken claim, CancellationToken run)
    {
        var attempt = 0;
        while (true)
        {
            long before;
            lock (_gate)
            {
                if (seg.Owner != id) return Outcome.Lost;
                if (seg.IsComplete) return Outcome.Done;
                before = seg.Position;
            }

            try
            {
                await FetchAsync(seg, id, handle, claim);
                lock (_gate)
                {
                    if (seg.Owner != id) return Outcome.Lost;
                    if (seg.IsComplete) return Outcome.Done;
                    if (seg.OpenEnded)
                    {
                        seg.End = seg.Position - 1; // unknown size: end of stream is end of file
                        return Outcome.Done;
                    }
                }
                throw new IOException("Connection closed before this part finished.");
            }
            catch (OperationCanceledException) when (claim.IsCancellationRequested && !run.IsCancellationRequested)
            {
                return Outcome.Lost;
            }
            catch (Exception ex) when (IsTransient(ex) && !claim.IsCancellationRequested)
            {
                var live = -1;
                lock (_gate)
                {
                    if (seg.Owner != id) return Outcome.Lost;
                    if (seg.Position > before) attempt = 0;
                    if (!_info.SupportsRanges) seg.Position = seg.Start; // can't continue mid-file

                    // Server says "too many connections": drop this one rather than hammer it. The last one never quits.
                    if (ex is TransientHttpException { IsBusy: true } && _liveWorkers > 1)
                        live = --_liveWorkers;
                }
                if (live > 0)
                {
                    Log?.Invoke($"Server is limiting connections (HTTP {((TransientHttpException)ex).StatusCode}); continuing with {live}.");
                    return Outcome.Yielded;
                }

                if (++attempt > _opt.MaxRetries)
                    throw new DownloadFailedException($"Gave up after {_opt.MaxRetries} retries: {ex.Message}", ex);

                var delay = RetryDelay(attempt);
                if (ex is TransientHttpException { RetryAfter: { } after } && after > delay)
                    delay = after < _opt.RetryMaxDelay * 4 ? after : _opt.RetryMaxDelay * 4;
                Log?.Invoke($"Part @{seg.Start:N0}: {ex.GetBaseException().Message} Retry {attempt} in {delay.TotalSeconds:0.#}s.");
                try
                {
                    await Task.Delay(delay, claim);
                }
                catch (OperationCanceledException) when (!run.IsCancellationRequested)
                {
                    return Outcome.Lost;
                }
            }
        }
    }

    private async Task FetchAsync(Segment seg, int id, SafeFileHandle handle, CancellationToken ct)
    {
        long from, to;
        lock (_gate)
        {
            if (seg.Owner != id) return;
            from = seg.Position;
            to = seg.End;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _info.Url);
        TurboHttp.ApplyHeaders(request, _info.Headers);
        if (_info.SupportsRanges)
            request.Headers.Range = new RangeHeaderValue(from, to == long.MaxValue ? null : to);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_opt.ReadTimeout);
        var buffer = ArrayPool<byte>.Shared.Rent(_opt.BufferSize);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            var code = (int)response.StatusCode;
            if (code is 408 or 429 or >= 500) throw TransientHttpException.From(response);
            if (!response.IsSuccessStatusCode)
                throw new ServerRefusedException(code, $"Server returned HTTP {code} {response.ReasonPhrase}.");
            if (_info.SupportsRanges && response.StatusCode != HttpStatusCode.PartialContent)
                throw new DownloadFailedException("Server stopped accepting range requests.");

            await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
            while (true)
            {
                stall.CancelAfter(_opt.ReadTimeout);
                var read = await stream.ReadAsync(buffer.AsMemory(0, _opt.BufferSize), stall.Token);
                if (read == 0) return;

                long writeAt;
                int take;
                lock (_gate)
                {
                    if (seg.Owner != id) return; // taken over while we waited on the network
                    writeAt = seg.Position;
                    take = (int)Math.Min(read, seg.Remaining);
                }

                if (take > 0)
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, take), writeAt, ct);
                    lock (_gate)
                    {
                        // If we lost the part mid-write, the new owner restarts from the old Position and rewrites
                        // these same bytes, so only the owner may advance it.
                        if (seg.Owner != id) return;
                        seg.Position += take;
                        seg.LastProgress = Stopwatch.GetTimestamp();
                        seg.GotBytes = true;
                    }
                    Interlocked.Add(ref _transferred, take);
                }

                // The range shrank (split) or we reached the end; the rest of this response belongs to someone else.
                lock (_gate)
                {
                    if (seg.IsComplete) return;
                }

                await _localLimiter.WaitAsync(take, ct);
                if (_globalLimiter is not null) await _globalLimiter.WaitAsync(take, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Connection timed out or stalled.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or TransientHttpException
        || (ex is IOException && !IsBadPath(ex));

    private const int ErrorInvalidName = unchecked((int)0x8007007B);
    private const int ErrorBadPathname = unchecked((int)0x800700A1);

    /// <summary>
    /// True when the folder or file name itself can't be used (a bad character, a malformed or too long path). Trying
    /// again won't change that, unlike a full disk, a file in use or a dropped network share.
    /// </summary>
    private static bool IsBadPath(Exception ex) =>
        ex is PathTooLongException || ex.HResult is ErrorInvalidName or ErrorBadPathname;

    /// <summary>Whether a failure that ended the download is worth trying again later.</summary>
    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        HostReturnedPageException or RemoteFileChangedException => false,
        _ when IsTransient(ex) => true,
        DownloadFailedException { InnerException: { } inner } when IsTransient(inner) => true, // "Gave up after N retries"
        _ => false,
    };

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_opt.ProgressInterval);
        var lastBytes = Interlocked.Read(ref _transferred);
        var lastTime = Stopwatch.GetTimestamp();
        var lastCheckpoint = lastTime;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Stopwatch.GetTimestamp();
                var bytes = Interlocked.Read(ref _transferred);
                var seconds = Stopwatch.GetElapsedTime(lastTime, now).TotalSeconds;
                if (seconds > 0)
                {
                    var instant = (bytes - lastBytes) / seconds;
                    var old = Volatile.Read(ref _speed);
                    Volatile.Write(ref _speed, old == 0 ? instant : old * 0.7 + instant * 0.3);
                }
                lastBytes = bytes;
                lastTime = now;

                if (Stopwatch.GetElapsedTime(lastCheckpoint, now) >= _opt.CheckpointInterval)
                {
                    lastCheckpoint = now;
                    SyncState();
                    Checkpoint?.Invoke(_info);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Copy live segment progress into <see cref="DownloadInfo.Segments"/> so it can be saved.</summary>
    private void SyncState()
    {
        lock (_gate)
        {
            if (_segmentsLoaded) _info.Segments = _segments.Select(ToState).ToList();
        }
    }

    private static SegmentState ToState(Segment s) => new() { Start = s.Start, End = s.End, Position = s.Position };
}
