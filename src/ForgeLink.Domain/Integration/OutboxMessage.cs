namespace ForgeLink.Domain.Integration;

public enum OutboxStatus
{
    Pending,
    Sent,
    DeadLettered,
    Cancelled,
}

/// <summary>
/// Transactional outbox entry: written in the same database transaction as the shop-floor change it
/// describes, then delivered to the ERP by a background dispatcher. Guarantees the ERP eventually sees
/// every transaction exactly once (with the ERP honouring <see cref="IdempotencyKey"/>).
/// </summary>
public class OutboxMessage
{
    public const int MaxErrorLength = 2000;

    private OutboxMessage() { }

    public OutboxMessage(string messageType, string partitionKey, string payload, string idempotencyKey, DateTime createdAtUtc, string? createdBy = null)
    {
        MessageType = Guard.NotBlank(messageType, nameof(messageType));
        PartitionKey = Guard.NotBlank(partitionKey, nameof(partitionKey));
        Payload = Guard.NotBlank(payload, nameof(payload));
        IdempotencyKey = Guard.NotBlank(idempotencyKey, nameof(idempotencyKey));
        CreatedAtUtc = Guard.Utc(createdAtUtc);
        NextAttemptAtUtc = CreatedAtUtc;
        CreatedBy = createdBy;
        Status = OutboxStatus.Pending;
    }

    /// <summary>Monotonic identity: also defines delivery order within a partition.</summary>
    public long Id { get; private set; }

    public string MessageType { get; private set; } = "";

    /// <summary>Messages sharing a partition (e.g. one SKU or one work order) are delivered strictly in order.</summary>
    public string PartitionKey { get; private set; } = "";

    public string Payload { get; private set; } = "";
    public string IdempotencyKey { get; private set; } = "";
    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public string? ErpReference { get; private set; }
    public string? CreatedBy { get; private set; }

    public bool IsDue(DateTime nowUtc) => Status == OutboxStatus.Pending && NextAttemptAtUtc <= nowUtc;

    public void MarkSent(string? erpReference, DateTime nowUtc)
    {
        Attempts++;
        Status = OutboxStatus.Sent;
        SentAtUtc = Guard.Utc(nowUtc);
        ErpReference = erpReference;
        LastError = null;
    }

    public void ScheduleRetry(string error, DateTime nextAttemptAtUtc)
    {
        Attempts++;
        LastError = Truncate(error);
        NextAttemptAtUtc = Guard.Utc(nextAttemptAtUtc);
    }

    public void DeadLetter(string error)
    {
        Attempts++;
        Status = OutboxStatus.DeadLettered;
        LastError = Truncate(error);
    }

    /// <summary>Operator action after fixing the root cause (e.g. missing item in ERP).</summary>
    public void Requeue(DateTime nowUtc)
    {
        if (Status is OutboxStatus.Sent or OutboxStatus.Cancelled)
            throw new DomainException("outbox.not-retryable", $"Message {Id} is {Status} and cannot be retried.");
        Status = OutboxStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = Guard.Utc(nowUtc);
    }

    public void Cancel(string reason, string cancelledBy)
    {
        if (Status == OutboxStatus.Sent)
            throw new DomainException("outbox.already-sent", $"Message {Id} was already accepted by the ERP.");
        Status = OutboxStatus.Cancelled;
        LastError = Truncate($"Cancelled by {cancelledBy}: {Guard.NotBlank(reason, nameof(reason))}");
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}

/// <summary>Exponential backoff with jitter for outbox redelivery.</summary>
public static class RetryBackoff
{
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitterFraction = 0.2, Random? random = null)
    {
        if (attempt < 1) attempt = 1;
        var exp = Math.Min(baseDelay.TotalSeconds * Math.Pow(2, attempt - 1), maxDelay.TotalSeconds);
        var jitter = jitterFraction <= 0 ? 0 : exp * jitterFraction * (((random ?? Random.Shared).NextDouble() * 2) - 1);
        return TimeSpan.FromSeconds(Math.Clamp(exp + jitter, 0, maxDelay.TotalSeconds));
    }
}
