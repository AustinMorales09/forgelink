namespace ForgeLink.Domain.Equipment;

/// <summary>Overall Equipment Effectiveness = Availability x Performance x Quality, each in [0, 1].</summary>
public readonly record struct OeeResult(double Availability, double Performance, double Quality)
{
    public double Oee => Availability * Performance * Quality;

    public static OeeResult Zero => new(0, 0, 0);
}

public static class OeeCalculator
{
    /// <param name="plannedTime">Planned production time in the window.</param>
    /// <param name="downtime">Unplanned stop time inside the window.</param>
    /// <param name="totalCount">Good + reject parts.</param>
    /// <param name="goodCount">Parts that passed first time.</param>
    /// <param name="idealCycleSeconds">Fastest theoretical seconds per part.</param>
    public static OeeResult Calculate(TimeSpan plannedTime, TimeSpan downtime, int totalCount, int goodCount, double idealCycleSeconds)
    {
        if (plannedTime <= TimeSpan.Zero) return OeeResult.Zero;

        var runTime = plannedTime - downtime;
        if (runTime < TimeSpan.Zero) runTime = TimeSpan.Zero;

        var availability = runTime / plannedTime;
        var performance = runTime > TimeSpan.Zero && idealCycleSeconds > 0
            ? idealCycleSeconds * totalCount / runTime.TotalSeconds
            : 0;
        var quality = totalCount > 0 ? (double)Math.Min(goodCount, totalCount) / totalCount : 0;

        return new OeeResult(Clamp(availability), Clamp(performance), Clamp(quality));
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
