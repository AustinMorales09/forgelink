namespace ForgeLink.Contracts;

// ---------- Production ----------

public sealed record WorkOrderDto(
    string Number, string Sku, string ItemDescription, string Uom, decimal? UnitWeightKg, string Line,
    decimal QuantityOrdered, decimal QuantityCompleted, decimal QuantityScrapped, decimal QuantityRemaining,
    string Status, DateTime DueDateUtc, string? CurrentMachine, int Version);

public sealed record StartWorkOrderRequest(string MachineCode, string? EmployeeBadge);

/// <param name="Complete">True to complete the order, false to pause it.</param>
public sealed record StopWorkOrderRequest(bool Complete, string? EmployeeBadge);

public sealed record ConsumedLotDto(string LotNumber, decimal Quantity);

public sealed record ReportProductionRequest(
    decimal GoodQuantity,
    decimal ScrapQuantity,
    string? ScrapReason = null,
    string? OutputLotNumber = null,
    IReadOnlyList<ConsumedLotDto>? ConsumedLots = null,
    string? EmployeeBadge = null);

public sealed record ProductionReportResult(
    string WorkOrderNumber, string OutputLotNumber, decimal QuantityCompleted, decimal QuantityRemaining,
    string Status, Guid TransactionId);

// ---------- Inventory ----------

public sealed record InventoryBalanceDto(string Sku, string Location, string LotNumber, decimal Quantity, string LotStatus);

public sealed record MoveInventoryRequest(string Sku, string LotNumber, string FromLocation, string ToLocation, decimal Quantity);

public sealed record AdjustInventoryRequest(string Sku, string LotNumber, string Location, decimal QuantityDelta, string ReasonCode);

public sealed record InventoryTransactionDto(
    Guid Id, string Type, string Sku, string LotNumber, decimal Quantity, string? FromLocation, string? ToLocation,
    string? Reference, string? ReasonCode, string UserName, DateTime OccurredAtUtc);

public sealed record SubmitCycleCountRequest(string Sku, string Location, string LotNumber, decimal CountedQuantity);

public sealed record CycleCountDto(
    Guid Id, string Sku, string Location, string LotNumber, decimal SystemQuantity, decimal CountedQuantity,
    decimal Variance, string Status, bool WithinTolerance, string CountedBy, string? ApprovedBy, DateTime CountedAtUtc);

// ---------- Quality & traceability ----------

public sealed record RecordQualityCheckRequest(
    string WorkOrderNumber, string LotNumber, string Characteristic,
    decimal Nominal, decimal LowerLimit, decimal UpperLimit, decimal Measured);

public sealed record QualityCheckDto(
    Guid Id, string WorkOrderNumber, string LotNumber, string Characteristic, decimal Nominal,
    decimal LowerLimit, decimal UpperLimit, decimal Measured, string Result, string Inspector, DateTime CheckedAtUtc);

public sealed record LotTraceNodeDto(
    string LotNumber, string Sku, string Status, string? WorkOrderNumber, decimal? LinkQuantity,
    IReadOnlyList<LotTraceNodeDto> Children);

/// <param name="Components">Upstream genealogy: what went into this lot.</param>
/// <param name="WhereUsed">Downstream genealogy: what this lot went into (recall scope).</param>
public sealed record LotTraceDto(
    string LotNumber, LotTraceNodeDto Components, LotTraceNodeDto WhereUsed,
    IReadOnlyList<QualityCheckDto> QualityChecks, IReadOnlyList<InventoryBalanceDto> OnHand);

// ---------- Equipment / OT ----------

public static class MachineSignalTypes
{
    public const string State = "state";
    public const string Count = "count";
    public const string Heartbeat = "heartbeat";
}

/// <summary>Normalised OT signal, whether it arrived over MQTT, HTTP or a PLC gateway.</summary>
public sealed record MachineSignalDto(
    string MachineCode, string SignalType, string? State = null, string? Reason = null,
    int? GoodDelta = null, int? RejectDelta = null, DateTime? TimestampUtc = null);

public sealed record MachineStatusDto(
    string Code, string Line, string Description, string State, string? Reason,
    DateTime StateChangedAtUtc, DateTime? LastHeartbeatUtc, string? CurrentWorkOrder);

// ---------- KPIs & integration health ----------

public sealed record OeeDto(double Availability, double Performance, double Quality, double Oee);

public sealed record MachineKpiDto(string MachineCode, string State, OeeDto Oee, double DowntimeMinutes, int GoodCount, int RejectCount);

public sealed record DowntimeReasonDto(string ReasonCode, double Minutes, int Occurrences);

public sealed record ThroughputPointDto(DateTime HourUtc, int Good, int Reject);

public sealed record SyncStateDto(string Name, DateTime WatermarkUtc, DateTime? LastRunUtc, DateTime? LastSuccessUtc, int RecordsLastRun, string? LastError);

public sealed record IntegrationHealthDto(
    int Pending, int DeadLettered, double? OldestPendingMinutes, int SentLastHour, IReadOnlyList<SyncStateDto> Syncs);

public sealed record LineKpiDto(
    string Line, DateTime FromUtc, DateTime ToUtc, OeeDto Oee, IReadOnlyList<MachineKpiDto> Machines,
    IReadOnlyList<DowntimeReasonDto> DowntimePareto, IReadOnlyList<ThroughputPointDto> Throughput,
    double? InventoryAccuracyPercent, int CycleCounts, IntegrationHealthDto Integration);

public sealed record OutboxMessageDto(
    long Id, string MessageType, string PartitionKey, string Status, int Attempts, DateTime CreatedAtUtc,
    DateTime NextAttemptAtUtc, DateTime? SentAtUtc, string? LastError, string? ErpReference, string IdempotencyKey,
    string? CreatedBy, string Payload);

public sealed record AuditEntryDto(long Id, string EntityType, string EntityKey, string Action, string UserName, DateTime TimestampUtc, string Changes);

/// <summary>Inbound ERP event (webhook). Signed with HMAC-SHA256; see <see cref="WebhookSignature"/>.</summary>
public sealed record ErpWebhookEvent(string EventType, string? WorkOrderNumber, string? Sku);
