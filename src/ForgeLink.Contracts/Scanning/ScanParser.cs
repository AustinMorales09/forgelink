using System.Globalization;
using System.Text.RegularExpressions;

namespace ForgeLink.Contracts.Scanning;

public enum ScanKind
{
    Unknown,
    WorkOrder,
    Location,
    Lot,
    Item,
    Badge,
    Gs1,
}

public sealed record ScanResult(
    ScanKind Kind, string Value, string Raw,
    string? Sku = null, string? Lot = null, decimal? Quantity = null,
    string? Gtin = null, string? Serial = null, DateOnly? Expiry = null, string? Sscc = null);

/// <summary>
/// Interprets barcode scans from keyboard-wedge scanners, rugged handhelds (DataWedge) and camera scanners.
/// Supports ForgeLink prefixes (WO-, LOC:, LOT:, SKU:, EMP:) and GS1-128 / GS1 DataMatrix in both raw
/// (FNC1 = ASCII 29) and human-readable "(AI)value" form. AI 240 carries the internal part number.
/// </summary>
public static partial class ScanParser
{
    public const char GroupSeparator = '\u001d';

    // AI -> fixed data length (null = variable, terminated by GS or end of data).
    private static readonly Dictionary<string, int?> Ais = new()
    {
        ["00"] = 18, ["01"] = 14, ["02"] = 14, ["10"] = null, ["11"] = 6, ["13"] = 6, ["15"] = 6, ["17"] = 6,
        ["21"] = null, ["30"] = null, ["37"] = null, ["240"] = null,
        ["3100"] = 6, ["3101"] = 6, ["3102"] = 6, ["3103"] = 6, ["3104"] = 6, ["3105"] = 6,
    };

    public static ScanResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new ScanResult(ScanKind.Unknown, "", raw ?? "");
        var original = raw;
        var s = raw.Trim();

        // AIM symbology identifier, e.g. "]C1" = GS1-128, "]d2" = GS1 DataMatrix, "]Q3" = GS1 QR.
        var aimGs1 = false;
        if (s.Length > 3 && s[0] == ']')
        {
            aimGs1 = s.Substring(1, 2) is "C1" or "d2" or "Q3" or "e0";
            s = s[3..];
        }

        if (aimGs1 || s.StartsWith('(') || s.Contains(GroupSeparator))
            return ParseGs1(s, original);

        var upper = s.ToUpperInvariant();
        if (upper.StartsWith("WO-", StringComparison.Ordinal)) return new ScanResult(ScanKind.WorkOrder, upper, original);
        if (TryPrefix(upper, "WO:", out var wo)) return new ScanResult(ScanKind.WorkOrder, wo, original);
        if (TryPrefix(upper, "LOC:", out var loc)) return new ScanResult(ScanKind.Location, loc, original);
        if (TryPrefix(upper, "LOT:", out var lot)) return new ScanResult(ScanKind.Lot, lot, original, Lot: lot);
        if (TryPrefix(upper, "SKU:", out var sku) || TryPrefix(upper, "ITEM:", out sku)) return new ScanResult(ScanKind.Item, sku, original, Sku: sku);
        if (TryPrefix(upper, "EMP:", out var badge) || TryPrefix(upper, "BADGE:", out badge)) return new ScanResult(ScanKind.Badge, badge, original);

        return new ScanResult(ScanKind.Unknown, upper, original);
    }

    private static bool TryPrefix(string value, string prefix, out string rest)
    {
        rest = value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..].Trim() : "";
        return rest.Length > 0;
    }

    private static ScanResult ParseGs1(string data, string original)
    {
        var fields = data.StartsWith('(') ? ParseHumanReadable(data) : ParseRaw(data);

        string? Get(string ai) => fields.TryGetValue(ai, out var v) ? v : null;

        decimal? qty = null;
        if (decimal.TryParse(Get("30") ?? Get("37"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) qty = count;
        foreach (var (ai, weight) in fields.Where(f => f.Key.StartsWith("310", StringComparison.Ordinal)))
        {
            // 310n: net weight in kg with n implied decimals.
            if (long.TryParse(weight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                qty = w / (decimal)Math.Pow(10, ai[3] - '0');
        }

        DateOnly? expiry = null;
        if (Get("17") is { Length: 6 } exp && DateOnly.TryParseExact(exp.EndsWith("00", StringComparison.Ordinal) ? exp[..4] + "01" : exp, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            expiry = d;

        var sku = Get("240")?.ToUpperInvariant();
        var lot = Get("10")?.ToUpperInvariant();
        var gtin = Get("01") ?? Get("02");
        var value = lot ?? sku ?? gtin ?? Get("00") ?? data;
        return new ScanResult(ScanKind.Gs1, value, original, sku, lot, qty, gtin, Get("21"), expiry, Get("00"));
    }

    private static Dictionary<string, string> ParseHumanReadable(string data)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in HumanReadableAi().Matches(data))
            result[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return result;
    }

    private static Dictionary<string, string> ParseRaw(string data)
    {
        var result = new Dictionary<string, string>();
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] == GroupSeparator) { i++; continue; }

            var ai = Ais.Keys.Where(k => data.AsSpan(i).StartsWith(k)).OrderByDescending(k => k.Length).FirstOrDefault();
            if (ai is null) break; // unknown AI: stop rather than guess
            i += ai.Length;

            string value;
            if (Ais[ai] is { } fixedLength)
            {
                value = data.Substring(i, Math.Min(fixedLength, data.Length - i));
                i += value.Length;
            }
            else
            {
                var end = data.IndexOf(GroupSeparator, i);
                if (end < 0) end = data.Length;
                value = data[i..end];
                i = end;
            }
            result[ai] = value;
        }
        return result;
    }

    [GeneratedRegex(@"\((\d{2,4})\)([^(]*)")]
    private static partial Regex HumanReadableAi();
}
