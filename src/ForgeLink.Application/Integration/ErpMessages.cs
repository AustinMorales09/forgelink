using ForgeLink.Contracts;

namespace ForgeLink.Application.Integration;

/// <summary>Outbound ERP transaction types. Each maps to one ERP endpoint in the HTTP adapter.</summary>
public static class ErpMessageTypes
{
    public const string ProductionReceipt = "erp.production-receipt";
    public const string InventoryTransfer = "erp.inventory-transfer";
    public const string InventoryAdjustment = "erp.inventory-adjustment";
    public const string Labor = "erp.labor";
    public const string QualityHold = "erp.quality-hold";

    public static readonly IReadOnlyList<string> All = [ProductionReceipt, InventoryTransfer, InventoryAdjustment, Labor, QualityHold];
}

public sealed record ProductionReceiptPayload(
    Guid TransactionId, string WorkOrderNumber, string Sku, string LotNumber, decimal GoodQuantity, decimal ScrapQuantity,
    string? ScrapReason, string ToLocation, IReadOnlyList<ConsumedLotDto> Consumed, string ReportedBy, DateTime OccurredAtUtc);

public sealed record InventoryTransferPayload(
    Guid TransactionId, string Sku, string LotNumber, decimal Quantity, string FromLocation, string ToLocation, string User, DateTime OccurredAtUtc);

public sealed record InventoryAdjustmentPayload(
    Guid TransactionId, string Sku, string LotNumber, string Location, decimal QuantityDelta, string ReasonCode,
    string? Reference, string User, DateTime OccurredAtUtc);

public sealed record LaborPayload(
    Guid LaborEntryId, string EmployeeBadge, string WorkOrderNumber, string MachineCode, DateTime StartUtc, DateTime EndUtc, decimal Hours);

public sealed record QualityHoldPayload(
    Guid QualityCheckId, string LotNumber, string Sku, string WorkOrderNumber, string Characteristic,
    decimal Measured, decimal LowerLimit, decimal UpperLimit, string Inspector, DateTime OccurredAtUtc);
