using System.Buffers.Text;
using System.Globalization;
using System.Text;
using ForgeLink.Domain;

namespace ForgeLink.Application.Common;

/// <summary>
/// Opaque keyset cursor for lists ordered newest-first by a timestamp.
/// It holds the timestamp of the last row returned and how many rows at exactly that timestamp were already returned,
/// so rows inserted while a client pages never shift or duplicate results (unlike OFFSET paging).
/// </summary>
public readonly record struct TimeCursor(DateTime AtUtc, int SeenAtSameTime)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{AtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{SeenAtSameTime.ToString(CultureInfo.InvariantCulture)}"));

    public static TimeCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seen)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks && seen > 0)
                return new TimeCursor(new DateTime(ticks, DateTimeKind.Utc), seen);
        }
        catch (FormatException)
        {
        }
        throw new DomainException("validation", "The cursor is not valid. Start again without a cursor.");
    }

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>Builds the cursor for the page after <paramref name="page"/> (null when the page was not full).</summary>
    public static string? Next<T>(IReadOnlyList<T> page, int limit, Func<T, DateTime> at, TimeCursor? previous)
    {
        if (page.Count < limit) return null;
        var last = at(page[^1]);
        var sameTime = page.Count(r => at(r) == last);
        // If the whole page shares the previous cursor's timestamp, keep counting from where that cursor left off.
        if (previous is { } p && p.AtUtc == last && sameTime == page.Count) sameTime += p.SeenAtSameTime;
        return new TimeCursor(last, sameTime).Encode();
    }
}
