namespace ForgeLink.Domain.Inventory;

public enum LotStatus
{
    Available,
    Hold,
    Rejected,
}

/// <summary>A traceable batch of material. Quality failures put the lot on hold, which blocks consumption and moves.</summary>
public class Lot : IAudited
{
    public const string QualityHoldLocationPrefix = "QA-";

    private Lot() { }

    public Lot(string lotNumber, string sku, string? workOrderNumber, DateTime createdAtUtc)
    {
        LotNumber = Guard.NotBlank(lotNumber, nameof(lotNumber)).ToUpperInvariant();
        Sku = Guard.NotBlank(sku, nameof(sku));
        WorkOrderNumber = workOrderNumber;
        CreatedAtUtc = Guard.Utc(createdAtUtc);
        Status = LotStatus.Available;
    }

    public string LotNumber { get; private set; } = "";
    public string Sku { get; private set; } = "";
    public string? WorkOrderNumber { get; private set; }
    public LotStatus Status { get; private set; }
    public string? HoldReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Hold(string reason)
    {
        if (Status == LotStatus.Rejected) return;
        Status = LotStatus.Hold;
        HoldReason = Guard.NotBlank(reason, nameof(reason));
    }

    public void Release()
    {
        if (Status != LotStatus.Hold)
            throw new DomainException("lot.not-on-hold", $"Lot {LotNumber} is {Status}, not on hold.");
        Status = LotStatus.Available;
        HoldReason = null;
    }

    /// <summary>Throws unless the lot can be consumed into production or moved to a normal location.</summary>
    public void EnsureUsable(string? destinationLocation = null)
    {
        if (Status == LotStatus.Available) return;
        var toQuarantine = destinationLocation?.StartsWith(QualityHoldLocationPrefix, StringComparison.OrdinalIgnoreCase) == true;
        if (toQuarantine) return;
        throw new DomainException("lot.on-hold", $"Lot {LotNumber} is on {Status} ({HoldReason}). It can only be moved to a {QualityHoldLocationPrefix}* location.");
    }
}

/// <summary>Genealogy edge: <see cref="ParentLot"/> (component) was consumed into <see cref="ChildLot"/> (output).</summary>
public class LotGenealogyLink
{
    private LotGenealogyLink() { }

    public LotGenealogyLink(string parentLot, string childLot, decimal quantity, string workOrderNumber, DateTime atUtc)
    {
        ParentLot = Guard.NotBlank(parentLot, nameof(parentLot)).ToUpperInvariant();
        ChildLot = Guard.NotBlank(childLot, nameof(childLot)).ToUpperInvariant();
        Quantity = Guard.Positive(quantity, nameof(quantity));
        WorkOrderNumber = workOrderNumber;
        LinkedAtUtc = Guard.Utc(atUtc);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string ParentLot { get; private set; } = "";
    public string ChildLot { get; private set; } = "";
    public decimal Quantity { get; private set; }
    public string WorkOrderNumber { get; private set; } = "";
    public DateTime LinkedAtUtc { get; private set; }
}
