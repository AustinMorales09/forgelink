using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace ForgeLink.Devices;

public sealed record WeightReading(decimal NetKg, bool Stable, string Raw);

public interface IWeighScale
{
    string Name { get; }

    /// <summary>Requests one reading. Implementations should wait for a stable weight up to a timeout.</summary>
    Task<WeightReading> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Parses the common "continuous / print" ASCII format used by many industrial indicators
/// (e.g. <c>ST,NT,+  12.345 kg</c> or <c>US,GS,-0000.50lb</c>): stability flag, gross/net, signed value, unit.
/// </summary>
public static partial class ScaleProtocol
{
    private const decimal KgPerLb = 0.45359237m;

    public static WeightReading Parse(string line)
    {
        var m = Frame().Match(line);
        if (!m.Success) throw new FormatException($"Unrecognised scale frame: '{line.Trim()}'");

        var value = decimal.Parse(m.Groups["value"].Value.Replace(" ", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var unit = m.Groups["unit"].Value.ToLowerInvariant();
        var kg = unit switch
        {
            "kg" => value,
            "g" => value / 1000m,
            "lb" => Math.Round(value * KgPerLb, 4),
            _ => throw new FormatException($"Unsupported unit '{unit}'"),
        };
        return new WeightReading(kg, m.Groups["status"].Value.Equals("ST", StringComparison.OrdinalIgnoreCase), line.Trim());
    }

    /// <summary>Count-by-weight: (gross - tare) / piece weight, rounded down (never over-report production).</summary>
    public static int PiecesFromWeight(decimal netKg, decimal unitWeightKg) =>
        unitWeightKg <= 0 ? throw new ArgumentOutOfRangeException(nameof(unitWeightKg)) : (int)Math.Floor(Math.Max(0, netKg) / unitWeightKg);

    [GeneratedRegex(@"^\s*(?<status>ST|US|OL),(?<mode>GS|NT|TR),\s*(?<value>[+-]?\s*\d+(\.\d+)?)\s*(?<unit>kg|g|lb)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Frame();
}

/// <summary>RS-232/USB-serial indicator in demand mode: send a print command, read one frame, retry until stable.</summary>
public sealed class SerialWeighScale(string portName, int baudRate = 9600, string printCommand = "P\r\n") : IWeighScale
{
    public string Name => $"{portName}@{baudRate}";

    public async Task<WeightReading> ReadAsync(CancellationToken ct = default)
    {
        using var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One) { ReadTimeout = 1500, WriteTimeout = 1000, NewLine = "\r\n" };
        port.Open();
        WeightReading? last = null;
        for (var attempt = 0; attempt < 5 && !ct.IsCancellationRequested; attempt++)
        {
            port.DiscardInBuffer();
            port.Write(printCommand);
            var line = await Task.Run(port.ReadLine, ct);
            last = ScaleProtocol.Parse(line);
            if (last.Stable) return last;
            await Task.Delay(300, ct);
        }
        throw new TimeoutException($"Scale did not settle. Last frame: {last?.Raw ?? "none"}");
    }
}

/// <summary>Deterministic-ish simulator for demos: weight of a tray of parts with noise.</summary>
public sealed class SimulatedWeighScale(decimal pieceWeightKg = 1.24m, int minPieces = 18, int maxPieces = 30) : IWeighScale
{
    public string Name => "simulated";

    public async Task<WeightReading> ReadAsync(CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        var pieces = Random.Shared.Next(minPieces, maxPieces + 1);
        var noise = (decimal)(Random.Shared.NextDouble() * 0.02 - 0.01);
        var kg = Math.Round(pieces * pieceWeightKg + noise, 3);
        var frame = $"ST,NT,+{kg.ToString("0000.000", CultureInfo.InvariantCulture)}kg";
        return ScaleProtocol.Parse(frame);
    }
}
