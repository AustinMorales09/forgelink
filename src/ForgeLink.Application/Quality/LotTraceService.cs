using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Inventory;
using ForgeLink.Contracts;
using ForgeLink.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Quality;

/// <summary>Forward and backward lot genealogy for recalls and customer complaints.</summary>
public sealed class LotTraceService(IAppDbContext db, InventoryService inventory)
{
    private const int MaxDepth = 10;

    public async Task<LotTraceDto> TraceAsync(string lotNumber, CancellationToken ct)
    {
        lotNumber = Text.Upper(lotNumber);
        var root = await db.Lots.AsNoTracking().FirstOrDefaultAsync(l => l.LotNumber == lotNumber, ct)
            ?? throw new NotFoundException("Lot", lotNumber);

        var components = await BuildAsync(root, null, upstream: true, depth: 0, [], ct);
        var whereUsed = await BuildAsync(root, null, upstream: false, depth: 0, [], ct);

        var checks = (await db.QualityChecks.AsNoTracking().Where(q => q.LotNumber == lotNumber)
            .OrderByDescending(q => q.CheckedAtUtc).ToListAsync(ct)).Select(q => q.ToDto()).ToList();

        var onHand = (await inventory.QueryAsync(root.Sku, null, ct)).Where(b => b.LotNumber == lotNumber).ToList();
        return new LotTraceDto(lotNumber, components, whereUsed, checks, onHand);
    }

    private async Task<LotTraceNodeDto> BuildAsync(Lot lot, decimal? linkQty, bool upstream, int depth, HashSet<string> visited, CancellationToken ct)
    {
        var children = new List<LotTraceNodeDto>();
        if (depth < MaxDepth && visited.Add(lot.LotNumber))
        {
            var links = upstream
                ? await db.LotGenealogy.AsNoTracking().Where(g => g.ChildLot == lot.LotNumber).ToListAsync(ct)
                : await db.LotGenealogy.AsNoTracking().Where(g => g.ParentLot == lot.LotNumber).ToListAsync(ct);

            var related = links.Select(l => upstream ? l.ParentLot : l.ChildLot).Distinct().ToList();
            var lots = await db.Lots.AsNoTracking().Where(l => related.Contains(l.LotNumber)).ToDictionaryAsync(l => l.LotNumber, ct);

            foreach (var group in links.GroupBy(l => upstream ? l.ParentLot : l.ChildLot))
            {
                if (lots.TryGetValue(group.Key, out var next))
                    children.Add(await BuildAsync(next, group.Sum(g => g.Quantity), upstream, depth + 1, visited, ct));
            }
        }

        return new LotTraceNodeDto(lot.LotNumber, lot.Sku, lot.Status.ToString(), lot.WorkOrderNumber, linkQty, children);
    }
}
