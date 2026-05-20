using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    // Shared "last successful NNTP operation" timestamp across all breakers.
    // Used by MultiProviderNntpClient.AreAllProvidersTripped to detect the
    // cascading-outage window — where one provider's cooldown has just expired
    // (so it isn't technically tripped) but every other provider is tripped
    // and nothing has succeeded recently, so any new request is doomed.
    // Initialised to startup tick so a cold-start outage doesn't immediately
    // trip the "stale" heuristic before the breakers have a chance to fire.
    private static long _anyProviderLastSuccessTickMs = Environment.TickCount64;
    public static long AnyProviderLastSuccessTickMs => Volatile.Read(ref _anyProviderLastSuccessTickMs);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;

    public string ProviderName => _providerName;
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Milliseconds until the trip cooldown expires, or 0 if the breaker
    /// is currently closed. Read-only diagnostic accessor — does not affect
    /// trip state.
    /// </summary>
    public int CooldownRemainingMs
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return 0;
            var remaining = trippedUntil - Environment.TickCount64;
            return remaining > 0 ? (int)remaining : 0;
        }
    }

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return false;
            return Environment.TickCount64 < trippedUntil;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
        }

        // Update the cross-breaker shared timestamp so the WebDAV / queue
        // short-circuit knows that *some* provider just demonstrated health.
        Volatile.Write(ref _anyProviderLastSuccessTickMs, Environment.TickCount64);
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures < FailureThreshold) return;

            // Idempotent while open: an already-tripped breaker must not re-arm
            // `_trippedUntilMs` (that would extend the trip window indefinitely
            // and prevent recovery), must not double `_currentCooldown` again,
            // and must not re-log on every failure. In-flight requests started
            // before the trip will keep producing failures after the transition;
            // those are expected and should be silently counted.
            var now = Environment.TickCount64;
            if (_trippedUntilMs != 0 && now < _trippedUntilMs) return;

            _trippedUntilMs = now + (long)_currentCooldown.TotalMilliseconds;
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. " +
                "Skipping for {Cooldown}s.",
                _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }
}
