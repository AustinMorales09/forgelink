namespace ForgeLink.Domain.Inventory;

/// <summary>Item master. Owned by the ERP and replicated here by the master-data sync.</summary>
public class Item : IAudited
{
    private Item() { }

    public Item(string sku, string description, string uom, decimal? unitWeightKg, bool isLotControlled, DateTime modifiedAtUtc)
    {
        Sku = Guard.NotBlank(sku, nameof(sku)).ToUpperInvariant();
        UpdateFromErp(description, uom, unitWeightKg, isLotControlled, modifiedAtUtc);
    }

    public string Sku { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Uom { get; private set; } = "EA";
    public decimal? UnitWeightKg { get; private set; }
    public bool IsLotControlled { get; private set; }
    public DateTime ModifiedAtUtc { get; private set; }

    public void UpdateFromErp(string description, string uom, decimal? unitWeightKg, bool isLotControlled, DateTime modifiedAtUtc)
    {
        Description = Guard.NotBlank(description, nameof(description));
        Uom = Guard.NotBlank(uom, nameof(uom)).ToUpperInvariant();
        UnitWeightKg = unitWeightKg is > 0 ? unitWeightKg : null;
        IsLotControlled = isLotControlled;
        ModifiedAtUtc = Guard.Utc(modifiedAtUtc);
    }
}
