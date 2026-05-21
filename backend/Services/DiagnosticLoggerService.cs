using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Emits a single structured diagnostic line every 60 seconds (or every 15
/// seconds while the NNTP cascade is engaged, since that's the interesting
/// window). Surfaces queue depth, cleanup-service backlogs, per-provider
/// breaker state, threadpool counters, and GC state — so operators can
/// tell at a glance which subsystem is consuming CPU during an outage
/// without needing dotnet-counters or container exec access.
///
/// Cheap to compute: ~5 small COUNT queries on indexed columns + a couple
/// of GC accessors per emission. The COUNT queries are on tables that
/// rarely exceed low thousands of rows, so they add no meaningful load.
/// </summary>
public class DiagnosticLoggerService(UsenetStreamingClient usenetClient) : BackgroundService
{
    private static readonly TimeSpan IntervalNormal = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IntervalDuringCascade = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var cascadeEngaged = false;
            try
            {
                cascadeEngaged = await LogDiagnosticsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning("DiagnosticLoggerService iteration failed: {Error}", e.Message);
            }

            var delay = cascadeEngaged ? IntervalDuringCascade : IntervalNormal;
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> LogDiagnosticsAsync(CancellationToken ct)
    {
        var cascadeEngaged = usenetClient.AreAllProvidersTripped;
        var providers = usenetClient.GetProviderDiagnostics();

        await using var ctx = new DavDatabaseContext();
        var now = DateTime.Now;

        // Queue: total + how many are eligible right now (PauseUntil null or past).
        // Eligible count is the canonical "how much work is about to hit the
        // QueueManager when it wakes up" number.
        var queueTotal = await ctx.QueueItems.CountAsync(ct).ConfigureAwait(false);
        var queueEligible = queueTotal == 0
            ? 0
            : await ctx.QueueItems
                .CountAsync(q => q.PauseUntil == null || q.PauseUntil <= now, ct)
                .ConfigureAwait(false);

        // Background-service backlogs.
        var blobCleanup = await ctx.BlobCleanupItems.CountAsync(ct).ConfigureAwait(false);
        var nzbCleanup = await ctx.NzbBlobCleanupItems.CountAsync(ct).ConfigureAwait(false);
        var davCleanup = await ctx.DavCleanupItems.CountAsync(ct).ConfigureAwait(false);

        // Threadpool: high pending count indicates starvation.
        var tpThreads = ThreadPool.ThreadCount;
        var tpPending = ThreadPool.PendingWorkItemCount;
        var tpCompleted = ThreadPool.CompletedWorkItemCount;

        // Memory + GC: rapid Gen2 count growth is the canonical GC-pressure tell.
        var memMb = GC.GetTotalMemory(false) / (1024 * 1024);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        // Compact per-provider summary. Format:
        //   name=ok|tripped(<remainingS>s)|f<consecutiveFailures>|c<active>/<idle>
        //        |lifetime[F=<recordedFailures>,S=<successes>,NA=<articleNotFound>]
        //        |last="<lastFailureReason>"
        // The lifetime breakdown distinguishes real provider failures (F)
        // that COULD trip the breaker from article-not-found events (NA)
        // that look similar in logs but cannot trip the breaker.
        var providerSummary = new StringBuilder();
        for (var i = 0; i < providers.Count; i++)
        {
            if (i > 0) providerSummary.Append(' ');
            var p = providers[i];
            providerSummary.Append(p.Name).Append('=');
            if (p.IsTripped)
                providerSummary.Append("tripped(")
                    .Append(p.CooldownRemainingMs / 1000)
                    .Append("s)");
            else
                providerSummary.Append("ok");
            providerSummary.Append("|f").Append(p.ConsecutiveFailures);
            providerSummary.Append("|c").Append(p.LiveConnections - p.IdleConnections)
                .Append('/').Append(p.IdleConnections);
            providerSummary.Append("|lifetime[F=").Append(p.TotalRecordedFailures)
                .Append(",S=").Append(p.TotalRecordedSuccesses)
                .Append(",NA=").Append(p.TotalArticleNotFound).Append(']');
            if (!string.IsNullOrEmpty(p.LastFailureReason))
                providerSummary.Append("|last=\"").Append(p.LastFailureReason).Append('"');
        }

        Log.Information(
            "diag cascade={Cascade} providers=[{Providers}] queue={QueueTotal}/{QueueEligible} cleanup=[blob={Blob},nzb={Nzb},dav={Dav}] tp=[t={Threads},pend={Pending},done={Done}] mem={MemMb}MB gc=[g0={G0},g1={G1},g2={G2}]",
            cascadeEngaged ? "engaged" : "released",
            providerSummary.ToString(),
            queueTotal, queueEligible,
            blobCleanup, nzbCleanup, davCleanup,
            tpThreads, tpPending, tpCompleted,
            memMb, gen0, gen1, gen2);

        return cascadeEngaged;
    }
}
