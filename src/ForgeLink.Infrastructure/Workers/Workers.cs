using ForgeLink.Application.Common;
using ForgeLink.Application.Equipment;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Reporting;
using ForgeLink.Infrastructure.Erp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Infrastructure.Workers;

/// <summary>Runs a unit of work on a fixed cadence in its own DI scope, surviving (and logging) failures.</summary>
public abstract class PeriodicWorker(IServiceScopeFactory scopes, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (DB init, broker) before the first tick.
        await Task.Yield();
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunOnceAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Worker} iteration failed", GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}

public sealed class OutboxDispatcherWorker(IServiceScopeFactory scopes, IOptions<OutboxOptions> options, ILogger<OutboxDispatcherWorker> logger)
    : PeriodicWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(0.5, options.Value.PollIntervalSeconds));

    protected override async Task RunOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        var processor = services.GetRequiredService<OutboxProcessor>();
        // Drain quickly while there is a backlog, then fall back to the polling cadence.
        while ((await processor.ProcessBatchAsync(ct)).Sent >= options.Value.BatchSize && !ct.IsCancellationRequested) { }
    }
}

public sealed class ErpSyncWorker(IServiceScopeFactory scopes, IOptions<ErpClientOptions> options, ILogger<ErpSyncWorker> logger)
    : PeriodicWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, options.Value.SyncIntervalSeconds));

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<ErpSyncService>().SyncMasterDataAsync(ct);
}

public sealed class MachineWatchdogWorker(IServiceScopeFactory scopes, ILogger<MachineWatchdogWorker> logger)
    : PeriodicWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(10);

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<TelemetryService>().MarkStaleMachinesOfflineAsync(ct);
}

/// <summary>Runs the daily OEE rollup pipeline on a short cadence so today's partial row stays current.</summary>
public sealed class KpiRollupWorker(IServiceScopeFactory scopes, IOptions<ReportingOptions> options, ILogger<KpiRollupWorker> logger)
    : PeriodicWorker(scopes, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, options.Value.RollupIntervalMinutes));

    protected override Task RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<KpiRollupService>().RunAsync(ct);
}
