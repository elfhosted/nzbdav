using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Tasks;

public sealed class ExportInlineNzbsHostedService(
    IServiceScopeFactory scopeFactory,
    ExportInlineNzbsOptions options
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        Log.Information(
            "Automatic inline NZB export enabled (batch size {Batch}, delay {Delay}).",
            options.BatchSize,
            options.DelayBetweenBatches);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var storageService = scope.ServiceProvider.GetRequiredService<NzbStorageService>();
            var exporter = new ExportInlineNzbsCommand(dbContext, storageService, options.BatchSize);
            await exporter
                .RunAsync(options.ReportPath, options.DelayBetweenBatches, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Log.Information("Inline NZB export cancelled because the host is shutting down.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Inline NZB export terminated unexpectedly.");
        }
    }
}

public sealed record ExportInlineNzbsOptions
{
    public bool Enabled { get; init; }
    public int BatchSize { get; init; } = 100;
    public TimeSpan DelayBetweenBatches { get; init; } = TimeSpan.FromSeconds(1);
    public string? ReportPath { get; init; }
}
