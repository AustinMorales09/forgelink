using ForgeLink.Contracts;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Abstractions;

/// <summary>Unit of work over the ForgeLink database. Implemented by the EF Core context in Infrastructure.</summary>
public interface IAppDbContext
{
    DbSet<Item> Items { get; }
    DbSet<WorkOrder> WorkOrders { get; }
    DbSet<LaborEntry> LaborEntries { get; }
    DbSet<Lot> Lots { get; }
    DbSet<LotGenealogyLink> LotGenealogy { get; }
    DbSet<InventoryBalance> InventoryBalances { get; }
    DbSet<InventoryTransaction> InventoryTransactions { get; }
    DbSet<CycleCount> CycleCounts { get; }
    DbSet<QualityCheck> QualityChecks { get; }
    DbSet<Machine> Machines { get; }
    DbSet<DowntimeEvent> DowntimeEvents { get; }
    DbSet<ProductionCounter> ProductionCounters { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<SyncState> SyncStates { get; }
    DbSet<DailyMachineKpi> DailyMachineKpis { get; }

    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ICurrentUser
{
    string Name { get; }
    bool IsInRole(string role);
}

/// <summary>Pushes change notifications to connected dashboards/HMIs (SignalR in the web host).</summary>
public interface IShopFloorNotifier
{
    Task MachineChangedAsync(string machineCode, CancellationToken ct = default);
    Task WorkOrderChangedAsync(string workOrderNumber, CancellationToken ct = default);
    Task InventoryChangedAsync(string sku, CancellationToken ct = default);
}

public sealed class NullShopFloorNotifier : IShopFloorNotifier
{
    public Task MachineChangedAsync(string machineCode, CancellationToken ct = default) => Task.CompletedTask;
    public Task WorkOrderChangedAsync(string workOrderNumber, CancellationToken ct = default) => Task.CompletedTask;
    public Task InventoryChangedAsync(string sku, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Downtime Pareto query. Swappable so SQL Server can use a tuned stored procedure.</summary>
public interface IDowntimeParetoQuery
{
    Task<IReadOnlyList<DowntimeReasonDto>> GetAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public static class Roles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Integration = "Integration";
    public const string Admin = "Admin";
}
