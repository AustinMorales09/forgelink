using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Integration;
using ForgeLink.Contracts;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Application.Reporting;

/// <summary>Operational KPIs: OEE, downtime Pareto, hourly throughput, inventory accuracy and integration health.</summary>
public sealed class KpiService(IAppDbContext db, IDowntimeParetoQuery pareto, IntegrationAdminService integration)
{
    public async Task<LineKpiDto> GetLineKpisAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        line = Text.Upper(line);
        fromUtc = DateTime.SpecifyKind(fromUtc.ToUniversalTime(), DateTimeKind.Utc);
        toUtc = DateTime.SpecifyKind(toUtc.ToUniversalTime(), DateTimeKind.Utc);
        if (toUtc <= fromUtc) toUtc = fromUtc.AddHours(1);

        var machines = await db.Machines.AsNoTracking().Where(m => m.Line == line).OrderBy(m => m.Code).ToListAsync(ct);
        var codes = machines.Select(m => m.Code).ToList();

        var downtime = await db.DowntimeEvents.AsNoTracking()
            .Where(d => codes.Contains(d.MachineCode) && d.StartUtc < toUtc && (d.EndUtc == null || d.EndUtc > fromUtc))
            .ToListAsync(ct);

        var firstBucket = ProductionCounter.BucketFor(fromUtc);
        var counters = await db.ProductionCounters.AsNoTracking()
            .Where(c => codes.Contains(c.MachineCode) && c.BucketStartUtc >= firstBucket && c.BucketStartUtc < toUtc)
            .ToListAsync(ct);

        var planned = toUtc - fromUtc;
        var machineKpis = new List<MachineKpiDto>();
        var lineResults = new List<OeeResult>();

        foreach (var m in machines)
        {
            var down = downtime.Where(d => d.MachineCode == m.Code).Aggregate(TimeSpan.Zero, (sum, d) => sum + d.OverlapWith(fromUtc, toUtc));
            var mine = counters.Where(c => c.MachineCode == m.Code).ToList();
            var good = mine.Sum(c => c.GoodCount);
            var reject = mine.Sum(c => c.RejectCount);
            var oee = OeeCalculator.Calculate(planned, down, good + reject, good, m.IdealCycleTimeSeconds);
            lineResults.Add(oee);
            machineKpis.Add(new MachineKpiDto(m.Code, m.State.ToString(), oee.ToDto(), Math.Round(down.TotalMinutes, 1), good, reject));
        }

        var lineOee = lineResults.Count == 0
            ? OeeResult.Zero
            : new OeeResult(lineResults.Average(r => r.Availability), lineResults.Average(r => r.Performance), lineResults.Average(r => r.Quality));

        var throughput = counters
            .GroupBy(c => c.BucketStartUtc)
            .OrderBy(g => g.Key)
            .Select(g => new ThroughputPointDto(g.Key, g.Sum(c => c.GoodCount), g.Sum(c => c.RejectCount)))
            .ToList();

        var counts = await db.CycleCounts.AsNoTracking()
            .Where(c => c.CountedAtUtc >= fromUtc && c.CountedAtUtc < toUtc && c.Status != CycleCountStatus.Rejected)
            .Select(c => c.WithinTolerance)
            .ToListAsync(ct);
        double? accuracy = counts.Count == 0 ? null : Math.Round(100.0 * counts.Count(w => w) / counts.Count, 1);

        return new LineKpiDto(line, fromUtc, toUtc, lineOee.ToDto(), machineKpis,
            await pareto.GetAsync(line, fromUtc, toUtc, ct), throughput, accuracy, counts.Count,
            await integration.GetHealthAsync(ct));
    }
}

/// <summary>Portable LINQ implementation (SQLite, SQL Server). SQL Server deployments swap in the stored procedure.</summary>
public sealed class EfDowntimeParetoQuery(IAppDbContext db) : IDowntimeParetoQuery
{
    public async Task<IReadOnlyList<DowntimeReasonDto>> GetAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var codes = await db.Machines.AsNoTracking().Where(m => m.Line == line).Select(m => m.Code).ToListAsync(ct);
        var events = await db.DowntimeEvents.AsNoTracking()
            .Where(d => codes.Contains(d.MachineCode) && d.StartUtc < toUtc && (d.EndUtc == null || d.EndUtc > fromUtc))
            .ToListAsync(ct);

        return events
            .GroupBy(e => e.ReasonCode)
            .Select(g => new DowntimeReasonDto(g.Key, Math.Round(g.Sum(e => e.OverlapWith(fromUtc, toUtc).TotalMinutes), 1), g.Count()))
            .OrderByDescending(r => r.Minutes)
            .ToList();
    }
}
