namespace ForgeLink.Application.Abstractions;

/// <summary>Port to the ERP. The HTTP adapter lives in Infrastructure; tests use a fake.</summary>
public interface IErpClient
{
    /// <summary>Posts one transaction. Must never throw for remote failures; classify them instead.</summary>
    Task<ErpPostResult> PostAsync(string messageType, string payloadJson, string idempotencyKey, CancellationToken ct);

    Task<IReadOnlyList<ErpItem>> GetItemsChangedSinceAsync(DateTime sinceUtc, CancellationToken ct);
    Task<IReadOnlyList<ErpWorkOrder>> GetWorkOrdersChangedSinceAsync(DateTime sinceUtc, CancellationToken ct);
    Task<ErpWorkOrder?> GetWorkOrderAsync(string number, CancellationToken ct);
}

public enum ErpPostOutcome
{
    /// <summary>The ERP accepted and booked the transaction.</summary>
    Accepted,

    /// <summary>The ERP had already booked this idempotency key. Treated as success.</summary>
    Duplicate,

    /// <summary>Timeout, 5xx, 429, network error, open circuit. Retry later.</summary>
    TransientFailure,

    /// <summary>The ERP rejected the data (4xx). Retrying will not help; a person must intervene.</summary>
    PermanentFailure,
}

public sealed record ErpPostResult(ErpPostOutcome Outcome, string? ErpReference = null, string? Error = null)
{
    public bool IsSuccess => Outcome is ErpPostOutcome.Accepted or ErpPostOutcome.Duplicate;
}

public sealed record ErpItem(string Sku, string Description, string Uom, decimal? UnitWeightKg, bool LotControlled, DateTime ModifiedAtUtc);

public sealed record ErpWorkOrder(string Number, string Sku, decimal QuantityOrdered, string Line, DateTime DueDateUtc, string Status, DateTime ModifiedAtUtc);
