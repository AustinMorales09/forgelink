using System.Text.Json;
using ForgeLink.Application.Abstractions;
using ForgeLink.Domain.Integration;

namespace ForgeLink.Application.Common;

public sealed class NotFoundException(string entity, string key) : Exception($"{entity} '{key}' was not found.")
{
    public string Entity { get; } = entity;
    public string Key { get; } = key;
}

public sealed class ForgeLinkOptions
{
    public const string Section = "ForgeLink";

    /// <summary>Cycle-count variance (percent of system quantity) that posts without supervisor approval.</summary>
    public decimal CycleCountTolerancePercent { get; set; } = 2m;

    /// <summary>A machine with no heartbeat for this long is marked Offline.</summary>
    public int MachineHeartbeatTimeoutSeconds { get; set; } = 30;
}

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
    public double BaseDelaySeconds { get; set; } = 2;
    public double MaxDelaySeconds { get; set; } = 300;
    public double PollIntervalSeconds { get; set; } = 2;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal static class OutboxExtensions
{
    /// <summary>Stages an ERP message in the current unit of work; it commits atomically with the business change.</summary>
    public static OutboxMessage EnqueueErp<T>(this IAppDbContext db, string messageType, string partitionKey, T payload, string idempotencyKey, DateTime nowUtc, string? user)
    {
        var message = new OutboxMessage(messageType, partitionKey, Json.Serialize(payload), idempotencyKey, nowUtc, user);
        db.OutboxMessages.Add(message);
        return message;
    }
}

internal static class Text
{
    public static string Upper(string? value) => (value ?? "").Trim().ToUpperInvariant();
}
