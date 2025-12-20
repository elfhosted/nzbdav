using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
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
        var envLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            await using var databaseContext = new DavDatabaseContext();
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database.MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            return;
        }

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        var exportOptions = BuildExportOptions(args);

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

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
            .AddSingleton(exportOptions)
            .AddSingleton(websocketManager)
            .AddSingleton<NzbStorageService>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ArrMonitoringService>()
            .AddSingleton<HealthCheckService>()
            .AddHostedService<ExportInlineNzbsHostedService>()
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

        // force instantiation of services
        var app = builder.Build();
        app.Services.GetRequiredService<ArrMonitoringService>();
        app.Services.GetRequiredService<HealthCheckService>();

        // run
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseWebSockets();
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
        await app.RunAsync().ConfigureAwait(false);
    }
    private static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], option, StringComparison.Ordinal)) continue;
            return i + 1 < args.Count ? args[i + 1] : null;
        }

        return null;
    }

    private static int? TryParseIntOption(IReadOnlyList<string> args, string option)
    {
        var value = GetOptionValue(args, option);
        if (value == null) return null;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ExportInlineNzbsOptions BuildExportOptions(string[] args)
    {
        var enabled = args.Contains("--auto-export-inline-nzbs")
                      || args.Contains("--export-inline-nzbs")
                      || EnvironmentUtil.IsVariableTrue("NZB_STORAGE_AUTO_EXPORT_INLINE_NZBS");

        var batchSize = TryParseIntOption(args, "--export-batch-size")
                        ?? TryParseIntOption(args, "--batch-size")
                        ?? GetIntFromEnv("NZB_STORAGE_EXPORT_BATCH_SIZE")
                        ?? 100;

        var delayMs = TryParseIntOption(args, "--export-delay-ms")
                      ?? GetIntFromEnv("NZB_STORAGE_EXPORT_DELAY_MS")
                      ?? 500;

        var reportPath = GetOptionValue(args, "--export-report-path")
                         ?? GetOptionValue(args, "--report-path")
                         ?? Environment.GetEnvironmentVariable("NZB_STORAGE_EXPORT_REPORT_PATH");

        return new ExportInlineNzbsOptions
        {
            Enabled = enabled,
            BatchSize = Math.Max(1, batchSize),
            DelayBetweenBatches = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
            ReportPath = reportPath
        };
    }

    private static int? GetIntFromEnv(string variable)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(variable), out var value)
            ? value
            : null;
    }
}