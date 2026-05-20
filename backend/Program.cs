using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

class Program
{
    static async Task Main(string[] args)
    {
        // Update thread-pool
        var coreCount = Environment.ProcessorCount;
        var minThreads = Math.Max(coreCount * 2, 50); // 2x cores, minimum 50
        var maxThreads = Math.Max(coreCount * 50, 1000); // 50x cores, minimum 1000
        ThreadPool.SetMinThreads(minThreads, minThreads);
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = EnvironmentUtil.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            // Suppress NWebDav's per-property "raised an exception" Errors when
            // the cause is client cancellation. When a WebDAV client disconnects
            // mid-PROPFIND, PropFindHandler keeps iterating its property list
            // and every remaining property's getter throws OperationCanceledException
            // against the now-cancelled token. Each one logs an Error with a
            // full stack trace — on a busy pod with frequent cancellations from
            // Plex/Jellyfin/rclone, that's both log noise and meaningful CPU
            // spent on stack-trace serialisation. Client-cancellation is not
            // an error condition we need to alert on.
            .Filter.ByExcluding(e =>
                e.Exception is OperationCanceledException
                && e.MessageTemplate.Text.StartsWith("Property "))
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger();

        // Block upgrades to version 0.6.x
        BlockUpgradesToV06X();

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database
                .MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            await PerformDatabaseVacuumIfEnabled();
            return;
        }

        // initialize S3 blob store (if configured via env vars)
        await BlobStoreProvider.InitializeAsync();

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // initialize rclone client
        RcloneClient.Initialize(configManager);

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

        // If the deployment enforces symlinks, rewrite any stored "strm" config
        // to "symlinks" and convert existing .strm files in the background.
        await EnforceSymlinkImportStrategyAsync(configManager, websocketManager).ConfigureAwait(false);

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddHostedService<HealthCheckService>()
            .AddHostedService<ArrMonitoringService>()
            .AddHostedService<BlobCleanupService>()
            .AddHostedService<NzbBlobCleanupService>()
            .AddHostedService<HistoryCleanupService>()
            .AddHostedService<DavCleanupService>()
            .AddHostedService<UsenetFileToBlobstoreMigrationService>()
            .AddHostedService<RemoveOrphanedFilesSchedulerService>()
            .AddHostedService<DiagnosticLoggerService>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddScoped<SabApiController>()
            .AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });

        // run
        var app = builder.Build();
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
        await app.RunAsync().ConfigureAwait(false);
    }

    private static void BlockUpgradesToV06X()
    {
        // If the database file doesn't exist.
        // Then this is a new installation.
        // Do nothing.
        if (!File.Exists(DavDatabaseContext.DatabaseFilePath)) return;

        // If there is no pending database migration,
        // Then the user has already upgraded.
        // Do nothing.
        using var databaseContext = new DavDatabaseContext();
        const string migration = "20260226053712_Add-NzbBlobId-And-NzbNames";
        var hasPendingMigration = databaseContext.Database.GetPendingMigrations().Contains(migration);
        if (!hasPendingMigration) return;

        // If the user has set the UPGRADE env variable,
        // Then they have acknowledged the upgrade message.
        // Do nothing.
        var upgradeEnv = EnvironmentUtil.GetEnvironmentVariable("UPGRADE");
        if (upgradeEnv == "0.6.0") return;

        // Otherwise, display the upgrade message, and exit.
        Console.WriteLine(
            """
            Version 0.6.0 of nzbdav is NOT backwards compatible.
            You can upgrade, but you won't be able to downgrade.
            Make a backup of your entire /config directory prior to upgrading.
            The only way to downgrade back to a previous version is by restoring this backup.
            To acknowledge this message and continue upgrading, set the env variable UPGRADE=0.6.0
            """
        );
        Environment.Exit(1);
    }

    /// <summary>
    /// When LOCK_IMPORT_STRATEGY_SYMLINKS is set, run StrmToSymlinksTask
    /// in the background on every startup, and rewrite the stored
    /// api.import-strategy config to "symlinks" only AFTER conversion
    /// completes — so partial failures get retried on the next startup
    /// instead of being silently considered "already migrated".
    /// ConfigManager.GetImportStrategy() also returns "symlinks"
    /// unconditionally when locked, so runtime behaviour is correct
    /// regardless of whether conversion has succeeded yet.
    /// </summary>
    private static Task EnforceSymlinkImportStrategyAsync(
        ConfigManager configManager,
        WebsocketManager websocketManager)
    {
        if (!ConfigManager.IsImportStrategyLockedToSymlinks()) return Task.CompletedTask;

        Log.Information("LOCK_IMPORT_STRATEGY_SYMLINKS: scheduling StrmToSymlinksTask in background");
        _ = Task.Run(async () =>
        {
            try
            {
                await using var taskDbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(taskDbContext);
                var task = new Tasks.StrmToSymlinksTask(configManager, dbClient, websocketManager);
                await task.Execute().ConfigureAwait(false);

                // StrmToSymlinksTask swallows its own exceptions, so a non-throwing
                // Execute() doesn't prove full success. Verify zero remaining .strm
                // files in the library before claiming the migration is complete —
                // anything else means we should retry on the next startup.
                var remainingStrm = Utils.OrganizedLinksUtil
                    .GetLibraryDavItemLinks(configManager)
                    .Any(x => x.SymlinkOrStrmInfo is Utils.SymlinkAndStrmUtil.StrmInfo);
                if (remainingStrm)
                {
                    Log.Warning("LOCK_IMPORT_STRATEGY_SYMLINKS: conversion left .strm files behind; will retry on next startup");
                    return;
                }

                // Conversion fully complete. Persist the new strategy so the
                // UI shows the correct "current" value. Idempotent.
                await using var dbContext = new DavDatabaseContext();
                var stored = await dbContext.ConfigItems
                    .FirstOrDefaultAsync(x => x.ConfigName == "api.import-strategy")
                    .ConfigureAwait(false);
                if (stored == null)
                {
                    dbContext.ConfigItems.Add(new Database.Models.ConfigItem
                    {
                        ConfigName = "api.import-strategy",
                        ConfigValue = "symlinks"
                    });
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
                else if (stored.ConfigValue != "symlinks")
                {
                    Log.Information("LOCK_IMPORT_STRATEGY_SYMLINKS: rewriting stored api.import-strategy '{Old}' -> 'symlinks' after successful conversion", stored.ConfigValue);
                    stored.ConfigValue = "symlinks";
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
                configManager.UpdateValues([new Database.Models.ConfigItem
                {
                    ConfigName = "api.import-strategy",
                    ConfigValue = "symlinks"
                }]);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LOCK_IMPORT_STRATEGY_SYMLINKS: enforcement task failed; will retry on next startup");
            }
        });

        return Task.CompletedTask;
    }

    private static async Task PerformDatabaseVacuumIfEnabled()
    {
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        if (configManager.IsDatabaseStartupVacuumEnabled())
        {
            Console.Write("Performing database vacuum...");
            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;");
            Console.WriteLine("Done.");
        }
    }
}