using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ForgeLink.Infrastructure.Persistence;

/// <summary>Relational model: keys, lengths, unique constraints and the indexes the hot queries rely on.</summary>
internal static class ModelConfiguration
{
    private const int Code = 40;
    private const int Name = 100;

    public static void Apply(ModelBuilder model)
    {
        model.Entity<Item>(e =>
        {
            e.HasKey(x => x.Sku);
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Uom).HasMaxLength(10);
            e.HasIndex(x => x.ModifiedAtUtc);
        });

        model.Entity<WorkOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Number).HasMaxLength(Code);
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.Line).HasMaxLength(20);
            e.Property(x => x.CurrentMachine).HasMaxLength(Code);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Line, x.Status });
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.Sku).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LaborEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EmployeeBadge).HasMaxLength(Code);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(Code);
            e.Property(x => x.MachineCode).HasMaxLength(Code);
            e.HasIndex(x => new { x.EmployeeBadge, x.EndUtc });
            e.HasIndex(x => x.WorkOrderNumber);
        });

        model.Entity<Lot>(e =>
        {
            e.HasKey(x => x.LotNumber);
            e.Property(x => x.LotNumber).HasMaxLength(Code);
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(Code);
            e.Property(x => x.HoldReason).HasMaxLength(400);
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.Sku).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LotGenealogyLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ParentLot).HasMaxLength(Code);
            e.Property(x => x.ChildLot).HasMaxLength(Code);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(Code);
            e.HasIndex(x => x.ParentLot);
            e.HasIndex(x => x.ChildLot);
        });

        model.Entity<InventoryBalance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.Location).HasMaxLength(Code);
            e.Property(x => x.LotNumber).HasMaxLength(Code);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Sku, x.Location, x.LotNumber }).IsUnique();
            e.HasIndex(x => x.Location);
        });

        model.Entity<InventoryTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.LotNumber).HasMaxLength(Code);
            e.Property(x => x.FromLocation).HasMaxLength(Code);
            e.Property(x => x.ToLocation).HasMaxLength(Code);
            e.Property(x => x.Reference).HasMaxLength(Name);
            e.Property(x => x.ReasonCode).HasMaxLength(Code);
            e.Property(x => x.UserName).HasMaxLength(Name);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => new { x.Sku, x.OccurredAtUtc });
        });

        model.Entity<CycleCount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(Code);
            e.Property(x => x.Location).HasMaxLength(Code);
            e.Property(x => x.LotNumber).HasMaxLength(Code);
            e.Property(x => x.CountedBy).HasMaxLength(Name);
            e.Property(x => x.ApprovedBy).HasMaxLength(Name);
            e.HasIndex(x => new { x.Status, x.CountedAtUtc });
        });

        model.Entity<QualityCheck>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(Code);
            e.Property(x => x.LotNumber).HasMaxLength(Code);
            e.Property(x => x.Characteristic).HasMaxLength(Name);
            e.Property(x => x.Inspector).HasMaxLength(Name);
            e.HasIndex(x => x.LotNumber);
        });

        model.Entity<Machine>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(Code);
            e.Property(x => x.Line).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.StateReason).HasMaxLength(Code);
            e.Property(x => x.CurrentWorkOrder).HasMaxLength(Code);
            e.HasIndex(x => x.Line);
        });

        model.Entity<DowntimeEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineCode).HasMaxLength(Code);
            e.Property(x => x.ReasonCode).HasMaxLength(Code);
            e.HasIndex(x => new { x.MachineCode, x.StartUtc });
            e.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineCode).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProductionCounter>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineCode).HasMaxLength(Code);
            e.HasIndex(x => new { x.MachineCode, x.BucketStartUtc }).IsUnique();
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.MessageType).HasMaxLength(Name);
            e.Property(x => x.PartitionKey).HasMaxLength(Name);
            e.Property(x => x.IdempotencyKey).HasMaxLength(Name);
            e.Property(x => x.ErpReference).HasMaxLength(Name);
            e.Property(x => x.CreatedBy).HasMaxLength(Name);
            e.Property(x => x.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
        });

        model.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EntityType).HasMaxLength(Name);
            e.Property(x => x.EntityKey).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(20);
            e.Property(x => x.UserName).HasMaxLength(Name);
            e.HasIndex(x => new { x.EntityType, x.EntityKey });
            e.HasIndex(x => x.TimestampUtc);
        });

        model.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(250);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        model.Entity<DailyMachineKpi>(e =>
        {
            e.HasKey(x => new { x.MachineCode, x.Day });
            e.Property(x => x.MachineCode).HasMaxLength(Code);
            e.Property(x => x.Line).HasMaxLength(20);
            e.Property(x => x.TopDowntimeReason).HasMaxLength(Code);
            e.HasIndex(x => new { x.Line, x.Day });
        });

        model.Entity<SyncState>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(Name);
            e.Property(x => x.LastError).HasMaxLength(1000);
        });
    }
}

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
