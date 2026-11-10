using ForgeLink.Application.Abstractions;
using ForgeLink.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Infrastructure.Persistence;

/// <summary>SQL Server implementation backed by <c>dbo.usp_DowntimePareto</c> (set-based, runs next to the data).</summary>
public sealed class SqlServerDowntimeParetoQuery(ForgeLinkDbContext db) : IDowntimeParetoQuery
{
    public async Task<IReadOnlyList<DowntimeReasonDto>> GetAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        // FormattableString => parameterised (@p0, @p1, ...): no SQL injection via the line code.
        var rows = await db.Database
            .SqlQuery<ParetoRow>($"EXEC dbo.usp_DowntimePareto @Line = {line}, @FromUtc = {fromUtc}, @ToUtc = {toUtc}")
            .ToListAsync(ct);
        return rows.Select(r => new DowntimeReasonDto(r.ReasonCode, Math.Round(r.Minutes, 1), r.Occurrences)).ToList();
    }

    private sealed class ParetoRow
    {
        public string ReasonCode { get; set; } = "";
        public double Minutes { get; set; }
        public int Occurrences { get; set; }
    }
}
