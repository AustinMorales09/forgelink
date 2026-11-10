namespace ForgeLink.Domain.Production;

public enum WorkOrderStatus
{
    Released,
    InProgress,
    Paused,
    Completed,
    Cancelled,
}

/// <summary>
/// A production order released by the ERP. The ERP owns the header (item, quantity, due date);
/// the shop floor owns execution state (status, quantities reported, current machine).
/// </summary>
public class WorkOrder : IVersioned, IAudited
{
    /// <summary>Allowed over-production before the operator must get the order increased in the ERP.</summary>
    public const decimal OverrunTolerance = 0.10m;

    private WorkOrder() { }

    public WorkOrder(string number, string sku, decimal quantityOrdered, string line, DateTime dueDateUtc)
    {
        Number = Guard.NotBlank(number, nameof(number)).ToUpperInvariant();
        Sku = Guard.NotBlank(sku, nameof(sku));
        QuantityOrdered = Guard.Positive(quantityOrdered, nameof(quantityOrdered));
        Line = Guard.NotBlank(line, nameof(line));
        DueDateUtc = Guard.Utc(dueDateUtc);
        Status = WorkOrderStatus.Released;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Number { get; private set; } = "";
    public string Sku { get; private set; } = "";
    public string Line { get; private set; } = "";
    public decimal QuantityOrdered { get; private set; }
    public decimal QuantityCompleted { get; private set; }
    public decimal QuantityScrapped { get; private set; }
    public DateTime DueDateUtc { get; private set; }
    public WorkOrderStatus Status { get; private set; }
    public string? CurrentMachine { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int Version { get; set; }

    public decimal QuantityRemaining => Math.Max(0, QuantityOrdered - QuantityCompleted);
    public bool IsClosed => Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled;

    public void Start(string machineCode, DateTime nowUtc)
    {
        machineCode = Guard.NotBlank(machineCode, nameof(machineCode));
        if (IsClosed)
            throw new DomainException("workorder.closed", $"Work order {Number} is {Status} and cannot be started.");

        // Re-sending "start" from the same machine (e.g. a retried request) is a no-op, not an error.
        if (Status == WorkOrderStatus.InProgress && CurrentMachine == machineCode)
            return;

        if (Status == WorkOrderStatus.InProgress)
            throw new DomainException("workorder.running-elsewhere",
                $"Work order {Number} is already running on {CurrentMachine}.");

        Status = WorkOrderStatus.InProgress;
        CurrentMachine = machineCode;
        StartedAtUtc ??= Guard.Utc(nowUtc);
    }

    public void Pause()
    {
        if (Status == WorkOrderStatus.Paused) return;
        if (Status != WorkOrderStatus.InProgress)
            throw new DomainException("workorder.not-running", $"Work order {Number} is {Status}; only running orders can be paused.");
        Status = WorkOrderStatus.Paused;
        CurrentMachine = null;
    }

    public void ReportProduction(decimal goodQuantity, decimal scrapQuantity)
    {
        Guard.NotNegative(goodQuantity, nameof(goodQuantity));
        Guard.NotNegative(scrapQuantity, nameof(scrapQuantity));
        if (goodQuantity + scrapQuantity == 0)
            throw new DomainException("validation", "Report a good or scrap quantity greater than zero.");
        if (Status != WorkOrderStatus.InProgress)
            throw new DomainException("workorder.not-running", $"Work order {Number} is {Status}; start it before reporting production.");

        var maxAllowed = QuantityOrdered * (1 + OverrunTolerance);
        if (QuantityCompleted + goodQuantity > maxAllowed)
            throw new DomainException("workorder.overrun",
                $"Reporting {goodQuantity} would exceed the ordered quantity {QuantityOrdered} by more than {OverrunTolerance:P0}.");

        QuantityCompleted += goodQuantity;
        QuantityScrapped += scrapQuantity;
    }

    public void Complete(DateTime nowUtc)
    {
        if (Status == WorkOrderStatus.Completed) return;
        if (Status is not (WorkOrderStatus.InProgress or WorkOrderStatus.Paused))
            throw new DomainException("workorder.not-started", $"Work order {Number} is {Status} and cannot be completed.");
        Status = WorkOrderStatus.Completed;
        CompletedAtUtc = Guard.Utc(nowUtc);
        CurrentMachine = null;
    }

    /// <summary>
    /// Applies a header change pushed from the ERP. Returns false when the change conflicts with
    /// shop-floor execution (e.g. cancelling an order that is running) and needs a human decision.
    /// </summary>
    public bool ApplyErpUpdate(decimal quantityOrdered, DateTime dueDateUtc, bool cancelled)
    {
        DueDateUtc = Guard.Utc(dueDateUtc);
        if (quantityOrdered > 0) QuantityOrdered = quantityOrdered;

        if (!cancelled || Status == WorkOrderStatus.Cancelled) return true;
        if (Status is WorkOrderStatus.InProgress or WorkOrderStatus.Completed) return false;

        Status = WorkOrderStatus.Cancelled;
        CurrentMachine = null;
        return true;
    }
}
