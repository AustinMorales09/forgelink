using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Application.Integration;

/// <summary>
/// Inbound ERP master data: items and released work orders.
/// Two paths share the same upsert logic: a scheduled incremental batch (watermark on ModifiedAtUtc) and
/// an event-driven webhook for a single work order. The batch is the safety net if a webhook is lost.
/// </summary>
public sealed class ErpSyncService(IAppDbContext db, IErpClient erp, TimeProvider clock, ILogger<ErpSyncService> logger)
{
    public const string ItemsFeed = "erp.items";
    public const string WorkOrdersFeed = "erp.work-orders";

    public sealed record SyncSummary(int Items, int WorkOrders, IReadOnlyList<string> Conflicts);

    public async Task<SyncSummary> SyncMasterDataAsync(CancellationToken ct)
    {
        var items = await RunFeedAsync(ItemsFeed, async (state, token) =>
        {
            var changed = await erp.GetItemsChangedSinceAsync(state.WatermarkUtc, token);
            foreach (var item in changed) await UpsertItemAsync(item, token);
            return (changed.Count, changed.Count == 0 ? state.WatermarkUtc : changed.Max(i => i.ModifiedAtUtc), new List<string>());
        }, ct);

        var orders = await RunFeedAsync(WorkOrdersFeed, async (state, token) =>
        {
            var changed = await erp.GetWorkOrdersChangedSinceAsync(state.WatermarkUtc, token);
            var conflicts = new List<string>();
            foreach (var wo in changed)
            {
                var conflict = await UpsertWorkOrderAsync(wo, token);
                if (conflict is not null) conflicts.Add(conflict);
            }
            return (changed.Count, changed.Count == 0 ? state.WatermarkUtc : changed.Max(w => w.ModifiedAtUtc), conflicts);
        }, ct);

        return new SyncSummary(items.Count, orders.Count, orders.Conflicts);
    }

    /// <summary>Webhook path: fetch the authoritative record rather than trusting the event body.</summary>
    public async Task<bool> SyncWorkOrderAsync(string number, CancellationToken ct)
    {
        var wo = await erp.GetWorkOrderAsync(number, ct);
        if (wo is null)
        {
            logger.LogWarning("ERP webhook referenced work order {Number}, but the ERP does not return it", number);
            return false;
        }

        if (!await db.Items.AnyAsync(i => i.Sku == Text.Upper(wo.Sku), ct))
        {
            // Item may have been created in the same ERP transaction; pull items first.
            var items = await erp.GetItemsChangedSinceAsync(wo.ModifiedAtUtc.AddDays(-1), ct);
            foreach (var item in items) await UpsertItemAsync(item, ct);
        }

        var conflict = await UpsertWorkOrderAsync(wo, ct);
        await db.SaveChangesAsync(ct);
        if (conflict is not null) logger.LogWarning("{Conflict}", conflict);
        return true;
    }

    private async Task<(int Count, List<string> Conflicts)> RunFeedAsync(
        string feed, Func<SyncState, CancellationToken, Task<(int Count, DateTime Watermark, List<string> Conflicts)>> work, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Name == feed, ct);
        if (state is null)
        {
            state = new SyncState(feed);
            db.SyncStates.Add(state);
        }

        try
        {
            var (count, watermark, conflicts) = await work(state, ct);
            state.Succeeded(watermark, count, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            if (count > 0) logger.LogInformation("ERP sync {Feed}: {Count} records, watermark {Watermark:o}", feed, count, watermark);
            return (count, conflicts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ERP sync {Feed} failed", feed);
            // Discard partial upserts from this feed, keep the failure record.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is not SyncState).ToList())
                entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
            state.Failed(ex.Message, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            return (0, []);
        }
    }

    private async Task UpsertItemAsync(ErpItem source, CancellationToken ct)
    {
        var sku = Text.Upper(source.Sku);
        var item = db.Items.Local.FirstOrDefault(i => i.Sku == sku) ?? await db.Items.FirstOrDefaultAsync(i => i.Sku == sku, ct);
        if (item is null)
            db.Items.Add(new Item(sku, source.Description, source.Uom, source.UnitWeightKg, source.LotControlled, source.ModifiedAtUtc));
        else
            item.UpdateFromErp(source.Description, source.Uom, source.UnitWeightKg, source.LotControlled, source.ModifiedAtUtc);
    }

    /// <returns>A conflict description when the ERP change could not be applied automatically.</returns>
    private async Task<string?> UpsertWorkOrderAsync(ErpWorkOrder source, CancellationToken ct)
    {
        var number = Text.Upper(source.Number);
        var cancelled = string.Equals(source.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
        var wo = db.WorkOrders.Local.FirstOrDefault(w => w.Number == number) ?? await db.WorkOrders.FirstOrDefaultAsync(w => w.Number == number, ct);

        if (wo is null)
        {
            if (cancelled || !string.Equals(source.Status, "Released", StringComparison.OrdinalIgnoreCase)) return null;
            db.WorkOrders.Add(new WorkOrder(number, Text.Upper(source.Sku), source.QuantityOrdered, Text.Upper(source.Line), source.DueDateUtc));
            return null;
        }

        return wo.ApplyErpUpdate(source.QuantityOrdered, source.DueDateUtc, cancelled)
            ? null
            : $"ERP cancelled work order {number} while it is {wo.Status} on the floor; supervisor action required.";
    }
}
