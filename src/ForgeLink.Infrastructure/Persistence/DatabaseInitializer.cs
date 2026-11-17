using System.Reflection;
using System.Text.RegularExpressions;
using ForgeLink.Application.Abstractions;
using ForgeLink.Contracts;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Infrastructure.Persistence;

public static partial class DatabaseInitializer
{
    /// <summary>
    /// Creates the schema (demo: EnsureCreated; production: migration bundle applied by the release pipeline),
    /// deploys SQL Server programmability, and seeds demo data when enabled.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeLinkDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        await db.Database.EnsureCreatedAsync(ct);

        if (db.Database.IsSqlServer())
        {
            foreach (var batch in ReadSqlBatches())
                await db.Database.ExecuteSqlRawAsync(batch, ct);
            logger.LogInformation("SQL Server programmability deployed");
        }

        if (config.GetValue("Database:SeedDemoData", true) && !await db.Machines.AnyAsync(ct))
        {
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            await DemoDataSeeder.SeedAsync(db, clock.GetUtcNow().UtcDateTime, ct, includeShiftHistory: true);
            logger.LogInformation("Seeded demo plant data");
        }
    }

    private static IEnumerable<string> ReadSqlBatches()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            foreach (var batch in GoSeparator().Split(reader.ReadToEnd()))
                if (!string.IsNullOrWhiteSpace(batch) && batch.Split('\n').Any(l => !l.TrimStart().StartsWith("--") && l.Trim().Length > 0))
                    yield return batch;
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

/// <summary>A small machining plant: two lines, raw bar stock staged at WIP, released work orders.</summary>
public static class DemoDataSeeder
{
    private static readonly (string Reason, int Weight)[] DowntimeReasons =
        [("TOOL_CHANGE", 30), ("MATERIAL_SHORTAGE", 20), ("CHIP_JAM", 20), ("QUALITY_ADJUST", 12), ("PLANNED_PM", 8), ("OPERATOR_BREAK", 10)];

    public static async Task SeedAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct, bool includeShiftHistory = false)
    {
        var modified = nowUtc.AddDays(-30);
        db.Items.AddRange(
            new Item("AL6061-BAR-50", "Aluminium 6061 bar, 50 mm", "KG", null, isLotControlled: true, modified),
            new Item("BRK-1001", "Brake caliper housing, machined", "EA", 1.240m, isLotControlled: true, modified),
            new Item("BRK-1002", "Brake caliper bracket", "EA", 0.410m, isLotControlled: true, modified),
            new Item("PKG-TRAY-24", "Returnable tray, 24 pocket", "EA", null, isLotControlled: false, modified));

        db.Machines.AddRange(
            new Machine("L1-CNC-01", "L1", "Okuma 5-axis cell #1", idealCycleTimeSeconds: 42),
            new Machine("L1-CNC-02", "L1", "Okuma 5-axis cell #2", idealCycleTimeSeconds: 42),
            new Machine("L1-WASH-01", "L1", "Parts washer", idealCycleTimeSeconds: 20),
            new Machine("L2-CNC-01", "L2", "Haas VF-4 bracket cell", idealCycleTimeSeconds: 28),
            new Machine("L2-CMM-01", "L2", "Zeiss CMM inspection", idealCycleTimeSeconds: 60));

        db.WorkOrders.AddRange(
            new WorkOrder("WO-24001", "BRK-1001", 400, "L1", nowUtc.Date.AddDays(1)),
            new WorkOrder("WO-24002", "BRK-1001", 250, "L1", nowUtc.Date.AddDays(3)),
            new WorkOrder("WO-24003", "BRK-1002", 600, "L2", nowUtc.Date.AddDays(2)));

        foreach (var (lot, location, qty) in new[]
                 {
                     ("HEAT-88231", Locations.LineWip("L1"), 800m),
                     ("HEAT-88232", Locations.LineWip("L2"), 450m),
                     ("HEAT-88233", "RM-01", 1200m),
                 })
        {
            db.Lots.Add(new Lot(lot, "AL6061-BAR-50", null, modified));
            var balance = new InventoryBalance("AL6061-BAR-50", location, lot);
            balance.Add(qty);
            db.InventoryBalances.Add(balance);
        }

        var trays = new InventoryBalance("PKG-TRAY-24", "FG-01", "");
        trays.Add(120);
        db.InventoryBalances.Add(trays);

        if (includeShiftHistory) AddShiftHistory(db, nowUtc);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Days of telemetry generated for a fresh demo database (feeds the daily OEE report).</summary>
    public const int HistoryDays = 14;

    /// <summary>
    /// Two weeks of plausible telemetry (run segments, stops with reason codes, hourly counts) so the dashboard has a
    /// realistic shift and the daily OEE report a trend on first launch. Days differ: speed, scrap and the odd long
    /// breakdown vary per machine per day. Deterministic seed: the same demo every time.
    /// </summary>
    private static void AddShiftHistory(IAppDbContext db, DateTime nowUtc)
    {
        var rng = new Random(20261109);
        var stop = nowUtc.AddMinutes(-5);
        var counters = new Dictionary<(string, DateTime), ProductionCounter>();

        foreach (var machine in db.Machines.Local.ToList())
        {
            var cursor = nowUtc.AddDays(-HistoryDays);
            var baseReject = 0.015 + rng.NextDouble() * 0.03;
            var baseSpeedLoss = 1.08 + rng.NextDouble() * 0.17;   // real cycles run 8-25 % slower than ideal
            var rejectRate = baseReject;
            var speedLoss = baseSpeedLoss;
            var day = DateTime.MinValue;
            var breakdownDue = DateTime.MaxValue;
            double carry = 0;

            while (cursor < stop)
            {
                if (cursor.Date != day)
                {
                    // New day: drift speed and scrap a little, and roughly one day in five has a long breakdown.
                    day = cursor.Date;
                    speedLoss = baseSpeedLoss * (0.95 + rng.NextDouble() * 0.15);
                    rejectRate = baseReject * (0.6 + rng.NextDouble() * 1.2);
                    breakdownDue = rng.Next(5) == 0 ? day.AddHours(rng.Next(2, 20)) : DateTime.MaxValue;
                }

                if (cursor >= breakdownDue)
                {
                    breakdownDue = DateTime.MaxValue;
                    var breakdown = new DowntimeEvent(machine.Code, cursor, "SPINDLE_FAULT");
                    cursor = Min(cursor.AddMinutes(rng.Next(60, 200)), stop);
                    breakdown.Close(cursor);
                    db.DowntimeEvents.Add(breakdown);
                    if (cursor >= stop) break;
                }

                var runEnd = Min(cursor.AddMinutes(rng.Next(25, 80)), stop);
                while (cursor < runEnd)
                {
                    var bucket = ProductionCounter.BucketFor(cursor);
                    var sliceEnd = Min(bucket.AddHours(1), runEnd);
                    carry += (sliceEnd - cursor).TotalSeconds / (machine.IdealCycleTimeSeconds * speedLoss);
                    var parts = (int)carry;
                    carry -= parts;
                    var rejects = (int)Math.Round(parts * rejectRate);

                    if (!counters.TryGetValue((machine.Code, bucket), out var counter))
                    {
                        counter = new ProductionCounter(machine.Code, bucket);
                        counters[(machine.Code, bucket)] = counter;
                        db.ProductionCounters.Add(counter);
                    }
                    counter.Add(parts - rejects, rejects);
                    cursor = sliceEnd;
                }

                if (cursor >= stop) break;
                var downtime = new DowntimeEvent(machine.Code, cursor, PickReason(rng));
                cursor = Min(cursor.AddMinutes(rng.Next(3, 22)), stop);
                downtime.Close(cursor);
                db.DowntimeEvents.Add(downtime);
            }
        }
    }

    private static string PickReason(Random rng)
    {
        var roll = rng.Next(DowntimeReasons.Sum(r => r.Weight));
        foreach (var (reason, weight) in DowntimeReasons)
        {
            if (roll < weight) return reason;
            roll -= weight;
        }
        return DowntimeReasons[0].Reason;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
