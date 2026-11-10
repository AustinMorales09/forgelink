using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Integration;
using ForgeLink.Contracts;
using ForgeLink.Domain;
using ForgeLink.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ForgeLink.Application.Inventory;

/// <summary>Material movement, adjustments and cycle counting, each mirrored to the ERP through the outbox.</summary>
public sealed class InventoryService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, IShopFloorNotifier notifier, IOptions<ForgeLinkOptions> options)
{
    public static readonly IReadOnlySet<string> AdjustmentReasonCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CYCLE_COUNT", "DAMAGE", "FOUND", "LOST", "CORRECTION", "SCRAP" };

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<InventoryBalanceDto>> QueryAsync(string? sku, string? location, CancellationToken ct)
    {
        var query = db.InventoryBalances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(sku)) query = query.Where(b => b.Sku == Text.Upper(sku));
        if (!string.IsNullOrWhiteSpace(location)) query = query.Where(b => b.Location == Text.Upper(location));

        var balances = await query.Where(b => b.Quantity != 0)
            .OrderBy(b => b.Sku).ThenBy(b => b.Location).ThenBy(b => b.LotNumber)
            .Take(1000).ToListAsync(ct);

        var lotNumbers = balances.Select(b => b.LotNumber).Where(l => l != "").Distinct().ToList();
        var lotStatus = await db.Lots.AsNoTracking().Where(l => lotNumbers.Contains(l.LotNumber))
            .ToDictionaryAsync(l => l.LotNumber, l => l.Status.ToString(), ct);

        return balances
            .Select(b => new InventoryBalanceDto(b.Sku, b.Location, b.LotNumber, b.Quantity, lotStatus.GetValueOrDefault(b.LotNumber, nameof(LotStatus.Available))))
            .ToList();
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> RecentTransactionsAsync(int take, CancellationToken ct) =>
        (await db.InventoryTransactions.AsNoTracking().OrderByDescending(t => t.OccurredAtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct))
        .Select(t => t.ToDto()).ToList();

    public async Task<InventoryTransactionDto> MoveAsync(MoveInventoryRequest request, CancellationToken ct)
    {
        var now = Now;
        var sku = Text.Upper(request.Sku);
        var lotNumber = Text.Upper(request.LotNumber);
        var from = Text.Upper(request.FromLocation);
        var to = Text.Upper(request.ToLocation);

        if (request.Quantity <= 0) throw new DomainException("validation", "Quantity must be greater than zero.");
        if (from == to) throw new DomainException("validation", "From and to locations must differ.");
        if (to.Length == 0) throw new DomainException("validation", "Destination location is required.");

        var item = await db.Items.FirstOrDefaultAsync(i => i.Sku == sku, ct) ?? throw new NotFoundException("Item", sku);
        if (item.IsLotControlled && lotNumber.Length == 0)
            throw new DomainException("validation", $"{sku} is lot-controlled; scan the lot number.");

        if (lotNumber.Length > 0)
        {
            var lot = await db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber, ct) ?? throw new NotFoundException("Lot", lotNumber);
            lot.EnsureUsable(to);
        }

        var source = await FindBalanceAsync(db, sku, from, lotNumber, ct)
            ?? throw new DomainException("inventory.insufficient", $"No {sku} lot '{lotNumber}' at {from}.");
        source.Remove(request.Quantity);

        var target = await FindBalanceAsync(db, sku, to, lotNumber, ct);
        if (target is null)
        {
            target = new InventoryBalance(sku, to, lotNumber);
            db.InventoryBalances.Add(target);
        }
        target.Add(request.Quantity);

        var tx = InventoryTransaction.Create(InventoryTransactionType.Move, sku, lotNumber, request.Quantity, from, to, null, null, user.Name, now);
        db.InventoryTransactions.Add(tx);
        db.EnqueueErp(ErpMessageTypes.InventoryTransfer, $"inv:{sku}",
            new InventoryTransferPayload(tx.Id, sku, lotNumber, request.Quantity, from, to, user.Name, now),
            tx.Id.ToString("N"), now, user.Name);

        await db.SaveChangesAsync(ct);
        await notifier.InventoryChangedAsync(sku, ct);
        return tx.ToDto();
    }

    public async Task<InventoryTransactionDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken ct)
    {
        if (request.QuantityDelta == 0) throw new DomainException("validation", "Adjustment quantity cannot be zero.");
        if (!AdjustmentReasonCodes.Contains(request.ReasonCode ?? ""))
            throw new DomainException("validation", $"Reason code must be one of: {string.Join(", ", AdjustmentReasonCodes)}.");

        var tx = await ApplyAdjustmentAsync(Text.Upper(request.Sku), Text.Upper(request.Location), Text.Upper(request.LotNumber),
            request.QuantityDelta, Text.Upper(request.ReasonCode), reference: null, ct);
        await db.SaveChangesAsync(ct);
        await notifier.InventoryChangedAsync(tx.Sku, ct);
        return tx.ToDto();
    }

    public async Task<CycleCountDto> SubmitCycleCountAsync(SubmitCycleCountRequest request, CancellationToken ct)
    {
        var sku = Text.Upper(request.Sku);
        var location = Text.Upper(request.Location);
        var lotNumber = Text.Upper(request.LotNumber);
        _ = await db.Items.FirstOrDefaultAsync(i => i.Sku == sku, ct) ?? throw new NotFoundException("Item", sku);

        var balance = await FindBalanceAsync(db, sku, location, lotNumber, ct);
        var count = CycleCount.Record(sku, location, lotNumber, balance?.Quantity ?? 0, request.CountedQuantity,
            user.Name, options.Value.CycleCountTolerancePercent, Now);
        db.CycleCounts.Add(count);

        if (count.Status == CycleCountStatus.Posted && count.Variance != 0)
            await ApplyAdjustmentAsync(sku, location, lotNumber, count.Variance, "CYCLE_COUNT", $"CC-{count.Id:N}", ct);

        await db.SaveChangesAsync(ct);
        return count.ToDto();
    }

    public async Task<IReadOnlyList<CycleCountDto>> PendingCycleCountsAsync(CancellationToken ct) =>
        (await db.CycleCounts.AsNoTracking().Where(c => c.Status == CycleCountStatus.PendingApproval)
            .OrderBy(c => c.CountedAtUtc).ToListAsync(ct)).Select(c => c.ToDto()).ToList();

    public async Task<CycleCountDto> ReviewCycleCountAsync(Guid id, bool approve, CancellationToken ct)
    {
        var count = await db.CycleCounts.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Cycle count", id.ToString());
        if (approve)
        {
            count.Approve(user.Name, Now);
            if (count.Variance != 0)
                await ApplyAdjustmentAsync(count.Sku, count.Location, count.LotNumber, count.Variance, "CYCLE_COUNT", $"CC-{count.Id:N}", ct);
        }
        else
        {
            count.Reject(user.Name);
        }

        await db.SaveChangesAsync(ct);
        await notifier.InventoryChangedAsync(count.Sku, ct);
        return count.ToDto();
    }

    private async Task<InventoryTransaction> ApplyAdjustmentAsync(
        string sku, string location, string lotNumber, decimal delta, string reasonCode, string? reference, CancellationToken ct)
    {
        var now = Now;
        var balance = await FindBalanceAsync(db, sku, location, lotNumber, ct);
        if (balance is null)
        {
            if (delta < 0) throw new DomainException("inventory.insufficient", $"No {sku} lot '{lotNumber}' at {location} to adjust down.");
            balance = new InventoryBalance(sku, location, lotNumber);
            db.InventoryBalances.Add(balance);
        }
        balance.Adjust(delta);

        var tx = InventoryTransaction.Create(InventoryTransactionType.Adjustment, sku, lotNumber, delta, null, location, reference, reasonCode, user.Name, now);
        db.InventoryTransactions.Add(tx);
        db.EnqueueErp(ErpMessageTypes.InventoryAdjustment, $"inv:{sku}",
            new InventoryAdjustmentPayload(tx.Id, sku, lotNumber, location, delta, reasonCode, reference, user.Name, now),
            tx.Id.ToString("N"), now, user.Name);
        return tx;
    }

    /// <summary>Finds a balance row, including rows added earlier in the same unit of work but not yet saved.</summary>
    internal static async Task<InventoryBalance?> FindBalanceAsync(IAppDbContext db, string sku, string location, string lotNumber, CancellationToken ct)
    {
        sku = Text.Upper(sku);
        location = Text.Upper(location);
        lotNumber = Text.Upper(lotNumber);
        return db.InventoryBalances.Local.FirstOrDefault(b => b.Sku == sku && b.Location == location && b.LotNumber == lotNumber)
            ?? await db.InventoryBalances.FirstOrDefaultAsync(b => b.Sku == sku && b.Location == location && b.LotNumber == lotNumber, ct);
    }
}
