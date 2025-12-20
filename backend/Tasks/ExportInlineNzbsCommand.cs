using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Tasks;

public sealed class ExportInlineNzbsCommand
{
    private readonly DavDatabaseContext _dbContext;
    private readonly NzbStorageService _storageService;
    private readonly int _batchSize;

    public ExportInlineNzbsCommand(DavDatabaseContext dbContext, NzbStorageService storageService, int batchSize)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _batchSize = Math.Max(1, batchSize);
    }

    public async Task<string> RunAsync(string? reportPath, TimeSpan? delayBetweenBatches, CancellationToken ct)
    {
        var report = new ExportReport
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            TotalInlineItemsAtStart = await BuildInlineQuery().CountAsync(ct).ConfigureAwait(false)
        };

        if (report.TotalInlineItemsAtStart == 0)
        {
            Log.Information("No inline NZB payloads found; nothing to export.");
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            return await PersistReportAsync(reportPath, report, ct).ConfigureAwait(false);
        }

        Log.Information("Beginning export of {Total} inline NZB payloads (batch size {Batch}).",
            report.TotalInlineItemsAtStart, _batchSize);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await BuildInlineQuery()
                .OrderBy(x => x.Id)
                .Take(_batchSize)
                .Include(x => x.QueueItem)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (batch.Count == 0) break;

            var batchExported = 0;
            foreach (var row in batch)
            {
                ct.ThrowIfCancellationRequested();
                var queueItem = row.QueueItem;
                try
                {
                    var stored = await _storageService.WriteAsync(row.Id, row.NzbContents, ct).ConfigureAwait(false);
                    row.NzbContents = string.Empty;
                    row.ExternalPath = stored.RelativePath;
                    row.ExternalCompression = stored.Compression;
                    row.ExternalLengthBytes = stored.LengthBytes;
                    row.ExternalSha256 = stored.Sha256;

                    report.Exported.Add(new ExportedEntry(
                        row.Id,
                        queueItem?.JobName ?? queueItem?.FileName,
                        queueItem?.Category,
                        stored.RelativePath,
                        stored.LengthBytes,
                        stored.Sha256
                    ));
                    batchExported++;
                }
                catch (Exception ex)
                {
                    report.Failures.Add(new FailedEntry(
                        row.Id,
                        queueItem?.JobName ?? queueItem?.FileName,
                        queueItem?.Category,
                        ex.Message
                    ));
                    Log.Error(ex, "Failed to export inline NZB for queue item {QueueItemId}.", row.Id);
                }
            }

            report.ExportedCount += batchExported;
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();

            var remaining = await BuildInlineQuery().CountAsync(ct).ConfigureAwait(false);
            Log.Information(
                "Exported {BatchSuccess} NZBs this batch ({Exported}/{Total} total). Remaining inline payloads: {Remaining}.",
                batchExported,
                report.ExportedCount,
                report.TotalInlineItemsAtStart,
                remaining);

            if (remaining > 0 && delayBetweenBatches is { } delay && delay > TimeSpan.Zero)
            {
                Log.Debug("Waiting {Delay} before processing the next export batch.", delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        var reportFile = await PersistReportAsync(reportPath, report, ct).ConfigureAwait(false);
        Log.Information(
            "Inline NZB export finished. Successes: {Successes}. Failures: {Failures}. Report: {ReportPath}.",
            report.ExportedCount,
            report.Failures.Count,
            reportFile);
        return reportFile;
    }

    private IQueryable<QueueNzbContents> BuildInlineQuery()
    {
        return _dbContext.QueueNzbContents
            .Where(x => string.IsNullOrEmpty(x.ExternalPath))
            .Where(x => !string.IsNullOrEmpty(x.NzbContents));
    }

    private static async Task<string> PersistReportAsync(string? reportPath, ExportReport report, CancellationToken ct)
    {
        var finalPath = ResolveReportPath(reportPath);
        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(finalPath, json, ct).ConfigureAwait(false);
        return finalPath;
    }

    private static string ResolveReportPath(string? reportPath)
    {
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            return Path.GetFullPath(reportPath);
        }

        var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(reportsDir);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(reportsDir, $"export-inline-nzbs-{timestamp}.json");
    }

    public sealed record ExportReport
    {
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public int TotalInlineItemsAtStart { get; init; }
        public int ExportedCount { get; set; }
        public List<ExportedEntry> Exported { get; } = new();
        public List<FailedEntry> Failures { get; } = new();
    }

    public sealed record ExportedEntry
    (
        Guid QueueItemId,
        string? JobName,
        string? Category,
        string RelativePath,
        long LengthBytes,
        string Sha256
    );

    public sealed record FailedEntry
    (
        Guid QueueItemId,
        string? JobName,
        string? Category,
        string Error
    );
}
