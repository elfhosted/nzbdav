using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Thread-safe, lazy connection pool.
/// <para>
/// *  Connections are created through a user-supplied factory (sync or async).<br/>
/// *  At most <c>maxConnections</c> live instances exist at any time.<br/>
/// *  Idle connections older than <see cref="IdleTimeout"/> are disposed
///    automatically by a background sweeper.<br/>
/// *  <see cref="Dispose"/> / <see cref="DisposeAsync"/> stop the sweeper and
///    dispose all cached connections.  Borrowed handles returned afterwards are
///    destroyed immediately.
/// *  Note: This class was authored by ChatGPT 3o
/// </para>
/// </summary>
public sealed class ConnectionPool<T> : IDisposable, IAsyncDisposable
{
    /* -------------------------------- configuration -------------------------------- */

    public TimeSpan IdleTimeout { get; }
    public int LiveConnections => _live;
    public int IdleConnections => _idleConnections.Count;
    public int ActiveConnections => _live - _idleConnections.Count;
    public int AvailableConnections => _maxConnections - ActiveConnections;

    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly int _maxConnections;

    /* --------------------------------- state --------------------------------------- */

    private readonly ConcurrentStack<Pooled> _idleConnections = new();
    private readonly PrioritizedSemaphore _gate;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweeperTask; // keeps timer alive

    private int _live; // number of connections currently alive
    private int _disposed; // 0 == false, 1 == true

    /* ---- factory circuit breaker: stops all callers hammering a dead provider ---- */
    private const int FactoryFailureThreshold = 1; // trip on first failure; higher-level retries handle transients
    private static readonly TimeSpan FactoryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthFailureCooldown = TimeSpan.FromMinutes(5); // bad credentials won't self-heal
    private int _consecutiveFactoryFailures;
    private long _lastFactoryFailureTickMs;
    private Exception? _lastFactoryException;
    private int _isAuthFailure; // 0 = no, 1 = yes (last failure was auth/credentials)

    /* ---- adaptive concurrency (AIMD: additive-increase, multiplicative-decrease) ----
       Detects upstream connection-cap pressure by watching factory failures.
       On every factory failure: halve _adaptiveMaxConnections (floor 1).
       On every recovery interval with zero factory failures: grow by 1 (cap configured max).
       The gate semaphore's max-allowed is kept in sync so new acquires are bounded
       by the current adaptive cap, while in-flight connections drain naturally.
       Disable with env var DISABLE_ADAPTIVE_POOL_SIZE=true. */
    private static readonly bool AdaptiveDisabled = string.Equals(
        Environment.GetEnvironmentVariable("DISABLE_ADAPTIVE_POOL_SIZE"),
        "true", StringComparison.OrdinalIgnoreCase);
    private static readonly TimeSpan AdaptiveRecoveryInterval = TimeSpan.FromSeconds(60);
    private readonly int _configuredMaxConnections;
    private int _adaptiveMaxConnections;
    private long _lastAdaptiveAdjustTickMs;
    public int CurrentMaxConnections => Volatile.Read(ref _adaptiveMaxConnections);
    public int ConfiguredMaxConnections => _configuredMaxConnections;

    /* ------------------------------------------------------------------------------ */

    public ConnectionPool(
        int maxConnections,
        Func<CancellationToken, ValueTask<T>> connectionFactory,
        TimeSpan? idleTimeout = null)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));

        _factory = connectionFactory
                   ?? throw new ArgumentNullException(nameof(connectionFactory));
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);

        _maxConnections = maxConnections;
        _configuredMaxConnections = maxConnections;
        _adaptiveMaxConnections = maxConnections;
        _lastAdaptiveAdjustTickMs = Environment.TickCount64;
        _gate = new PrioritizedSemaphore(maxConnections, maxConnections);
        _sweeperTask = Task.Run(SweepLoop); // background idle-reaper
    }

    /* ============================== public API ==================================== */

    /// <summary>
    /// Borrow a connection while reserving capacity for higher-priority callers.
    /// Waits until at least (`reservedCount` + 1) slots are free before acquiring one,
    /// ensuring that after acquisition at least `reservedCount` remain available.
    /// </summary>
    public async Task<ConnectionLock<T>> GetConnectionLockAsync
    (
        SemaphorePriority priority,
        CancellationToken cancellationToken = default
    )
    {
        // Make caller cancellation also cancel the wait on the gate.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token);

        await _gate.WaitAsync(priority, linked.Token).ConfigureAwait(false);

        // Pool might have been disposed after wait returned:
        if (Volatile.Read(ref _disposed) == 1)
        {
            _gate.Release();
            ThrowDisposed();
        }

        // Try to reuse an existing idle connection.
        while (_idleConnections.TryPop(out var item))
        {
            if (!item.IsExpired(IdleTimeout))
            {
                TriggerConnectionPoolChangedEvent();
                return BuildLock(item.Connection);
            }

            // Stale – destroy and continue looking.
            DisposeConnection(item.Connection);
            Interlocked.Decrement(ref _live);
            TriggerConnectionPoolChangedEvent();
        }

        // Circuit breaker: if factory has failed repeatedly, delay callers for the
        // remaining cooldown period instead of hammering the provider and burning CPU.
        var failures = Volatile.Read(ref _consecutiveFactoryFailures);
        if (failures >= FactoryFailureThreshold)
        {
            var cooldown = Volatile.Read(ref _isAuthFailure) == 1 ? AuthFailureCooldown : FactoryCooldown;
            var elapsed = Environment.TickCount64 - Volatile.Read(ref _lastFactoryFailureTickMs);
            var remainingMs = (int)(cooldown.TotalMilliseconds - elapsed);
            if (remainingMs > 0)
            {
                _gate.Release();
                await Task.Delay(remainingMs, linked.Token).ConfigureAwait(false);
                throw _lastFactoryException ?? new InvalidOperationException(
                    $"Connection factory circuit breaker open ({failures} consecutive failures).");
            }
        }

        // Need a fresh connection.
        T conn;
        try
        {
            conn = await _factory(linked.Token).ConfigureAwait(false);
            Volatile.Write(ref _consecutiveFactoryFailures, 0); // reset on success
            Volatile.Write(ref _isAuthFailure, 0);
        }
        catch (Exception ex)
        {
            _lastFactoryException = ex;
            Interlocked.Increment(ref _consecutiveFactoryFailures);
            Volatile.Write(ref _lastFactoryFailureTickMs, Environment.TickCount64);
            var isBadCredentials = ex.Message.Contains("Invalid username or password",
                                      StringComparison.OrdinalIgnoreCase) ||
                                  ex.InnerException?.Message.Contains("Invalid username or password",
                                      StringComparison.OrdinalIgnoreCase) == true;
            Volatile.Write(ref _isAuthFailure, isBadCredentials ? 1 : 0);
            // Adaptive shrink: don't shrink on auth failures (config issue,
            // not concurrency pressure — adding to the floor wouldn't help).
            if (!isBadCredentials) AdaptiveOnFactoryFailure();
            _gate.Release(); // free the permit on failure
            throw;
        }

        Interlocked.Increment(ref _live);
        TriggerConnectionPoolChangedEvent();
        return BuildLock(conn);

        ConnectionLock<T> BuildLock(T c)
            => new(c, Return, Destroy);

        static void ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ConnectionPool<T>));
    }

    /* ========================== core helpers ====================================== */

    private readonly record struct Pooled(T Connection, long LastTouchedMillis)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(TimeSpan idle, long nowMillis = 0)
        {
            if (nowMillis == 0) nowMillis = Environment.TickCount64;
            return unchecked(nowMillis - LastTouchedMillis) >= idle.TotalMilliseconds;
        }
    }

    private void Return(T connection)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            DisposeConnection(connection);
            Interlocked.Decrement(ref _live);
            TriggerConnectionPoolChangedEvent();
            return;
        }

        _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
        _gate.Release();
        TriggerConnectionPoolChangedEvent();
    }

    private void Destroy(T connection)
    {
        // When a lock requests replacement, we dispose the connection instead of reusing.
        DisposeConnection(connection);
        Interlocked.Decrement(ref _live);
        if (Volatile.Read(ref _disposed) == 0)
        {
            _gate.Release();
        }

        TriggerConnectionPoolChangedEvent();
    }

    private void TriggerConnectionPoolChangedEvent()
    {
        OnConnectionPoolChanged?.Invoke(this, new ConnectionPoolStats.ConnectionPoolChangedEventArgs(
            _live,
            _idleConnections.Count,
            _maxConnections
        ));
    }

    /* =================== idle sweeper (background) ================================= */

    private async Task SweepLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(IdleTimeout / 2);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                SweepOnce();
                AdaptiveTryGrow();
            }
        }
        catch (OperationCanceledException)
        {
            /* normal on disposal */
        }
    }

    /* =================== adaptive concurrency ====================================== */

    /// <summary>
    /// Multiplicative-decrease: halve the runtime max-connections (floor 1) so
    /// the pool stops issuing new factory calls above whatever the upstream is
    /// happy to accept. Existing in-flight connections continue and drain
    /// naturally as `_gate.UpdateMaxAllowed` only gates *new* waiters.
    /// </summary>
    private void AdaptiveOnFactoryFailure()
    {
        if (AdaptiveDisabled) return;
        while (true)
        {
            var current = Volatile.Read(ref _adaptiveMaxConnections);
            if (current <= 1) return;
            var newMax = Math.Max(1, current / 2);
            if (Interlocked.CompareExchange(ref _adaptiveMaxConnections, newMax, current) != current)
                continue; // contended, retry
            Volatile.Write(ref _lastAdaptiveAdjustTickMs, Environment.TickCount64);
            _gate.UpdateMaxAllowed(newMax);
            Log.Information(
                "Connection pool adaptive: halved max-connections {Old}->{New} (configured={Configured}) after factory failure",
                current, newMax, _configuredMaxConnections);
            return;
        }
    }

    /// <summary>
    /// Additive-increase: if no adaptive adjustment has happened in the last
    /// AdaptiveRecoveryInterval (and we're below the configured ceiling),
    /// grow by 1. Called from the periodic sweep loop.
    /// </summary>
    private void AdaptiveTryGrow()
    {
        if (AdaptiveDisabled) return;
        var current = Volatile.Read(ref _adaptiveMaxConnections);
        if (current >= _configuredMaxConnections) return;

        var now = Environment.TickCount64;
        var lastAdjust = Volatile.Read(ref _lastAdaptiveAdjustTickMs);
        if (now - lastAdjust < AdaptiveRecoveryInterval.TotalMilliseconds) return;

        var newMax = current + 1;
        if (Interlocked.CompareExchange(ref _adaptiveMaxConnections, newMax, current) != current)
            return; // contended; we'll get another chance next sweep
        Volatile.Write(ref _lastAdaptiveAdjustTickMs, now);
        _gate.UpdateMaxAllowed(newMax);
        Log.Debug(
            "Connection pool adaptive: grew max-connections {Old}->{New} (configured={Configured}) after stable period",
            current, newMax, _configuredMaxConnections);
    }

    private void SweepOnce()
    {
        var now = Environment.TickCount64;
        var survivors = new List<Pooled>();
        var isAnyConnectionFreed = false;

        while (_idleConnections.TryPop(out var item))
        {
            if (item.IsExpired(IdleTimeout, now))
            {
                DisposeConnection(item.Connection);
                Interlocked.Decrement(ref _live);
                isAnyConnectionFreed = true;
            }
            else
            {
                survivors.Add(item);
            }
        }

        // Preserve original LIFO order.
        for (var i = survivors.Count - 1; i >= 0; i--)
            _idleConnections.Push(survivors[i]);

        if (isAnyConnectionFreed)
            TriggerConnectionPoolChangedEvent();
    }

    /* ------------------------- dispose helpers ------------------------------------ */

    private static void DisposeConnection(T conn)
    {
        if (conn is IDisposable d)
            d.Dispose();
    }

    /* -------------------------- IAsyncDisposable ---------------------------------- */

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _sweepCts.CancelAsync();

        try
        {
            await _sweeperTask.ConfigureAwait(false); // await clean sweep exit
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }

        // Drain and dispose cached items.
        while (_idleConnections.TryPop(out var item))
            DisposeConnection(item.Connection);

        _sweepCts.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /* ----------------------------- IDisposable ------------------------------------ */

    public void Dispose()
    {
        _ = DisposeAsync().AsTask(); // fire-and-forget synchronous path
    }
}