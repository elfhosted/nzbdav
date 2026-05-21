namespace NzbWebDAV.Clients.Usenet.Models;

/// <summary>
/// Lightweight read-only snapshot of one NNTP provider's runtime state,
/// used by DiagnosticLoggerService to surface what's happening during
/// outages without needing dotnet-counters or container exec access.
/// </summary>
public sealed record ProviderDiagnostic(
    string Name,
    bool IsTripped,
    int CooldownRemainingMs,
    int ConsecutiveFailures,
    int LiveConnections,
    int IdleConnections,
    long TotalRecordedFailures,
    long TotalRecordedSuccesses,
    long TotalArticleNotFound,
    string? LastFailureReason);
