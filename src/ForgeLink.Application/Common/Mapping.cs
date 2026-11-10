using ForgeLink.Contracts;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Domain.Quality;

namespace ForgeLink.Application.Common;

public static class Mapping
{
    public static WorkOrderDto ToDto(this WorkOrder wo, Item? item) => new(
        wo.Number, wo.Sku, item?.Description ?? "", item?.Uom ?? "EA", item?.UnitWeightKg, wo.Line,
        wo.QuantityOrdered, wo.QuantityCompleted, wo.QuantityScrapped, wo.QuantityRemaining,
        wo.Status.ToString(), wo.DueDateUtc, wo.CurrentMachine, wo.Version);

    public static MachineStatusDto ToDto(this Machine m) => new(
        m.Code, m.Line, m.Description, m.State.ToString(), m.StateReason, m.StateChangedAtUtc, m.LastHeartbeatUtc, m.CurrentWorkOrder);

    public static InventoryTransactionDto ToDto(this InventoryTransaction t) => new(
        t.Id, t.Type.ToString(), t.Sku, t.LotNumber, t.Quantity, t.FromLocation, t.ToLocation, t.Reference, t.ReasonCode, t.UserName, t.OccurredAtUtc);

    public static CycleCountDto ToDto(this CycleCount c) => new(
        c.Id, c.Sku, c.Location, c.LotNumber, c.SystemQuantity, c.CountedQuantity, c.Variance, c.Status.ToString(),
        c.WithinTolerance, c.CountedBy, c.ApprovedBy, c.CountedAtUtc);

    public static QualityCheckDto ToDto(this QualityCheck q) => new(
        q.Id, q.WorkOrderNumber, q.LotNumber, q.Characteristic, q.Nominal, q.LowerLimit, q.UpperLimit, q.Measured,
        q.Result.ToString(), q.Inspector, q.CheckedAtUtc);

    public static OutboxMessageDto ToDto(this OutboxMessage m) => new(
        m.Id, m.MessageType, m.PartitionKey, m.Status.ToString(), m.Attempts, m.CreatedAtUtc, m.NextAttemptAtUtc,
        m.SentAtUtc, m.LastError, m.ErpReference, m.IdempotencyKey, m.CreatedBy, m.Payload);

    public static SyncStateDto ToDto(this SyncState s) => new(s.Name, s.WatermarkUtc, s.LastRunUtc, s.LastSuccessUtc, s.RecordsLastRun, s.LastError);

    public static AuditEntryDto ToDto(this AuditEntry a) => new(a.Id, a.EntityType, a.EntityKey, a.Action, a.UserName, a.TimestampUtc, a.Changes);

    public static OeeDto ToDto(this OeeResult r) => new(r.Availability, r.Performance, r.Quality, r.Oee);
}
