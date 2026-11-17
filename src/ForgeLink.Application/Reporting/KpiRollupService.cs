using ForgeLink.Application.Abstractions;
using ForgeLink.Contracts;
using ForgeLink.Domain;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Application.Reporting;

public sealed class ReportingOptions
{
    public const string Section = "Reporting";

    /// <summary>IANA or Windows time zone id. Days are plant-local, so a night shift's midnight is the plant's midnight.</summary>
    public string PlantTimeZone { get; set; } = "UTC";

    /// <summary>Planned production hours in a full day (24 = continuous operation).</summary>
    public double PlannedHoursPerDay { get; set; } = 24;

    /// <summary>Closed days that are recomputed on every run, to absorb telemetry that arrives late (gateway backlog, offline replay).</summary>
    public int LateDataDays { get; set; } = 2;

    /// <summary>The furthest back a single run will go (first run, or after a long outage).</summary>
    public int MaxBackfillDays { get; set; } = 35;

    public double RollupIntervalMinutes { get; set; } = 10;
}

/// <summary>
/// Daily OEE pipeline: <b>extract</b> hourly production counters and downtime events for each plant-local day,
/// <b>transform</b> them into availability, performance, quality and the top downtime reason per machine,
/// and <b>load</b> them into <see cref="DailyMachineKpi"/> with an upsert keyed by (machine, day).
/// <para>
/// Idempotent and restartable: a watermark in <see cref="SyncState"/> records the last closed day; each run recomputes
/// from a few days before it (late data) through today (marked partial). Running it twice changes nothing.
/// </para>
/// </summary>
public sealed class KpiRollupService(IAppDbContext db, TimeProvider clock, IOptions<ReportingOptions> options, ILogger<KpiRollupService> logger)
{
    public const string WatermarkName = "kpi-rollup-daily";

    private readonly ReportingOptions _options = options.Value;
    private readonly TimeZoneInfo _zone = ResolveZone(options.Value.PlantTimeZone);

    public async Task<KpiRollupResultDto> RunAsync(CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var today = LocalDay(nowUtc);

        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Name == WatermarkName, ct);
        if (state is null)
        {
            state = new SyncState(WatermarkName);
            db.SyncStates.Add(state);
        }

        var first = await FirstDayToProcessAsync(state, today, nowUtc, ct);
        var machines = await db.Machines.AsNoTracking().ToListAsync(ct);
        var codes = machines.Select(m => m.Code).ToList();

        // Extract the whole range in two queries, then slice per day in memory.
        var (rangeStart, _) = Window(first);
        var counters = await db.ProductionCounters.AsNoTracking()
            .Where(c => codes.Contains(c.MachineCode) && c.BucketStartUtc >= ProductionCounter.BucketFor(rangeStart) && c.BucketStartUtc < nowUtc)
            .ToListAsync(ct);
        var downtime = await db.DowntimeEvents.AsNoTracking()
            .Where(d => codes.Contains(d.MachineCode) && d.StartUtc < nowUtc && (d.EndUtc == null || d.EndUtc > rangeStart))
            .ToListAsync(ct);
        var existing = (await db.DailyMachineKpis.Where(k => k.Day >= first && k.Day <= today).ToListAsync(ct))
            .ToDictionary(k => (k.MachineCode, k.Day));

        var rows = 0;
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var (startUtc, endUtc) = Window(day);
            var partial = day == today;
            if (partial) endUtc = nowUtc;
            if (endUtc <= startUtc) continue;

            // Planned time scales with the clock hours actually in the day (23 or 25 on DST changes) and, for today, the hours so far.
            var planned = TimeSpan.FromHours(_options.PlannedHoursPerDay / 24.0 * (endUtc - startUtc).TotalHours);

            foreach (var m in machines)
            {
                var mine = counters.Where(c => c.MachineCode == m.Code && c.BucketStartUtc >= startUtc && c.BucketStartUtc < endUtc).ToList();
                var stops = downtime.Where(d => d.MachineCode == m.Code)
                    .Select(d => (d.ReasonCode, Minutes: d.OverlapWith(startUtc, endUtc)))
                    .Where(x => x.Minutes > TimeSpan.Zero)
                    .ToList();
                var down = stops.Aggregate(TimeSpan.Zero, (sum, x) => sum + x.Minutes);
                if (down > planned) down = planned;
                var good = mine.Sum(c => c.GoodCount);
                var reject = mine.Sum(c => c.RejectCount);
                var top = stops.GroupBy(x => x.ReasonCode)
                    .Select(g => (Reason: g.Key, Total: g.Aggregate(TimeSpan.Zero, (s, x) => s + x.Minutes)))
                    .OrderByDescending(x => x.Total).ThenBy(x => x.Reason, StringComparer.Ordinal)
                    .Select(x => x.Reason).FirstOrDefault();

                var oee = OeeCalculator.Calculate(planned, down, good + reject, good, m.IdealCycleTimeSeconds);
                if (!existing.TryGetValue((m.Code, day), out var row))
                {
                    row = new DailyMachineKpi(m.Code, m.Line, day);
                    db.DailyMachineKpis.Add(row);
                    existing[(m.Code, day)] = row;
                }
                row.Update(m.Line, planned, down, good, reject, oee, top, partial, nowUtc);
                rows++;
            }
        }

        // Every day before today is now closed; the next run starts LateDataDays before this.
        state.Succeeded(Window(today).StartUtc, rows, nowUtc);
        await db.SaveChangesAsync(ct);
        var days = today.DayNumber - first.DayNumber + 1;
        logger.LogInformation("KPI rollup wrote {Rows} machine-days for {First}..{Last}", rows, first, today);
        return new KpiRollupResultDto(first, today, days, rows, nowUtc);
    }

    /// <summary>Reads the loaded table (the "serving" side of the pipeline).</summary>
    public async Task<IReadOnlyList<DailyOeeDto>> GetDailyAsync(string? line, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 366) throw new DomainException("validation", "Ask for at most 366 days at a time.");

        var query = db.DailyMachineKpis.AsNoTracking().Where(k => k.Day >= from && k.Day <= to);
        if (!string.IsNullOrWhiteSpace(line)) query = query.Where(k => k.Line == line.Trim().ToUpperInvariant());
        var rows = await query.OrderBy(k => k.Day).ThenBy(k => k.MachineCode).ToListAsync(ct);
        return rows.Select(k => new DailyOeeDto(k.Day, k.MachineCode, k.Line, k.PlannedMinutes, k.DowntimeMinutes, k.GoodCount, k.RejectCount,
            k.Availability, k.Performance, k.Quality, k.Oee, k.TopDowntimeReason, k.IsPartial, k.ComputedAtUtc)).ToList();
    }

    public DateOnly LocalDay(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _zone));

    /// <summary>[local midnight, next local midnight) expressed in UTC.</summary>
    public (DateTime StartUtc, DateTime EndUtc) Window(DateOnly day) =>
        (ToUtc(day.ToDateTime(TimeOnly.MinValue)), ToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private DateTime ToUtc(DateTime local)
    {
        // Midnight can fall in a DST gap in a few zones; move forward to the first valid instant.
        while (_zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
    }

    private async Task<DateOnly> FirstDayToProcessAsync(SyncState state, DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var floor = today.AddDays(-Math.Max(0, _options.MaxBackfillDays));
        // Never report days before the first telemetry we hold: they would be empty rows, and a re-run would keep adding them.
        var earliestCounter = await db.ProductionCounters.MinAsync(c => (DateTime?)c.BucketStartUtc, ct);
        var earliestStop = await db.DowntimeEvents.MinAsync(d => (DateTime?)d.StartUtc, ct);
        var earliest = new[] { earliestCounter, earliestStop }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(nowUtc).Min();
        var dataStart = LocalDay(DateTime.SpecifyKind(earliest, DateTimeKind.Utc));

        // First run: everything since the data starts. Later runs: the late-data window before the last closed day.
        var first = state.LastSuccessUtc is null
            ? dataStart
            : LocalDay(state.WatermarkUtc).AddDays(-Math.Max(0, _options.LateDataDays));
        if (first < dataStart) first = dataStart;
        if (first < floor) first = floor;
        return first > today ? today : first;
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try
        {
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InvalidOperationException($"Reporting:PlantTimeZone '{id}' is not a known time zone on this host.");
        }
    }
}
