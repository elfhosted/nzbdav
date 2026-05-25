using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for delegating NNTP commands to a connection pool.
///   * The connection pool enforces a maximum number of allowed connections
///   * When a connection is available, the NNTP command executes immediately
///   * When a connection is not available, the NNTP command waits until a connection becomes available.
///   * When multiple commands are awaiting a connection,
///     then BODY/ARTICLE commands have higher priority than STAT/HEAD/DATE commands.
/// </summary>
/// <param name="connectionPool"></param>
/// <param name="type"></param>
/// <param name="circuitBreaker"></param>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class MultiConnectionNntpClient(
    ConnectionPool<INntpClient> connectionPool,
    ProviderType type,
    ProviderCircuitBreaker circuitBreaker
) : NntpClient
{
    public ProviderType ProviderType { get; } = type;
    public string ProviderName => circuitBreaker.ProviderName;
    public bool IsTripped => circuitBreaker.IsTripped;
    public int CooldownRemainingMs => circuitBreaker.CooldownRemainingMs;
    public int ConsecutiveFailures => circuitBreaker.ConsecutiveFailures;
    public bool IsCurrentlyStruggling => circuitBreaker.IsTripped || circuitBreaker.ConsecutiveFailures > 0;
    public long TotalRecordedFailures => circuitBreaker.TotalRecordedFailures;
    public long TotalRecordedSuccesses => circuitBreaker.TotalRecordedSuccesses;
    public long TotalArticleNotFound => circuitBreaker.TotalArticleNotFound;
    public string? LastFailureReason => circuitBreaker.LastFailureReason;
    public int AdaptiveMaxConnections => connectionPool.CurrentMaxConnections;
    public int ConfiguredMaxConnections => connectionPool.ConfiguredMaxConnections;
    public int LiveConnections => connectionPool.LiveConnections;
    public int IdleConnections => connectionPool.IdleConnections;
    public int ActiveConnections => connectionPool.ActiveConnections;
    public int AvailableConnections => connectionPool.AvailableConnections;

    // Per-provider log throttle so a burst of in-flight failures from the
    // window before the breaker tripped doesn't drown the pod in Serilog
    // formatting + console writes. One Warning per provider per 10s for the
    // "Connection failed" / "NNTP X failed" lines is enough to know that
    // failures occurred; the breaker state log already records the trip.
    private long _lastConnectionFailedLogTickMs;
    private long _lastCommandFailedLogTickMs;
    private const int FailureLogIntervalMs = 10_000;

    private bool TryAcquireFailureLogSlot(ref long lastTickMs)
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref lastTickMs);
        if (now - last < FailureLogIntervalMs) return false;
        return Interlocked.CompareExchange(ref lastTickMs, now, last) == last;
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "STAT",
            SemaphorePriority.Low,
            (connection, _) => connection.StatAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "HEAD",
            SemaphorePriority.Low,
            (connection, _) => connection.HeadAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "BODY",
            SemaphorePriority.High,
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            SemaphorePriority.High,
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
    {
        return RunWithConnection(
            "DATE",
            SemaphorePriority.Low,
            (connection, _) => connection.DateAsync(ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "BODY",
            SemaphorePriority.High,
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            SemaphorePriority.High,
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    private async Task<T> RunWithConnection<T>
    (
        string name,
        SemaphorePriority priority,
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> command,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct,
        int retryCount = 1
    ) where T : UsenetResponse
    {
        var initialRetryCount = retryCount;
        while (retryCount >= 0)
        {
            // Abort in-flight retries the moment the provider's circuit breaker
            // trips. Without this, requests already past MultiProviderNntpClient's
            // filter keep burning CPU on doomed TCP/TLS handshakes + 2s backoffs
            // until their retry budget runs out, producing the burst of
            // "Connection failed" / "Provider failed" logs that drowns the pod.
            if (circuitBreaker.IsTripped)
                throw new CouldNotConnectToUsenetException(
                    $"Provider {circuitBreaker.ProviderName} circuit breaker is open; aborting in-flight retry.");

            ConnectionLock<INntpClient>? connectionLock = null;
            try
            {
                connectionLock = await connectionPool.GetConnectionLockAsync(priority, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsCancellationException())
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                var innerMsg = e.InnerException?.Message ?? e.Message;
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    var backoffSeconds = Math.Pow(2, initialRetryCount - retryCount); // 2s, 4s, 8s...
                    Log.Debug(e, "Error getting connection-lock. Retrying after {BackoffSeconds}s backoff.", backoffSeconds);
                    retryCount--;
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                    continue;
                }

                // Record the failure on the provider's breaker only AFTER
                // retries are exhausted — internal retries exist for transient
                // resilience, and double-counting (one breaker failure per
                // internal attempt) trips the breaker at ~1.5 logical caller
                // attempts instead of the FailureThreshold (3) the breaker
                // was sized for.
                circuitBreaker.RecordFailure($"connect: {innerMsg}");
                if (TryAcquireFailureLogSlot(ref _lastConnectionFailedLogTickMs))
                    Log.Warning("Connection failed for {Provider}: {Error}", circuitBreaker.ProviderName, innerMsg);
                else
                    Log.Debug("Connection failed for {Provider}: {Error} (log throttled)", circuitBreaker.ProviderName, innerMsg);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            T? result;
            try
            {
                result = await command(connectionLock.Connection, OnConnectionReadyAgain).ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsCancellationException())
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException _))
            {
                // Article-not-found is a per-article fact, not a provider
                // failure. Count it for diagnostics (lifetime counter) but
                // don't increment the breaker.
                circuitBreaker.RecordArticleNotFound();
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                var innerMsg = e.InnerException?.Message ?? e.Message;
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    var backoffSeconds = Math.Pow(2, initialRetryCount - retryCount); // 2s, 4s, 8s...
                    Log.Debug(e, "Error executing nntp {Name} command. Retrying after {BackoffSeconds}s backoff.", name, backoffSeconds);
                    retryCount--;
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                    continue;
                }

                // Record after retry exhaustion (see connect-path comment above).
                circuitBreaker.RecordFailure($"{name}: {innerMsg}");
                if (TryAcquireFailureLogSlot(ref _lastCommandFailedLogTickMs))
                    Log.Warning("NNTP {Command} failed for {Provider}: {Error}", name, circuitBreaker.ProviderName, innerMsg);
                else
                    Log.Debug("NNTP {Command} failed for {Provider}: {Error} (log throttled)", name, circuitBreaker.ProviderName, innerMsg);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            circuitBreaker.RecordSuccess();

            // stat, head, and date
            if (name is "STAT" or "HEAD" or "DATE")
            {
                LogException(() => connectionLock?.Dispose());
            }
            
            // body and article
            else if ((result?.Success ?? false) == false)
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
            }

            return result!;

            void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
            {
                if (articleBodyResult != ArticleBodyResult.Retrieved) return;

                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(articleBodyResult));
            }
        }

        Log.Error("Unreachable code reached");
        throw new InvalidOperationException("Unreachable code ");
    }

    private static void LogException(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled exception");
        }
    }

    public override void Dispose()
    {
        connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}