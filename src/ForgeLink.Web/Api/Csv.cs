using System.Globalization;
using System.Text;
using ForgeLink.Contracts;

namespace ForgeLink.Web.Api;

/// <summary>RFC 4180 CSV output with invariant formatting, so a German Excel and a US Excel read the same numbers.</summary>
public static class Csv
{
    public static string DailyOee(IEnumerable<DailyOeeDto> rows)
    {
        var sb = new StringBuilder();
        sb.Append("day,line,machine,planned_min,downtime_min,good,reject,availability_pct,performance_pct,quality_pct,oee_pct,top_downtime_reason,partial\r\n");
        foreach (var r in rows)
        {
            Line(sb,
                r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Line, r.MachineCode,
                Num(r.PlannedMinutes), Num(r.DowntimeMinutes),
                r.GoodCount.ToString(CultureInfo.InvariantCulture), r.RejectCount.ToString(CultureInfo.InvariantCulture),
                Pct(r.Availability), Pct(r.Performance), Pct(r.Quality), Pct(r.Oee),
                r.TopDowntimeReason ?? "", r.IsPartial ? "yes" : "no");
        }
        return sb.ToString();
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pct(double fraction) => (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(fields[i]));
        }
        sb.Append("\r\n");
    }

    /// <summary>Quotes when needed, and defuses spreadsheet formulas (a reason code like "=HYPERLINK(...)" stays text).</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
