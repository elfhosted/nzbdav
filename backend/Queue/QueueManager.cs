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
                    await (_inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask).ConfigureAwait(false);
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
