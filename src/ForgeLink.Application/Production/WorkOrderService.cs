using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Inventory;
using ForgeLink.Contracts;
using ForgeLink.Domain;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Production;

/// <summary>Production execution: start/pause/complete, labor capture and production reporting with backflush.</summary>
public sealed class WorkOrderService(IAppDbContext db, ICurrentUser user, TimeProvider clock, IShopFloorNotifier notifier)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<WorkOrderDto>> ListAsync(string? line, bool includeClosed, CancellationToken ct)
    {
        var query = db.WorkOrders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(line)) query = query.Where(w => w.Line == Text.Upper(line));
        if (!includeClosed) query = query.Where(w => w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Cancelled);

        var orders = await query.OrderBy(w => w.DueDateUtc).ThenBy(w => w.Number).Take(500).ToListAsync(ct);
        var items = await LoadItemsAsync(orders.Select(o => o.Sku), ct);
        return orders.Select(o => o.ToDto(items.GetValueOrDefault(o.Sku))).ToList();
    }

    public async Task<WorkOrderDto?> GetAsync(string number, CancellationToken ct)
    {
        var wo = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Number == Text.Upper(number), ct);
        if (wo is null) return null;
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Sku == wo.Sku, ct);
        return wo.ToDto(item);
    }

    public async Task<WorkOrderDto> StartAsync(string number, StartWorkOrderRequest request, CancellationToken ct)
    {
        var wo = await LoadAsync(number, ct);
        var machineCode = Text.Upper(request.MachineCode);
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Code == machineCode, ct)
            ?? throw new NotFoundException("Machine", machineCode);
        if (machine.Line != wo.Line)
            throw new DomainException("workorder.wrong-line", $"Work order {wo.Number} is scheduled on line {wo.Line}, but {machine.Code} is on {machine.Line}.");
        if (machine.CurrentWorkOrder is { } running && running != wo.Number)
            throw new DomainException("machine.busy", $"{machine.Code} is running {running}. Pause or complete it first.");

        wo.Start(machine.Code, Now);
        machine.AssignWorkOrder(wo.Number);

        if (!string.IsNullOrWhiteSpace(request.EmployeeBadge))
        {
            var badge = Text.Upper(request.EmployeeBadge);
            // An operator can only be clocked on to one job at a time.
            await ClockOutAsync(e => e.EmployeeBadge == badge, ct);
            db.LaborEntries.Add(new LaborEntry(badge, wo.Number, machine.Code, Now));
        }

        await db.SaveChangesAsync(ct);
        await notifier.WorkOrderChangedAsync(wo.Number, ct);
        await notifier.MachineChangedAsync(machine.Code, ct);
        return (await GetAsync(wo.Number, ct))!;
    }

    public async Task<WorkOrderDto> StopAsync(string number, StopWorkOrderRequest request, CancellationToken ct)
    {
        var wo = await LoadAsync(number, ct);
        var machineCode = wo.CurrentMachine;

        if (request.Complete) wo.Complete(Now);
        else wo.Pause();

        if (machineCode is not null)
        {
            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Code == machineCode, ct);
            machine?.AssignWorkOrder(null);
        }

        var badge = string.IsNullOrWhiteSpace(request.EmployeeBadge) ? null : Text.Upper(request.EmployeeBadge);
        await ClockOutAsync(e => e.WorkOrderNumber == wo.Number && (badge == null || e.EmployeeBadge == badge), ct);

        await db.SaveChangesAsync(ct);
        await notifier.WorkOrderChangedAsync(wo.Number, ct);
        if (machineCode is not null) await notifier.MachineChangedAsync(machineCode, ct);
        return (await GetAsync(wo.Number, ct))!;
    }

    /// <summary>
    /// Reports good/scrap quantity. In ONE transaction: updates the work order, backflushes consumed lots from line WIP,
    /// receives output into the line's output location, records genealogy, and stages the ERP production receipt.
    /// </summary>
    public async Task<ProductionReportResult> ReportProductionAsync(string number, ReportProductionRequest request, CancellationToken ct)
    {
        var now = Now;
        var wo = await LoadAsync(number, ct);
        var item = await db.Items.FirstOrDefaultAsync(i => i.Sku == wo.Sku, ct) ?? throw new NotFoundException("Item", wo.Sku);

        wo.ReportProduction(request.GoodQuantity, request.ScrapQuantity);
        if (request.ScrapQuantity > 0 && string.IsNullOrWhiteSpace(request.ScrapReason))
            throw new DomainException("validation", "A scrap reason is required when reporting scrap.");

        var transactionId = Guid.NewGuid();
        var lotNumber = string.IsNullOrWhiteSpace(request.OutputLotNumber)
            ? LotNumbering.ForWorkOrder(wo.Number, now)
            : Text.Upper(request.OutputLotNumber);

        var lot = await db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber, ct);
        if (lot is null)
        {
            lot = new Lot(lotNumber, wo.Sku, wo.Number, now);
            db.Lots.Add(lot);
        }
        else if (lot.Sku != wo.Sku)
        {
            throw new DomainException("lot.sku-mismatch", $"Lot {lotNumber} belongs to {lot.Sku}, not {wo.Sku}.");
        }

        var wip = Locations.LineWip(wo.Line);
        var output = Locations.LineOutput(wo.Line);
        var consumed = request.ConsumedLots ?? [];

        foreach (var c in consumed)
        {
            var componentLot = await db.Lots.FirstOrDefaultAsync(l => l.LotNumber == Text.Upper(c.LotNumber), ct)
                ?? throw new NotFoundException("Lot", c.LotNumber);
            componentLot.EnsureUsable();

            var balance = await InventoryService.FindBalanceAsync(db, componentLot.Sku, wip, componentLot.LotNumber, ct)
                ?? throw new DomainException("inventory.insufficient", $"Lot {componentLot.LotNumber} is not staged at {wip}.");
            balance.Remove(c.Quantity);

            db.InventoryTransactions.Add(InventoryTransaction.Create(
                InventoryTransactionType.Issue, componentLot.Sku, componentLot.LotNumber, c.Quantity, wip, null, wo.Number, null, user.Name, now));
            db.LotGenealogy.Add(new LotGenealogyLink(componentLot.LotNumber, lotNumber, c.Quantity, wo.Number, now));
        }

        if (request.GoodQuantity > 0)
        {
            var balanceLot = item.IsLotControlled ? lotNumber : "";
            var balance = await InventoryService.FindBalanceAsync(db, wo.Sku, output, balanceLot, ct);
            if (balance is null)
            {
                balance = new InventoryBalance(wo.Sku, output, balanceLot);
                db.InventoryBalances.Add(balance);
            }
            balance.Add(request.GoodQuantity);
            db.InventoryTransactions.Add(InventoryTransaction.Create(
                InventoryTransactionType.ProductionReceipt, wo.Sku, lotNumber, request.GoodQuantity, null, output, wo.Number, null, user.Name, now));
        }

        if (request.ScrapQuantity > 0)
        {
            db.InventoryTransactions.Add(InventoryTransaction.Create(
                InventoryTransactionType.Scrap, wo.Sku, lotNumber, request.ScrapQuantity, null, null, wo.Number, Text.Upper(request.ScrapReason), user.Name, now));
        }

        db.EnqueueErp(
            ErpMessageTypes.ProductionReceipt,
            partitionKey: $"wo:{wo.Number}",
            new ProductionReceiptPayload(transactionId, wo.Number, wo.Sku, lotNumber, request.GoodQuantity, request.ScrapQuantity,
                request.ScrapReason, output, consumed, user.Name, now),
            idempotencyKey: transactionId.ToString("N"),
            now, user.Name);

        await db.SaveChangesAsync(ct);
        await notifier.WorkOrderChangedAsync(wo.Number, ct);
        await notifier.InventoryChangedAsync(wo.Sku, ct);

        return new ProductionReportResult(wo.Number, lotNumber, wo.QuantityCompleted, wo.QuantityRemaining, wo.Status.ToString(), transactionId);
    }

    private async Task ClockOutAsync(System.Linq.Expressions.Expression<Func<LaborEntry, bool>> filter, CancellationToken ct)
    {
        var open = await db.LaborEntries.Where(e => e.EndUtc == null).Where(filter).ToListAsync(ct);
        foreach (var entry in open)
        {
            entry.ClockOut(Now);
            db.EnqueueErp(
                ErpMessageTypes.Labor,
                partitionKey: $"labor:{entry.EmployeeBadge}",
                new LaborPayload(entry.Id, entry.EmployeeBadge, entry.WorkOrderNumber, entry.MachineCode, entry.StartUtc, entry.EndUtc!.Value, entry.Hours),
                idempotencyKey: $"labor-{entry.Id:N}",
                Now, user.Name);
        }
    }

    private async Task<WorkOrder> LoadAsync(string number, CancellationToken ct) =>
        await db.WorkOrders.FirstOrDefaultAsync(w => w.Number == Text.Upper(number), ct)
        ?? throw new NotFoundException("Work order", number);

    private async Task<Dictionary<string, Item>> LoadItemsAsync(IEnumerable<string> skus, CancellationToken ct)
    {
        var list = skus.Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => list.Contains(i.Sku)).ToDictionaryAsync(i => i.Sku, ct);
    }
}
