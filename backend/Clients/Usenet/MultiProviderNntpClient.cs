using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : NntpClient
{
    // State-transition log throttling: we log once when AreAllProvidersTripped
    // first flips to true and once when it flips back to false. Stored as a
    // static so it survives provider list rebuilds on config change.
    private static int _lastCascadeState; // 0 = healthy, 1 = cascade engaged

    public override bool AreAllProvidersTripped
    {
        get
        {
            // Walk the providers once and collect every signal we need.
            var enabledCount = 0;
            var trippedCount = 0;
            var strugglingCount = 0;
            foreach (var p in providers)
            {
                if (p.ProviderType == ProviderType.Disabled) continue;
                enabledCount++;
                if (p.IsTripped) trippedCount++;
                if (p.IsCurrentlyStruggling) strugglingCount++;
            }
            if (enabledCount == 0) return false;

            var engaged = ComputeEngaged(enabledCount, trippedCount, strugglingCount);

            // Log on transition so operators can see in production when the
            // short-circuit kicks in / releases. Volatile flip via Interlocked
            // so we don't log on every check during steady state.
            var newState = engaged ? 1 : 0;
            var prev = Interlocked.Exchange(ref _lastCascadeState, newState);
            if (prev != newState)
            {
                if (engaged)
                    Log.Warning(
                        "Usenet cascade engaged ({Tripped}/{Total} providers tripped); WebDAV GETs and queue processing will short-circuit until recovery.",
                        trippedCount, enabledCount);
                else
                    Log.Information(
                        "Usenet cascade released ({Tripped}/{Total} providers tripped); WebDAV GETs and queue processing resuming.",
                        trippedCount, enabledCount);
            }

            return engaged;
        }
    }

    public override IReadOnlyList<ProviderDiagnostic> GetProviderDiagnostics()
    {
        var result = new List<ProviderDiagnostic>(providers.Count);
        foreach (var p in providers)
        {
            if (p.ProviderType == ProviderType.Disabled) continue;
            result.Add(new ProviderDiagnostic(
                Name: p.ProviderName,
                IsTripped: p.IsTripped,
                CooldownRemainingMs: p.CooldownRemainingMs,
                ConsecutiveFailures: p.ConsecutiveFailures,
                LiveConnections: p.LiveConnections,
                IdleConnections: p.IdleConnections,
                TotalRecordedFailures: p.TotalRecordedFailures,
                TotalRecordedSuccesses: p.TotalRecordedSuccesses,
                TotalArticleNotFound: p.TotalArticleNotFound,
                LastFailureReason: p.LastFailureReason));
        }
        return result;
    }

    private static bool ComputeEngaged(int enabledCount, int trippedCount, int strugglingCount)
    {
        // Full outage: every enabled provider's breaker is open.
        if (trippedCount == enabledCount) return true;

        // Universal-struggle: at least one provider has fully tripped AND
        // every enabled provider (including the not-yet-tripped ones) has
        // ConsecutiveFailures > 0 — i.e. their last operation was a failure
        // and they're on the path to tripping themselves. This is the
        // actual outage window worth short-circuiting: between the first
        // trip (1/N) and the second (2/N), the partially-working provider
        // is failing every real request but hasn't hit the 3-failure trip
        // threshold yet because some requests still succeed. As soon as a
        // not-tripped provider lands a real success, its ConsecutiveFailures
        // resets to 0 and the cascade releases automatically — so this
        // condition CANNOT engage while any provider is genuinely working
        // (an earlier majority-tripped fast path did engage in that case
        // and was removed after codex review flagged it as turning partial
        // outages into full ones).
        if (trippedCount > 0 && strugglingCount == enabledCount) return true;

        // Don't apply a time-based fallback. A long idle period followed by
        // a single tripped provider was previously enough to engage the
        // cascade, which over-fires during low-traffic windows when the
        // remaining providers haven't been exercised in a while. The
        // universal-struggle check above is more precise: it requires
        // evidence that the remaining providers are also currently failing,
        // not just that the process hasn't seen NNTP success recently.

        return false;
    }

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
        var attempted = 0;
        var realFailures = 0; // attempts that weren't just a mid-call breaker trip
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            // Skip silently if the provider's circuit breaker tripped after
            // GetOrderedProviders captured this list (another concurrent
            // request may have tripped it just now). Without this, every
            // in-flight request iterating a stale snapshot logs a Warning
            // per provider — drowning the pod in noise during outages.
            if (provider.IsTripped) continue;
            attempted++;

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
                var innerMsg = e.InnerException?.Message ?? e.Message;
                var isArticleNotFound = e.TryGetCausingException(out UsenetArticleNotFoundException _);
                // Demote to Debug when the breaker tripped during this call
                // (the trip transition itself is already logged once by
                // ProviderCircuitBreaker; per-request bounce-off logs are
                // pure noise). Also don't count these as real failures, so
                // the summary log below is suppressed for in-flight requests
                // that picked up a now-tripped provider via a stale snapshot.
                if (provider.IsTripped)
                {
                    Log.Debug("Provider {Provider} skipped after mid-call trip: {Error}",
                        provider.ProviderName, innerMsg);
                }
                else if (isArticleNotFound)
                {
                    // Article-not-found means the provider is healthy — we
                    // successfully connected, authenticated, and queried —
                    // it just doesn't carry this specific message-id (normal
                    // for articles aged out of retention, par2 files, etc).
                    // Don't log at Warning (it's not a provider problem) and
                    // don't increment realFailures (the breaker would
                    // mis-attribute retention gaps as outages). The caller
                    // (CheckAllSegmentsAsync, FetchFirstSegmentsStep, etc.)
                    // logs the missing article with its own file/context.
                    Log.Debug("Provider {Provider} does not carry message-id: {Error}",
                        provider.ProviderName, innerMsg);
                }
                else
                {
                    Log.Warning("Provider {Provider} failed: {Error}",
                        provider.ProviderName, innerMsg);
                    realFailures++;
                }
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (lastException != null)
        {
            // Only emit the summary log when there was at least one *real*
            // failure (provider was up when we tried, then errored). When
            // every attempt ended at "circuit breaker is open" because the
            // breaker tripped mid-call, this would log once per in-flight
            // request during an outage — exactly the noise this patch is
            // trying to suppress.
            if (realFailures > 0)
                Log.Warning("All {Count} providers failed. Last error: {Error}",
                    orderedProviders.Count, lastException.SourceException.Message);
            lastException.Throw();
        }

        // All providers were skipped (became tripped between GetOrderedProviders
        // and the iteration reaching them). Throw the same fail-fast exception
        // as the up-front check so callers see a consistent error during outages.
        if (attempted == 0)
            throw new CouldNotConnectToUsenetException(
                "All usenet providers became tripped during request iteration.");

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