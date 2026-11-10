namespace ForgeLink.Domain.Integration;

/// <summary>Append-only audit trail of changes to <see cref="IAudited"/> entities.</summary>
public class AuditEntry
{
    private AuditEntry() { }

    public AuditEntry(string entityType, string entityKey, string action, string userName, DateTime timestampUtc, string changes)
    {
        EntityType = entityType;
        EntityKey = entityKey;
        Action = action;
        UserName = userName;
        TimestampUtc = timestampUtc;
        Changes = changes;
    }

    public long Id { get; private set; }
    public string EntityType { get; private set; } = "";
    public string EntityKey { get; private set; } = "";
    public string Action { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public DateTime TimestampUtc { get; private set; }
    public string Changes { get; private set; } = "";
}

/// <summary>Stored response for an API call made with an Idempotency-Key, so client retries replay instead of re-executing.</summary>
public class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public IdempotencyRecord(string key, string requestHash, int statusCode, string responseBody, DateTime createdAtUtc)
    {
        Key = key;
        RequestHash = requestHash;
        StatusCode = statusCode;
        ResponseBody = responseBody;
        CreatedAtUtc = createdAtUtc;
    }

    public string Key { get; private set; } = "";
    public string RequestHash { get; private set; } = "";
    public int StatusCode { get; private set; }
    public string ResponseBody { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
}

/// <summary>High-water mark and health for one incremental sync feed (e.g. ERP items).</summary>
public class SyncState
{
    private SyncState() { }

    public SyncState(string name) => Name = name;

    public string Name { get; private set; } = "";
    public DateTime WatermarkUtc { get; private set; } = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc);
    public DateTime? LastRunUtc { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }
    public int RecordsLastRun { get; private set; }
    public string? LastError { get; private set; }

    public void Succeeded(DateTime newWatermarkUtc, int records, DateTime nowUtc)
    {
        if (newWatermarkUtc > WatermarkUtc) WatermarkUtc = newWatermarkUtc;
        LastRunUtc = nowUtc;
        LastSuccessUtc = nowUtc;
        RecordsLastRun = records;
        LastError = null;
    }

    public void Failed(string error, DateTime nowUtc)
    {
        LastRunUtc = nowUtc;
        LastError = error.Length > 1000 ? error[..1000] : error;
    }
}
