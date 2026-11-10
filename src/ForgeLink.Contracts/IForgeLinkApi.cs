namespace ForgeLink.Contracts;

/// <summary>
/// The ForgeLink application surface as seen by user interfaces. Implemented in-process for the
/// Blazor Server dashboard and over HTTP (with offline store-and-forward) for WPF and MAUI clients,
/// so the same Razor components run in both hosts.
/// </summary>
/// <remarks>
/// Commands accept an optional idempotency key. Retrying a command with the same key is always safe:
/// the server replays the original response instead of executing twice.
/// </remarks>
public interface IForgeLinkApi
{
    // Equipment & KPIs
    Task<IReadOnlyList<MachineStatusDto>> GetMachinesAsync(string? line = null, CancellationToken ct = default);
    Task<LineKpiDto> GetLineKpisAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<IntegrationHealthDto> GetIntegrationHealthAsync(CancellationToken ct = default);

    // Production
    Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(string? line = null, CancellationToken ct = default);
    Task<WorkOrderDto?> GetWorkOrderAsync(string number, CancellationToken ct = default);
    Task<WorkOrderDto> StartWorkOrderAsync(string number, StartWorkOrderRequest request, CancellationToken ct = default);
    Task<WorkOrderDto> StopWorkOrderAsync(string number, StopWorkOrderRequest request, CancellationToken ct = default);
    Task<ProductionReportResult> ReportProductionAsync(string number, ReportProductionRequest request, string? idempotencyKey = null, CancellationToken ct = default);

    // Inventory
    Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryAsync(string? sku = null, string? location = null, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryTransactionDto>> GetRecentTransactionsAsync(int take = 50, CancellationToken ct = default);
    Task<InventoryTransactionDto> MoveInventoryAsync(MoveInventoryRequest request, string? idempotencyKey = null, CancellationToken ct = default);
    Task<CycleCountDto> SubmitCycleCountAsync(SubmitCycleCountRequest request, string? idempotencyKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<CycleCountDto>> GetPendingCycleCountsAsync(CancellationToken ct = default);
    Task<CycleCountDto> ApproveCycleCountAsync(Guid id, bool approve, CancellationToken ct = default);

    // Quality & traceability
    Task<QualityCheckDto> RecordQualityCheckAsync(RecordQualityCheckRequest request, string? idempotencyKey = null, CancellationToken ct = default);
    Task<LotTraceDto> TraceLotAsync(string lotNumber, CancellationToken ct = default);
}

/// <summary>
/// Thrown by offline-capable clients when a command could not reach the server and was saved locally.
/// It will be replayed (with the same idempotency key) when connectivity returns. UIs should treat this as
/// "accepted, pending sync", not as an error.
/// </summary>
public sealed class QueuedForSyncException(string message, string idempotencyKey) : Exception(message)
{
    public string IdempotencyKey { get; } = idempotencyKey;
}
