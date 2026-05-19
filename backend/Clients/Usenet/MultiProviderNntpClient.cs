using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : NntpClient
{
    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    // Throttle the "all providers tripped" log so a request storm during an outage
    // doesn't produce one Warning per request (Serilog formatting + console writes
    // were observed to be a large fraction of CPU when the pod is failing).
    private static long _allTrippedLastLogTickMs;
    private const int AllTrippedLogIntervalMs = 30_000;

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var orderedProviders = GetOrderedProviders();

        // Fail-fast when no provider is currently usable. Without this we iterate
        // every tripped provider and each call sits ~30s inside ConnectionPool's
        // factory circuit breaker while holding a DownloadingNntpClient semaphore
        // slot. Aggressive client retries (Stremio/Radarr/Sonarr/Jellyfin) then
        // pile up behind those slots — threadpool, semaphore-waiter, exception,
        // and websocket-event load grows until the pod stops answering /health
        // and Kubernetes kills it. The ProviderCircuitBreaker cooldown already
        // re-admits each provider on its own clock, so no "probe attempt" is
        // needed here — the next request after a cooldown expires picks it up.
        if (orderedProviders.Count == 0)
        {
            var enabledCount = providers.Count(x => x.ProviderType != ProviderType.Disabled);
            if (enabledCount == 0)
                throw new Exception("There are no usenet providers configured.");

            var now = Environment.TickCount64;
            var lastLog = Volatile.Read(ref _allTrippedLastLogTickMs);
            if (now - lastLog >= AllTrippedLogIntervalMs &&
                Interlocked.CompareExchange(ref _allTrippedLastLogTickMs, now, lastLog) == lastLog)
            {
                Log.Warning(
                    "All {Count} usenet providers are in circuit-breaker cooldown; failing fast until one recovers.",
                    enabledCount);
            }

            throw new CouldNotConnectToUsenetException(
                $"All {enabledCount} usenet providers are in cooldown after recent failures.");
        }

        ExceptionDispatchInfo? lastException = null;
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    continue;

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                // Log a concise one-liner instead of letting the full stack trace propagate per-provider
                var innerMsg = e.InnerException?.Message ?? e.Message;
                Log.Warning("Provider {Provider} failed: {Error}", provider.ProviderName, innerMsg);
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (lastException != null)
        {
            Log.Warning("All {Count} providers failed. Last error: {Error}",
                orderedProviders.Count, lastException.SourceException.Message);
            lastException.Throw();
        }

        throw new Exception("There are no usenet providers configured.");
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        // Hard-skip tripped providers. Recovery is handled by ProviderCircuitBreaker's
        // cooldown — when a tripped provider's IsTripped flips back to false, the next
        // call to this method naturally includes it again, so there is no need to keep
        // a tripped provider in the list for "probing".
        return providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !x.IsTripped)
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}