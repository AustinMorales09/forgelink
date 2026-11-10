using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Domain.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Application.Integration;

/// <summary>
/// Delivers staged outbox messages to the ERP.
/// <list type="bullet">
/// <item>At-least-once delivery + ERP idempotency keys = effectively exactly-once booking.</item>
/// <item>Strict FIFO per partition: if an earlier inventory message for a SKU is failing, later ones wait,
/// so the ERP never sees a transfer before the receipt it depends on.</item>
/// <item>Transient failures back off exponentially; permanent (4xx) failures dead-letter for a person to resolve.</item>
/// </list>
/// </summary>
public sealed class OutboxProcessor(
    IAppDbContext db, IErpClient erp, TimeProvider clock, IOptions<OutboxOptions> options, ILogger<OutboxProcessor> logger)
{
    public sealed record BatchResult(int Sent, int Retrying, int DeadLettered, int Blocked);

    public async Task<BatchResult> ProcessBatchAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        // Oldest-first window of undelivered work. Dead-lettered messages are included only so they can block their partition.
        var window = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.DeadLettered)
            .OrderBy(m => m.Id)
            .Take(opts.BatchSize * 4)
            .ToListAsync(ct);

        var blocked = new HashSet<string>();
        int sent = 0, retrying = 0, dead = 0, skipped = 0, attempted = 0;

        foreach (var message in window)
        {
            if (attempted >= opts.BatchSize) break;

            if (message.Status == OutboxStatus.DeadLettered || !message.IsDue(now) || blocked.Contains(message.PartitionKey))
            {
                if (message.Status == OutboxStatus.Pending && blocked.Contains(message.PartitionKey)) skipped++;
                blocked.Add(message.PartitionKey);
                continue;
            }

            attempted++;
            ErpPostResult result;
            try
            {
                result = await erp.PostAsync(message.MessageType, message.Payload, message.IdempotencyKey, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ErpPostResult(ErpPostOutcome.TransientFailure, Error: ex.Message);
            }

            switch (result.Outcome)
            {
                case ErpPostOutcome.Accepted:
                case ErpPostOutcome.Duplicate:
                    message.MarkSent(result.ErpReference, now);
                    sent++;
                    break;

                case ErpPostOutcome.PermanentFailure:
                    message.DeadLetter(result.Error ?? "Rejected by ERP");
                    blocked.Add(message.PartitionKey);
                    dead++;
                    logger.LogError("Outbox {Id} ({Type}) dead-lettered: {Error}", message.Id, message.MessageType, result.Error);
                    break;

                default:
                    if (message.Attempts + 1 >= opts.MaxAttempts)
                    {
                        message.DeadLetter($"Gave up after {opts.MaxAttempts} attempts. Last error: {result.Error}");
                        dead++;
                        logger.LogError("Outbox {Id} ({Type}) exhausted retries: {Error}", message.Id, message.MessageType, result.Error);
                    }
                    else
                    {
                        var delay = RetryBackoff.Compute(message.Attempts + 1,
                            TimeSpan.FromSeconds(opts.BaseDelaySeconds), TimeSpan.FromSeconds(opts.MaxDelaySeconds));
                        message.ScheduleRetry(result.Error ?? "Transient failure", now + delay);
                        retrying++;
                        logger.LogWarning("Outbox {Id} ({Type}) attempt {Attempt} failed, retrying in {Delay}: {Error}",
                            message.Id, message.MessageType, message.Attempts, delay, result.Error);
                    }
                    blocked.Add(message.PartitionKey);
                    break;
            }

            // Persist per message so a crash mid-batch never re-sends messages the ERP already acknowledged.
            await db.SaveChangesAsync(ct);
        }

        if (sent + retrying + dead > 0)
            logger.LogInformation("Outbox batch: {Sent} sent, {Retrying} retrying, {Dead} dead-lettered, {Blocked} waiting on partition order",
                sent, retrying, dead, skipped);

        return new BatchResult(sent, retrying, dead, skipped);
    }
}
