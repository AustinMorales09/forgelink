using System.Text.Json;
using ForgeLink.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Client.Offline;

/// <summary>
/// Decorator that makes shop-floor commands survive network outages:
/// queries go straight to the server; commands are attempted online and, on a transient failure,
/// written to the local queue and reported to the UI as <see cref="QueuedForSyncException"/>.
/// <see cref="FlushAsync"/> replays the queue in order with the original idempotency keys.
/// </summary>
public sealed class StoreAndForwardForgeLinkApi(
    Func<ForgeLinkHttpClient> online, OfflineCommandQueue queue, ILogger<StoreAndForwardForgeLinkApi> logger) : IForgeLinkApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public OfflineCommandQueue Queue => queue;

    /// <summary>Raised when connectivity is gained or lost, as observed by real requests.</summary>
    public event EventHandler<bool>? ConnectivityChanged;

    public bool IsOnline { get; private set; } = true;

    // ---------- queries: pass-through ----------
    public Task<IReadOnlyList<MachineStatusDto>> GetMachinesAsync(string? line = null, CancellationToken ct = default) => Query(api => api.GetMachinesAsync(line, ct));
    public Task<LineKpiDto> GetLineKpisAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Query(api => api.GetLineKpisAsync(line, fromUtc, toUtc, ct));
    public Task<IntegrationHealthDto> GetIntegrationHealthAsync(CancellationToken ct = default) => Query(api => api.GetIntegrationHealthAsync(ct));
    public Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(string? line = null, CancellationToken ct = default) => Query(api => api.GetWorkOrdersAsync(line, ct));
    public Task<WorkOrderDto?> GetWorkOrderAsync(string number, CancellationToken ct = default) => Query(api => api.GetWorkOrderAsync(number, ct));
    public Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryAsync(string? sku = null, string? location = null, CancellationToken ct = default) => Query(api => api.GetInventoryAsync(sku, location, ct));
    public Task<IReadOnlyList<InventoryTransactionDto>> GetRecentTransactionsAsync(int take = 50, CancellationToken ct = default) => Query(api => api.GetRecentTransactionsAsync(take, ct));
    public Task<IReadOnlyList<CycleCountDto>> GetPendingCycleCountsAsync(CancellationToken ct = default) => Query(api => api.GetPendingCycleCountsAsync(ct));
    public Task<LotTraceDto> TraceLotAsync(string lotNumber, CancellationToken ct = default) => Query(api => api.TraceLotAsync(lotNumber, ct));

    // ---------- state changes that need the server's answer (not queued) ----------
    public Task<WorkOrderDto> StartWorkOrderAsync(string number, StartWorkOrderRequest request, CancellationToken ct = default) => Query(api => api.StartWorkOrderAsync(number, request, ct));
    public Task<WorkOrderDto> StopWorkOrderAsync(string number, StopWorkOrderRequest request, CancellationToken ct = default) => Query(api => api.StopWorkOrderAsync(number, request, ct));
    public Task<CycleCountDto> ApproveCycleCountAsync(Guid id, bool approve, CancellationToken ct = default) => Query(api => api.ApproveCycleCountAsync(id, approve, ct));

    // ---------- commands: store-and-forward ----------
    public Task<ProductionReportResult> ReportProductionAsync(string number, ReportProductionRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Command(CommandKinds.ReportProduction, number, request, idempotencyKey, (api, key) => api.ReportProductionAsync(number, request, key, ct),
            $"No connection: production report for {number} saved on this device and will sync automatically.", ct);

    public Task<InventoryTransactionDto> MoveInventoryAsync(MoveInventoryRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Command(CommandKinds.MoveInventory, null, request, idempotencyKey, (api, key) => api.MoveInventoryAsync(request, key, ct),
            $"No connection: move of {request.Quantity} {request.Sku} saved on this device and will sync automatically.", ct);

    public Task<CycleCountDto> SubmitCycleCountAsync(SubmitCycleCountRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Command(CommandKinds.SubmitCycleCount, null, request, idempotencyKey, (api, key) => api.SubmitCycleCountAsync(request, key, ct),
            "No connection: count saved on this device and will sync automatically.", ct);

    public Task<QualityCheckDto> RecordQualityCheckAsync(RecordQualityCheckRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Command(CommandKinds.RecordQualityCheck, null, request, idempotencyKey, (api, key) => api.RecordQualityCheckAsync(request, key, ct),
            "No connection: inspection saved on this device and will sync automatically.", ct);

    /// <summary>Replays queued commands oldest-first. Stops at the first transient failure to preserve order.</summary>
    public async Task<(int Sent, int Rejected, int Remaining)> FlushAsync(CancellationToken ct = default)
    {
        if (!await _flushGate.WaitAsync(0, ct)) return (0, 0, await queue.CountAsync(ct: ct));
        int sent = 0, rejected = 0;
        try
        {
            foreach (var cmd in await queue.ListAsync(QueuedCommandState.Queued, ct))
            {
                try
                {
                    await ReplayAsync(online(), cmd, ct);
                    await queue.RemoveAsync(cmd.Id, ct);
                    sent++;
                    SetOnline(true);
                }
                catch (Exception ex) when (IsTransient(ex, ct))
                {
                    await queue.RecordFailureAsync(cmd.Id, ex.Message, ct);
                    SetOnline(false);
                    break;
                }
                catch (ForgeLinkApiException ex)
                {
                    // The server is reachable but says no (e.g. lot went on hold while we were offline).
                    logger.LogWarning("Queued {Kind} {Key} rejected on replay: {Error}", cmd.Kind, cmd.IdempotencyKey, ex.Message);
                    await queue.MarkRejectedAsync(cmd.Id, $"{ex.StatusCode}: {ex.Message}", ct);
                    rejected++;
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }

        if (sent + rejected > 0) logger.LogInformation("Offline queue flushed: {Sent} sent, {Rejected} rejected", sent, rejected);
        return (sent, rejected, await queue.CountAsync(ct: ct));
    }

    private static Task ReplayAsync(ForgeLinkHttpClient api, QueuedCommand cmd, CancellationToken ct) => cmd.Kind switch
    {
        CommandKinds.ReportProduction => api.ReportProductionAsync(cmd.Target!, Read<ReportProductionRequest>(cmd), cmd.IdempotencyKey, ct),
        CommandKinds.MoveInventory => api.MoveInventoryAsync(Read<MoveInventoryRequest>(cmd), cmd.IdempotencyKey, ct),
        CommandKinds.SubmitCycleCount => api.SubmitCycleCountAsync(Read<SubmitCycleCountRequest>(cmd), cmd.IdempotencyKey, ct),
        CommandKinds.RecordQualityCheck => api.RecordQualityCheckAsync(Read<RecordQualityCheckRequest>(cmd), cmd.IdempotencyKey, ct),
        _ => throw new ForgeLinkApiException(400, "Unknown command", $"Unknown queued command kind '{cmd.Kind}'.", "unknown-command"),
    };

    private static T Read<T>(QueuedCommand cmd) => JsonSerializer.Deserialize<T>(cmd.PayloadJson, Json)!;

    private async Task<T> Query<T>(Func<ForgeLinkHttpClient, Task<T>> call)
    {
        try
        {
            var result = await call(online());
            SetOnline(true);
            return result;
        }
        catch (Exception ex) when (IsTransient(ex, CancellationToken.None))
        {
            SetOnline(false);
            throw;
        }
    }

    private async Task<TResult> Command<TRequest, TResult>(
        string kind, string? target, TRequest request, string? idempotencyKey,
        Func<ForgeLinkHttpClient, string, Task<TResult>> send, string queuedMessage, CancellationToken ct)
    {
        var key = idempotencyKey ?? Guid.NewGuid().ToString("N");

        // Keep order: if older commands are still queued, this one must queue behind them.
        if (await queue.CountAsync(ct: ct) == 0)
        {
            try
            {
                var result = await send(online(), key);
                SetOnline(true);
                return result;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                logger.LogWarning("Command {Kind} failed transiently ({Error}); queuing for later", kind, ex.Message);
                SetOnline(false);
            }
        }

        await queue.EnqueueAsync(kind, target, JsonSerializer.Serialize(request, Json), key, ct);
        throw new QueuedForSyncException(queuedMessage, key);
    }

    private void SetOnline(bool online)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        ConnectivityChanged?.Invoke(this, online);
    }

    public static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        ForgeLinkApiException api => api.IsTransient,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException => !ct.IsCancellationRequested,
        _ => ex.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true, // circuit open, attempt timeout
    };
}

/// <summary>Background replay loop for generic-host apps (WPF). MAUI drives <see cref="StoreAndForwardForgeLinkApi.FlushAsync"/> from connectivity events.</summary>
public sealed class OfflineSyncWorker(StoreAndForwardForgeLinkApi api, ILogger<OfflineSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await api.FlushAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Offline flush failed"); }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
