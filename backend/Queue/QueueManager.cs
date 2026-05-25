using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueManager : IDisposable
{
    private InProgressQueueItem? _inProgressQueueItem;

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();

    // Reserve threadpool headroom for non-queue work (SAB API polls, WebDAV,
    // browser settings PUTs) by pausing the queue when pending tasks pile
    // up. Override via env QUEUE_BACKPRESSURE_THRESHOLD. Default 100 is well
    // above quiet-system baseline (~10-40 pending) and well below the
    // runaway state (400-1000+) we've observed under saturation.
    private static readonly int QueueBackpressureThreshold =
        int.TryParse(Environment.GetEnvironmentVariable("QUEUE_BACKPRESSURE_THRESHOLD"), out var v) && v > 0
            ? v
            : 100;

    // How long an in-flight queue item is allowed to report ZERO progress
    // before we treat it as stuck and cancel it. Codex pre-merge review
    // rejected an earlier fixed-wall-clock timeout because it would kill
    // legitimately slow downloads (huge Blu-rays, season packs with full
    // article-existence health checks) that take longer than the budget
    // while still making progress. Progress-based detection only fires
    // when the item has genuinely stalled (no progress update from any
    // phase for the threshold window), so long-but-progressing items
    // run to completion. Override via env QUEUE_ITEM_STUCK_MINUTES.
    //
    // Note: MarkQueueItemCompleted (the DB-aggregator-and-save step) does
    // not report progress, so step 4 effectively starts a "stuck" window.
    // For typical NZBs that step completes in well under the threshold;
    // for huge NZBs where step 4 legitimately takes longer than the
    // threshold, operators should bump QUEUE_ITEM_STUCK_MINUTES.
    private static readonly TimeSpan QueueItemStuckThreshold =
        int.TryParse(Environment.GetEnvironmentVariable("QUEUE_ITEM_STUCK_MINUTES"), out var qt) && qt > 0
            ? TimeSpan.FromMinutes(qt)
            : TimeSpan.FromMinutes(5);

    private static readonly TimeSpan QueueItemStuckCheckInterval = TimeSpan.FromSeconds(30);


    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }

    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        return (_inProgressQueueItem?.QueueItem, _inProgressQueueItem?.ProgressPercentage);
    }

    public void AwakenQueue(DateTime? dateTime = null)
    {
        TimeSpan? cancelAfter = dateTime.HasValue ? (dateTime.Value - DateTime.Now) : null;
        lock (_sleepingQueueLock)
        {
            if (cancelAfter.HasValue && cancelAfter.Value > TimeSpan.Zero)
                _sleepingQueueToken.CancelAfter(cancelAfter.Value);
            else
                _sleepingQueueToken.Cancel();
        }
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        await LockAsync(async () =>
        {
            var inProgressId = _inProgressQueueItem?.QueueItem?.Id;
            if (inProgressId is not null && queueItemIds.Contains(inProgressId.Value))
            {
                await _inProgressQueueItem!.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await _inProgressQueueItem.ProcessingTask.ConfigureAwait(false);
                _inProgressQueueItem = null;
            }

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Pause the queue while every NNTP provider is in circuit-breaker
                // cooldown. Without this, each queue item runs FetchFirstSegmentsStep
                // with concurrency=MaxDownloadConnections+5 (~35 concurrent NNTP
                // attempts), all of which fail immediately — but the per-item cost
                // (NZB XML parse + segment-lookup DB query + 30+ task allocations
                // + exception unwinding) burns several hundred ms of CPU each, and
                // with a queue backlog the manager hammers items at full speed,
                // pegging CPU and starving the threadpool. Sleeping for 30s here
                // is cheap and lets the breakers recover before we try again.
                if (_usenetClient.AreAllProvidersTripped)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                // Yield to other work when the threadpool is congested. Each
                // queue item that starts processing spawns up to
                // GetQueueProcessingConcurrency() concurrent NNTP fetches plus
                // their state-machine continuations, RAR/par2 parsers, DB
                // transactions, etc. — easily 20-50 work items per queue
                // item. Under sustained load (large queue + slow provider)
                // the pool fills with these and the SAB API / WebDAV / UI
                // settings endpoints can't get scheduled within their
                // clients' own timeouts (Radarr/Sonarr HttpClient.Timeout =
                // 100s, rclone vfs poll = ~10s, browser settings PUT =
                // similar). Operators reported being unable to change
                // settings to back off the queue while the system was
                // overloaded *by* the queue.
                //
                // PendingWorkItemCount measures queued-but-not-yet-running
                // tasks; threshold 100 is well above quiet-system baseline
                // (~10-40 in normal operation per our diag snapshots) and
                // well below the runaway state (400-1000+ observed when
                // saturated). Sleep 500ms when over: long enough for the
                // pool to drain a meaningful amount, short enough to resume
                // the queue promptly when pressure eases.
                if (ThreadPool.PendingWorkItemCount > QueueBackpressureThreshold)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    continue;
                }

                // get the next queue-item from the database
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var topItem = await LockAsync(() => dbClient.GetTopQueueItem(ct)).ConfigureAwait(false);
                if (topItem.queueItem is null)
                {
                    try
                    {
                        // if we're done with the queue, wait a minute before checking again.
                        // or wait until awoken by cancellation of _sleepingQueueToken
                        await Task.Delay(TimeSpan.FromMinutes(1), _sleepingQueueToken.Token).ConfigureAwait(false);
                    }
                    catch when (_sleepingQueueToken.IsCancellationRequested)
                    {
                        lock (_sleepingQueueLock)
                        {
                            if (!_sleepingQueueToken.TryReset())
                            {
                                _sleepingQueueToken.Dispose();
                                _sleepingQueueToken = new CancellationTokenSource();
                            }
                        }
                    }

                    continue;
                }

                // create an article-caching nntp-client.
                // the cache will be scoped only to this single queue-item.
                using var cachingUsenetClient = new ArticleCachingNntpClient(_usenetClient);

                // process the queue-item
                try
                {
                    using var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await LockAsync(() =>
                    {
                        // ReSharper disable twice AccessToDisposedClosure
                        _inProgressQueueItem = BeginProcessingQueueItem(dbClient, cachingUsenetClient,
                            topItem.queueItem, topItem.queueNzbStream, queueItemCancellationTokenSource);
                    }).ConfigureAwait(false);

                    var inProgressItem = _inProgressQueueItem;
                    var processingTask = inProgressItem?.ProcessingTask ?? Task.CompletedTask;

                    // Watch progress in parallel with processing. If the item
                    // reports no progress change for QueueItemStuckThreshold,
                    // fire cancellation. Items that are genuinely making
                    // progress (NNTP fetches reporting, file processors
                    // reporting, etc.) get unlimited wall-clock time;
                    // items truly stuck get cancelled automatically and
                    // pushed out via PauseUntil so the queue moves on.
                    //
                    // The watchdog uses its own CTS linked to manager ct so
                    // we can stop it the moment processing completes —
                    // otherwise the watchdog's Task.Delay would keep us
                    // waiting up to QueueItemStuckCheckInterval after every
                    // normal queue item, throttling throughput.
                    using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var watchdog = inProgressItem != null
                        ? WatchForStuckProgressAsync(
                            inProgressItem, queueItemCancellationTokenSource, watchdogCts.Token)
                        : Task.CompletedTask;

                    try
                    {
                        await processingTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        // Stop the watchdog now that processing is done (or threw).
                        try { await watchdogCts.CancelAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { /* race with using-dispose, ignore */ }
                        try { await watchdog.ConfigureAwait(false); }
                        catch (Exception e) { Log.Warning(e, "Watchdog task threw after cancellation"); }
                    }

                    // If watchdog fired cancellation (and not external shutdown),
                    // push this item out so the next QueueManager iteration
                    // doesn't immediately pick it up again and re-hit the same
                    // hang. 15 minutes + jitter spreads retries.
                    if (queueItemCancellationTokenSource.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            await using var pauseContext = new DavDatabaseContext();
                            var item = await pauseContext.QueueItems
                                .FirstOrDefaultAsync(x => x.Id == topItem.queueItem.Id, ct)
                                .ConfigureAwait(false);
                            if (item != null)
                            {
                                item.PauseUntil = DateTime.Now
                                    .AddMinutes(15)
                                    .AddSeconds(Random.Shared.Next(0, 300));
                                await pauseContext.SaveChangesAsync(ct).ConfigureAwait(false);
                                Log.Warning(
                                    "Queue item `{Job}` made no progress for {Threshold} and was cancelled; PauseUntil = {Until} (15-20 min).",
                                    topItem.queueItem.JobName, QueueItemStuckThreshold, item.PauseUntil);
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Failed to set PauseUntil on stuck queue item `{Job}`",
                                topItem.queueItem.JobName);
                        }
                    }
                }
                finally
                {
                    if (topItem.queueNzbStream is not null)
                        await topItem.queueNzbStream.DisposeAsync();
                }
            }
            catch (Exception e)
            {
                Log.Error($"An unexpected error occured while processing the queue: {e.Message}");
            }
            finally
            {
                await LockAsync(() => { _inProgressQueueItem = null; }).ConfigureAwait(false);

                // Small per-item backoff to prevent thundering-herd CPU when a
                // large queue (we've observed 7k+ items in production) all
                // times out its PauseUntil window simultaneously after an NNTP
                // outage and the QueueManager grinds them sequentially through
                // fail-fast (~50ms each). 100ms is negligible against real
                // download times but caps fail-fast throughput at ~10 items/sec
                // and keeps a core free for inbound requests.
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutting down */ }
            }
        }
    }

    /// <summary>
    /// Background watchdog that cancels the queue item's CTS if its progress
    /// percentage doesn't change for QueueItemStuckThreshold. Polls every
    /// QueueItemStuckCheckInterval. Exits cleanly when the processing task
    /// completes, when the watchdog is cancelled, or when the manager is
    /// shutting down.
    /// </summary>
    private static async Task WatchForStuckProgressAsync(
        InProgressQueueItem inProgress,
        CancellationTokenSource itemCts,
        CancellationToken managerCt)
    {
        var lastObservedProgress = inProgress.ProgressPercentage;
        var lastChangeTickMs = Environment.TickCount64;

        while (!inProgress.ProcessingTask.IsCompleted && !managerCt.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(QueueItemStuckCheckInterval, managerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (inProgress.ProcessingTask.IsCompleted) return;

            var current = inProgress.ProgressPercentage;
            var now = Environment.TickCount64;
            if (current != lastObservedProgress)
            {
                lastObservedProgress = current;
                lastChangeTickMs = now;
                continue;
            }

            var idleMs = now - lastChangeTickMs;
            if (idleMs >= QueueItemStuckThreshold.TotalMilliseconds)
            {
                Log.Warning(
                    "Queue item `{Job}` made no progress for {Idle} (last reported progress: {Progress}%); " +
                    "firing cancellation. If this happens repeatedly the item or a downstream " +
                    "subsystem (SQLite writer contention, S3, etc.) is genuinely stuck — " +
                    "check the phase logs in QueueItemProcessor and consider raising " +
                    "QUEUE_ITEM_STUCK_MINUTES if the item is just slow.",
                    inProgress.QueueItem.JobName, TimeSpan.FromMilliseconds(idleMs), current);
                try
                {
                    await itemCts.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to cancel stuck queue item `{Job}`",
                        inProgress.QueueItem.JobName);
                }
                return;
            }
        }
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        QueueItem queueItem,
        Stream? queueNzbStream,
        CancellationTokenSource cts
    )
    {
        var progressHook = new Progress<int>();
        var task = new QueueItemProcessor(
            queueItem, queueNzbStream, dbClient, usenetClient,
            _configManager, _websocketManager, progressHook, cts.Token
        ).ProcessAsync();
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProcessingTask = task,
            ProgressPercentage = 0,
            CancellationTokenSource = cts
        };
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        progressHook.ProgressChanged += (_, progress) =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        };
        return inProgressQueueItem;
    }

    private async Task LockAsync(Func<Task> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<T> LockAsync<T>(Func<Task<T>> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task LockAsync(Action action)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    private class InProgressQueueItem
    {
        public QueueItem QueueItem { get; init; }
        public int ProgressPercentage { get; set; }
        public Task ProcessingTask { get; init; }
        public CancellationTokenSource CancellationTokenSource { get; init; }
    }
}
