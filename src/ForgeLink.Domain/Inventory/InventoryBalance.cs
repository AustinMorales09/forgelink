namespace ForgeLink.Domain.Inventory;

/// <summary>On-hand quantity of one item/lot at one location. Never allowed to go negative.</summary>
public class InventoryBalance : IVersioned, IAudited
{
    private InventoryBalance() { }

    public InventoryBalance(string sku, string location, string lotNumber)
    {
        Sku = Guard.NotBlank(sku, nameof(sku)).ToUpperInvariant();
        Location = Guard.NotBlank(location, nameof(location)).ToUpperInvariant();
        LotNumber = (lotNumber ?? "").Trim().ToUpperInvariant();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Sku { get; private set; } = "";
    public string Location { get; private set; } = "";
    public string LotNumber { get; private set; } = "";
    public decimal Quantity { get; private set; }
    public int Version { get; set; }

    public void Add(decimal quantity) => Quantity += Guard.Positive(quantity, nameof(quantity));

    public void Remove(decimal quantity)
    {
        Guard.Positive(quantity, nameof(quantity));
        if (quantity > Quantity)
            throw new DomainException("inventory.insufficient",
                $"Only {Quantity} of {Sku} lot '{LotNumber}' is at {Location}; cannot remove {quantity}.");
        Quantity -= quantity;
    }

    /// <summary>Signed adjustment (cycle count, damage, found stock).</summary>
    public void Adjust(decimal delta)
    {
        if (delta > 0) Add(delta);
        else if (delta < 0) Remove(-delta);
    }
}

public enum InventoryTransactionType
{
    Receipt,
    Issue,
    Move,
    Adjustment,
    ProductionReceipt,
    Scrap,
}

/// <summary>Immutable inventory ledger entry. The ledger, not the balance table, is the audit source of truth.</summary>
public class InventoryTransaction
{
    private InventoryTransaction() { }

    public static InventoryTransaction Create(
        InventoryTransactionType type, string sku, string lotNumber, decimal quantity,
        string? fromLocation, string? toLocation, string? reference, string? reasonCode,
        string userName, DateTime occurredAtUtc) => new()
        {
            Type = type,
            Sku = Guard.NotBlank(sku, nameof(sku)).ToUpperInvariant(),
            LotNumber = (lotNumber ?? "").ToUpperInvariant(),
            Quantity = quantity,
            FromLocation = fromLocation?.ToUpperInvariant(),
            ToLocation = toLocation?.ToUpperInvariant(),
            Reference = reference,
            ReasonCode = reasonCode,
            UserName = Guard.NotBlank(userName, nameof(userName)),
            OccurredAtUtc = Guard.Utc(occurredAtUtc),
        };

    public Guid Id { get; private set; } = Guid.NewGuid();
    public InventoryTransactionType Type { get; private set; }
    public string Sku { get; private set; } = "";
    public string LotNumber { get; private set; } = "";
    public decimal Quantity { get; private set; }
    public string? FromLocation { get; private set; }
    public string? ToLocation { get; private set; }
    public string? Reference { get; private set; }
    public string? ReasonCode { get; private set; }
    public string UserName { get; private set; } = "";
    public DateTime OccurredAtUtc { get; private set; }
}
