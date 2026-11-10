using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Contracts;
using ForgeLink.Domain;
using ForgeLink.Domain.Equipment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Application.Equipment;

/// <summary>
/// Turns raw OT signals into machine state, downtime events and production counters.
/// Signals may arrive late, duplicated or out of order (store-and-forward gateways), so state changes are
/// timestamp-ordered and unknown machines are logged, not fatal.
/// </summary>
public sealed class TelemetryService(
    IAppDbContext db, TimeProvider clock, IShopFloorNotifier notifier, IOptions<ForgeLinkOptions> options, ILogger<TelemetryService> logger)
{
    public async Task<IReadOnlyList<MachineStatusDto>> GetMachinesAsync(string? line, CancellationToken ct)
    {
        var query = db.Machines.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(line)) query = query.Where(m => m.Line == Text.Upper(line));
        return (await query.OrderBy(m => m.Line).ThenBy(m => m.Code).ToListAsync(ct)).Select(m => m.ToDto()).ToList();
    }

    /// <returns>False if the signal was ignored (unknown machine or malformed).</returns>
    public async Task<bool> HandleAsync(MachineSignalDto signal, CancellationToken ct)
    {
        var code = Text.Upper(signal.MachineCode);
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Code == code, ct);
        if (machine is null)
        {
            logger.LogWarning("Ignoring {SignalType} signal for unknown machine {MachineCode}", signal.SignalType, code);
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var at = signal.TimestampUtc is { } ts ? DateTime.SpecifyKind(ts.ToUniversalTime(), DateTimeKind.Utc) : now;
        if (at > now.AddMinutes(5)) at = now; // guard against device clock drift into the future

        var stateChanged = false;
        switch (signal.SignalType?.ToLowerInvariant())
        {
            case MachineSignalTypes.Heartbeat:
                machine.Heartbeat(at);
                break;

            case MachineSignalTypes.State:
                if (!Enum.TryParse<MachineState>(signal.State, ignoreCase: true, out var state))
                    throw new DomainException("validation", $"Unknown machine state '{signal.State}'.");
                stateChanged = await ApplyStateAsync(machine, state, signal.Reason, at, ct);
                break;

            case MachineSignalTypes.Count:
                var good = Math.Max(0, signal.GoodDelta ?? 0);
                var reject = Math.Max(0, signal.RejectDelta ?? 0);
                if (good + reject == 0) return false;
                var bucket = ProductionCounter.BucketFor(at);
                var counter = db.ProductionCounters.Local.FirstOrDefault(c => c.MachineCode == code && c.BucketStartUtc == bucket)
                    ?? await db.ProductionCounters.FirstOrDefaultAsync(c => c.MachineCode == code && c.BucketStartUtc == bucket, ct);
                if (counter is null)
                {
                    counter = new ProductionCounter(code, bucket);
                    db.ProductionCounters.Add(counter);
                }
                counter.Add(good, reject);
                machine.Heartbeat(at);
                break;

            default:
                logger.LogWarning("Ignoring unknown signal type {SignalType} from {MachineCode}", signal.SignalType, code);
                return false;
        }

        await db.SaveChangesAsync(ct);
        if (stateChanged) await notifier.MachineChangedAsync(code, ct);
        return true;
    }

    /// <summary>Marks machines Offline when their gateway stops sending heartbeats (cable pulled, PLC down).</summary>
    public async Task<int> MarkStaleMachinesOfflineAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var timeout = TimeSpan.FromSeconds(options.Value.MachineHeartbeatTimeoutSeconds);
        var machines = await db.Machines.Where(m => m.State != MachineState.Offline).ToListAsync(ct);
        var stale = machines.Where(m => m.IsStale(now, timeout)).ToList();

        foreach (var machine in stale)
            await ApplyStateAsync(machine, MachineState.Offline, "NO_HEARTBEAT", now, ct);

        if (stale.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        foreach (var m in stale) await notifier.MachineChangedAsync(m.Code, ct);
        return stale.Count;
    }

    private async Task<bool> ApplyStateAsync(Machine machine, MachineState state, string? reason, DateTime at, CancellationToken ct)
    {
        var wasDown = Machine.CountsAsDowntime(machine.State);
        var previousReason = machine.StateReason;
        if (!machine.ApplyState(state, reason, at)) return false;

        var nowDown = Machine.CountsAsDowntime(state);
        if (wasDown && (!nowDown || previousReason != machine.StateReason))
        {
            var open = await db.DowntimeEvents.Where(d => d.MachineCode == machine.Code && d.EndUtc == null).ToListAsync(ct);
            foreach (var d in open) d.Close(at);
        }

        if (nowDown && (!wasDown || previousReason != machine.StateReason))
            db.DowntimeEvents.Add(new DowntimeEvent(machine.Code, at, reason ?? state.ToString()));

        return true;
    }
}
