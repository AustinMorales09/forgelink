using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Integration;
using ForgeLink.Contracts;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Quality;

/// <summary>In-process inspection. An out-of-spec result automatically places the lot on hold and notifies the ERP.</summary>
public sealed class QualityService(IAppDbContext db, ICurrentUser user, TimeProvider clock, IShopFloorNotifier notifier)
{
    public async Task<QualityCheckDto> RecordAsync(RecordQualityCheckRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lotNumber = Text.Upper(request.LotNumber);
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.LotNumber == lotNumber, ct) ?? throw new NotFoundException("Lot", lotNumber);

        var check = QualityCheck.Record(request.WorkOrderNumber, lotNumber, request.Characteristic,
            request.Nominal, request.LowerLimit, request.UpperLimit, request.Measured, user.Name, now);
        db.QualityChecks.Add(check);

        if (check.Result == QualityResult.Fail)
        {
            lot.Hold($"{check.Characteristic} measured {check.Measured} outside [{check.LowerLimit}, {check.UpperLimit}]");
            db.EnqueueErp(ErpMessageTypes.QualityHold, $"lot:{lot.LotNumber}",
                new QualityHoldPayload(check.Id, lot.LotNumber, lot.Sku, check.WorkOrderNumber, check.Characteristic,
                    check.Measured, check.LowerLimit, check.UpperLimit, user.Name, now),
                $"qh-{check.Id:N}", now, user.Name);
        }

        await db.SaveChangesAsync(ct);
        if (check.Result == QualityResult.Fail) await notifier.InventoryChangedAsync(lot.Sku, ct);
        return check.ToDto();
    }
}
