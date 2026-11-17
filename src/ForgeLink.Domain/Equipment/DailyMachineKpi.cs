namespace ForgeLink.Domain.Equipment;

/// <summary>
/// One machine's OEE for one plant-local calendar day: the reporting table the daily rollup pipeline loads.
/// Keyed by (machine, day) so re-running the pipeline overwrites instead of duplicating.
/// </summary>
public class DailyMachineKpi
{
    private DailyMachineKpi() { }

    public DailyMachineKpi(string machineCode, string line, DateOnly day)
    {
        MachineCode = Guard.NotBlank(machineCode, nameof(machineCode));
        Line = Guard.NotBlank(line, nameof(line));
        Day = day;
    }

    public string MachineCode { get; private set; } = "";
    public string Line { get; private set; } = "";
    public DateOnly Day { get; private set; }

    public double PlannedMinutes { get; private set; }
    public double DowntimeMinutes { get; private set; }
    public int GoodCount { get; private set; }
    public int RejectCount { get; private set; }
    public double Availability { get; private set; }
    public double Performance { get; private set; }
    public double Quality { get; private set; }
    public double Oee { get; private set; }

    /// <summary>Reason code with the most downtime minutes that day (null when there was no downtime).</summary>
    public string? TopDowntimeReason { get; private set; }

    /// <summary>True while the day is still in progress; the row is recomputed until the day closes.</summary>
    public bool IsPartial { get; private set; }

    public DateTime ComputedAtUtc { get; private set; }

    public void Update(
        string line, TimeSpan planned, TimeSpan downtime, int good, int reject, OeeResult oee, string? topReason, bool isPartial, DateTime computedAtUtc)
    {
        Line = Guard.NotBlank(line, nameof(line));
        PlannedMinutes = Math.Round(planned.TotalMinutes, 2);
        DowntimeMinutes = Math.Round(downtime.TotalMinutes, 2);
        GoodCount = good;
        RejectCount = reject;
        Availability = Math.Round(oee.Availability, 4);
        Performance = Math.Round(oee.Performance, 4);
        Quality = Math.Round(oee.Quality, 4);
        Oee = Math.Round(oee.Oee, 4);
        TopDowntimeReason = topReason;
        IsPartial = isPartial;
        ComputedAtUtc = Guard.Utc(computedAtUtc);
    }
}
