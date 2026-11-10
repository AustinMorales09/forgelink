using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Contracts;
using ForgeLink.Domain.Integration;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Integration;

/// <summary>Operations console: inspect, retry and cancel outbox traffic; read the audit trail.</summary>
public sealed class IntegrationAdminService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public async Task<IntegrationHealthDto> GetHealthAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hourAgo = now.AddHours(-1);

        var pending = await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Pending, ct);
        var dead = await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.DeadLettered, ct);
        var sentLastHour = await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Sent && m.SentAtUtc >= hourAgo, ct);
        var oldest = await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.Id).Select(m => (DateTime?)m.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var syncs = await db.SyncStates.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

        return new IntegrationHealthDto(pending, dead,
            oldest is null ? null : Math.Round((now - oldest.Value).TotalMinutes, 1),
            sentLastHour, syncs.Select(s => s.ToDto()).ToList());
    }

    public async Task<IReadOnlyList<OutboxMessageDto>> ListAsync(OutboxStatus? status, int take, CancellationToken ct)
    {
        var query = db.OutboxMessages.AsNoTracking();
        if (status is not null) query = query.Where(m => m.Status == status);
        return (await query.OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(m => m.ToDto()).ToList();
    }

    public async Task<OutboxMessageDto?> GetAsync(long id, CancellationToken ct) =>
        (await db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct))?.ToDto();

    public async Task RetryAsync(long id, CancellationToken ct)
    {
        var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Outbox message", id.ToString());
        message.Requeue(clock.GetUtcNow().UtcDateTime);
        db.AuditEntries.Add(new AuditEntry(nameof(OutboxMessage), id.ToString(), "Requeued", user.Name, clock.GetUtcNow().UtcDateTime, "{}"));
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(long id, string reason, CancellationToken ct)
    {
        var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Outbox message", id.ToString());
        message.Cancel(reason, user.Name);
        db.AuditEntries.Add(new AuditEntry(nameof(OutboxMessage), id.ToString(), "Cancelled", user.Name, clock.GetUtcNow().UtcDateTime,
            Json.Serialize(new { reason })));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntryDto>> AuditTrailAsync(string? entityType, int take, CancellationToken ct)
    {
        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        return (await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(a => a.ToDto()).ToList();
    }
}
