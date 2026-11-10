using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForgeLink.Contracts;

/// <summary>
/// Deterministic lot numbering shared by server and stations: one output lot per work order per production day.
/// Because a station can compute it offline, labels printed during a network outage match what the server records later.
/// </summary>
public static class LotNumbering
{
    public static string ForWorkOrder(string workOrderNumber, DateTime utc) =>
        $"{workOrderNumber.Trim().ToUpperInvariant()}-{utc.ToUniversalTime():yyMMdd}";
}

/// <summary>Standard location naming for production lines.</summary>
public static class Locations
{
    public static string LineWip(string line) => $"{line.ToUpperInvariant()}-WIP";
    public static string LineOutput(string line) => $"{line.ToUpperInvariant()}-OUT";
}

/// <summary>
/// HMAC-SHA256 webhook signature: <c>sha256=hex(HMAC(secret, "{timestamp}.{body}"))</c>.
/// The timestamp is signed to stop replay of captured requests.
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-Erp-Signature";
    public const string TimestampHeader = "X-Erp-Timestamp";

    public static string Compute(string secret, long unixTimestamp, string body)
    {
        var data = Encoding.UTF8.GetBytes($"{unixTimestamp.ToString(CultureInfo.InvariantCulture)}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    public static bool Verify(string secret, string? timestampHeader, string? signatureHeader, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > tolerance.TotalSeconds) return false;

        var expected = Encoding.UTF8.GetBytes(Compute(secret, ts, body));
        var actual = Encoding.UTF8.GetBytes(signatureHeader.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
