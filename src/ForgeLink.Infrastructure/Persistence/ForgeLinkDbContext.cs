using System.Text.Json;
using ForgeLink.Application.Abstractions;
using ForgeLink.Domain;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ForgeLink.Infrastructure.Persistence;

public sealed class ForgeLinkDbContext(DbContextOptions<ForgeLinkDbContext> options, ICurrentUser? currentUser = null, TimeProvider? clock = null)
    : DbContext(options), IAppDbContext
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<LaborEntry> LaborEntries => Set<LaborEntry>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotGenealogyLink> LotGenealogy => Set<LotGenealogyLink>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<CycleCount> CycleCounts => Set<CycleCount>();
    public DbSet<QualityCheck> QualityChecks => Set<QualityCheck>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<DowntimeEvent> DowntimeEvents => Set<DowntimeEvent>();
    public DbSet<ProductionCounter> ProductionCounters => Set<ProductionCounter>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Quantities: 4 decimal places covers kg, metres and fractional UoMs.
        builder.Properties<decimal>().HavePrecision(18, 4);
        builder.Properties<decimal?>().HavePrecision(18, 4);
        // Everything is stored in UTC; make sure values come back flagged as UTC (SQLite loses Kind).
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        // Enums as strings: readable in SQL, safe against reordering.
        builder.Properties<WorkOrderStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<LotStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<InventoryTransactionType>().HaveConversion<string>().HaveMaxLength(30);
        builder.Properties<CycleCountStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<QualityResult>().HaveConversion<string>().HaveMaxLength(10);
        builder.Properties<MachineState>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<OutboxStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder model) => ModelConfiguration.Apply(model);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        ChangeTracker.DetectChanges();
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var user = currentUser?.Name ?? "system";
        var audits = new List<AuditEntry>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Modified && entry.Entity is IVersioned versioned)
                versioned.Version++;

            if (entry.Entity is IAudited && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                audits.Add(CreateAudit(entry, user, now));
        }

        AuditEntries.AddRange(audits);
    }

    private static AuditEntry CreateAudit(EntityEntry entry, string user, DateTime now)
    {
        var key = string.Join("|", entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString()));
        object changes = entry.State switch
        {
            EntityState.Added => entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue),
            EntityState.Deleted => entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue),
            _ => entry.Properties
                .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
                .ToDictionary(p => p.Metadata.Name, p => (object?)new { from = p.OriginalValue, to = p.CurrentValue }),
        };
        return new AuditEntry(entry.Metadata.ClrType.Name, key, entry.State.ToString(), user, now, JsonSerializer.Serialize(changes));
    }
}
