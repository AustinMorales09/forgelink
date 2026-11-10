namespace ForgeLink.Domain.Inventory;

public enum CycleCountStatus
{
    PendingApproval,
    Posted,
    Rejected,
}

/// <summary>
/// A physical count compared against the system quantity. Variances inside tolerance post automatically;
/// larger variances need a supervisor who is not the counter (segregation of duties).
/// </summary>
public class CycleCount : IAudited
{
    private CycleCount() { }

    public static CycleCount Record(
        string sku, string location, string lotNumber, decimal systemQuantity, decimal countedQuantity,
        string countedBy, decimal tolerancePercent, DateTime nowUtc)
    {
        Guard.NotNegative(countedQuantity, nameof(countedQuantity));
        var within = IsWithinTolerance(systemQuantity, countedQuantity, tolerancePercent);
        var now = Guard.Utc(nowUtc);
        return new CycleCount
        {
            Sku = Guard.NotBlank(sku, nameof(sku)).ToUpperInvariant(),
            Location = Guard.NotBlank(location, nameof(location)).ToUpperInvariant(),
            LotNumber = (lotNumber ?? "").Trim().ToUpperInvariant(),
            SystemQuantity = systemQuantity,
            CountedQuantity = countedQuantity,
            CountedBy = Guard.NotBlank(countedBy, nameof(countedBy)),
            CountedAtUtc = now,
            WithinTolerance = within,
            Status = within ? CycleCountStatus.Posted : CycleCountStatus.PendingApproval,
            PostedAtUtc = within ? now : null,
        };
    }

    public static bool IsWithinTolerance(decimal systemQuantity, decimal countedQuantity, decimal tolerancePercent)
    {
        if (systemQuantity == 0) return countedQuantity == 0;
        var variancePct = Math.Abs(countedQuantity - systemQuantity) / Math.Abs(systemQuantity) * 100m;
        return variancePct <= tolerancePercent;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Sku { get; private set; } = "";
    public string Location { get; private set; } = "";
    public string LotNumber { get; private set; } = "";
    public decimal SystemQuantity { get; private set; }
    public decimal CountedQuantity { get; private set; }
    public bool WithinTolerance { get; private set; }
    public CycleCountStatus Status { get; private set; }
    public string CountedBy { get; private set; } = "";
    public DateTime CountedAtUtc { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTime? PostedAtUtc { get; private set; }

    public decimal Variance => CountedQuantity - SystemQuantity;

    public void Approve(string approver, DateTime nowUtc)
    {
        EnsurePending();
        approver = Guard.NotBlank(approver, nameof(approver));
        if (string.Equals(approver, CountedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("cyclecount.self-approval", "The person who counted cannot approve their own variance.");
        Status = CycleCountStatus.Posted;
        ApprovedBy = approver;
        PostedAtUtc = Guard.Utc(nowUtc);
    }

    public void Reject(string approver)
    {
        EnsurePending();
        Status = CycleCountStatus.Rejected;
        ApprovedBy = Guard.NotBlank(approver, nameof(approver));
    }

    private void EnsurePending()
    {
        if (Status != CycleCountStatus.PendingApproval)
            throw new DomainException("cyclecount.not-pending", $"Cycle count is {Status}.");
    }
}
