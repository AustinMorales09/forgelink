using ForgeLink.Application.Reporting;
using ForgeLink.Domain.Equipment;
using ForgeLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeLink.UnitTests.Application;

/// <summary>The daily OEE pipeline against a real SQLite database with hand-built telemetry.</summary>
public sealed class KpiRollupTests : IDisposable
{
    // Wednesday 2026-11-11, mid-afternoon UTC.
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 11);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private readonly FakeClock _clock = new(Now);
    private readonly TestDb _db;

    public KpiRollupTests()
    {
        _db = new TestDb(_clock, seed: false);
        using var db = _db.CreateContext();
        db.Machines.Add(new Machine("M1", "L1", "Mill", idealCycleTimeSeconds: 60));
        db.Machines.Add(new Machine("M2", "L2", "Lathe", idealCycleTimeSeconds: 30));
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private KpiRollupService Service(ForgeLinkDbContext db, string zone = "UTC", int lateDays = 2) =>
        new(db, _clock, Options.Create(new ReportingOptions { PlantTimeZone = zone, LateDataDays = lateDays }), NullLogger<KpiRollupService>.Instance);

    private async Task<(int Rows, IReadOnlyList<Contracts.DailyOeeDto> Data)> RunAsync(string zone = "UTC", int lateDays = 2)
    {
        await using var db = _db.CreateContext();
        var result = await Service(db, zone, lateDays).RunAsync(CancellationToken.None);
        await using var read = _db.CreateContext();
        return (result.RowsWritten, await Service(read, zone).GetDailyAsync(null, Today.AddDays(-60), Today, CancellationToken.None));
    }

    private void Counts(string machine, DateTime hourUtc, int good, int reject = 0)
    {
        using var db = _db.CreateContext();
        var bucket = ProductionCounter.BucketFor(hourUtc);
        var counter = db.ProductionCounters.FirstOrDefault(c => c.MachineCode == machine && c.BucketStartUtc == bucket);
        if (counter is null) db.ProductionCounters.Add(counter = new ProductionCounter(machine, bucket));
        counter.Add(good, reject);
        db.SaveChanges();
    }

    private void Stop(string machine, DateTime startUtc, DateTime? endUtc, string reason)
    {
        using var db = _db.CreateContext();
        var e = new DowntimeEvent(machine, startUtc, reason);
        if (endUtc is { } end) e.Close(end);
        db.DowntimeEvents.Add(e);
        db.SaveChanges();
    }

    private static DateTime At(DateOnly day, int hour, int minute = 0) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc);

    [Fact]
    public async Task Closed_day_gets_oee_components_and_the_top_downtime_reason()
    {
        Counts("M1", At(Yesterday, 8), good: 300, reject: 10);
        Counts("M1", At(Yesterday, 9), good: 300, reject: 10);
        Stop("M1", At(Yesterday, 12), At(Yesterday, 14), "jam");
        Stop("M1", At(Yesterday, 16), At(Yesterday, 16, 30), "setup");

        var (_, data) = await RunAsync();
        var row = Assert.Single(data, r => r.MachineCode == "M1" && r.Day == Yesterday);

        Assert.False(row.IsPartial);
        Assert.Equal(1440, row.PlannedMinutes);
        Assert.Equal(150, row.DowntimeMinutes);
        Assert.Equal(600, row.GoodCount);
        Assert.Equal(20, row.RejectCount);
        Assert.Equal(1290.0 / 1440, row.Availability, 3);
        Assert.Equal(60.0 * 620 / (1290 * 60), row.Performance, 3);   // ideal time for all parts / run time
        Assert.Equal(600.0 / 620, row.Quality, 3);
        Assert.Equal(row.Availability * row.Performance * row.Quality, row.Oee, 3);
        Assert.Equal("JAM", row.TopDowntimeReason);

        // Machines that did nothing still get a row (a silent machine is a finding, not a gap).
        var idle = Assert.Single(data, r => r.MachineCode == "M2" && r.Day == Yesterday);
        Assert.Equal(0, idle.Oee);
        Assert.Null(idle.TopDowntimeReason);
    }

    [Fact]
    public async Task Downtime_across_midnight_is_split_between_the_two_days()
    {
        Stop("M1", At(Yesterday.AddDays(-1), 22), At(Yesterday, 3), "breakdown");

        var (_, data) = await RunAsync();
        Assert.Equal(120, data.Single(r => r.MachineCode == "M1" && r.Day == Yesterday.AddDays(-1)).DowntimeMinutes);
        Assert.Equal(180, data.Single(r => r.MachineCode == "M1" && r.Day == Yesterday).DowntimeMinutes);
    }

    [Fact]
    public async Task Today_is_partial_and_planned_only_up_to_now_including_open_stops()
    {
        Stop("M2", At(Today, 14), null, "waiting_material"); // still down at 15:00

        var (_, data) = await RunAsync();
        var row = data.Single(r => r.MachineCode == "M2" && r.Day == Today);
        Assert.True(row.IsPartial);
        Assert.Equal(15 * 60, row.PlannedMinutes);
        Assert.Equal(60, row.DowntimeMinutes);
    }

    [Fact]
    public async Task Running_twice_is_idempotent()
    {
        Counts("M1", At(Yesterday, 10), good: 50);
        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(first.Data.Count, second.Data.Count);
        Assert.Equal(
            first.Data.Select(r => (r.Day, r.MachineCode, r.GoodCount, r.Oee)),
            second.Data.Select(r => (r.Day, r.MachineCode, r.GoodCount, r.Oee)));
        await using var db = _db.CreateContext();
        Assert.Equal(second.Data.Count, await db.DailyMachineKpis.CountAsync());
    }

    [Fact]
    public async Task Late_telemetry_is_absorbed_within_the_window_and_older_days_stay_closed()
    {
        Counts("M1", At(Today.AddDays(-8), 10), good: 1); // history starts 8 days back, so day -7 gets a (zero) row
        Counts("M1", At(Yesterday, 10), good: 50);
        await RunAsync(lateDays: 2);

        // The next morning a gateway replays a backlog: some for yesterday (inside the window), some for a week ago (outside).
        _clock.Advance(TimeSpan.FromDays(1));
        Counts("M1", At(Yesterday, 11), good: 25);
        Counts("M1", At(Today.AddDays(-7), 9), good: 999);

        var (_, data) = await RunAsync(lateDays: 2);
        Assert.Equal(75, data.Single(r => r.MachineCode == "M1" && r.Day == Yesterday).GoodCount);
        Assert.False(data.Single(r => r.MachineCode == "M1" && r.Day == Today).IsPartial); // "today" closed when the clock moved on
        Assert.Equal(0, data.Single(r => r.MachineCode == "M1" && r.Day == Today.AddDays(-7)).GoodCount);
    }

    [Fact]
    public async Task Days_follow_the_plant_time_zone()
    {
        // 03:00 UTC on the 10th is still the evening of the 9th in New York.
        Counts("M1", At(Yesterday, 3), good: 40);

        var (_, data) = await RunAsync("America/New_York");
        Assert.Equal(40, data.Single(r => r.MachineCode == "M1" && r.Day == Yesterday.AddDays(-1)).GoodCount);
        Assert.Equal(0, data.Single(r => r.MachineCode == "M1" && r.Day == Yesterday).GoodCount);
    }

    [Fact]
    public async Task Daylight_saving_day_has_25_planned_hours()
    {
        // US clocks fell back on Sunday 2026-11-01. Data on that day makes the first run start there.
        Counts("M1", new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), good: 1);

        var (_, data) = await RunAsync("America/New_York");
        Assert.Equal(25 * 60, data.Single(r => r.MachineCode == "M1" && r.Day == new DateOnly(2026, 11, 1)).PlannedMinutes);
        Assert.Equal(24 * 60, data.Single(r => r.MachineCode == "M1" && r.Day == new DateOnly(2026, 11, 2)).PlannedMinutes);
    }
}
