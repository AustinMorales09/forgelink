using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ForgeLink.Devices;

public sealed record LotLabel(string Sku, string Description, string LotNumber, decimal Quantity, string Uom, string WorkOrderNumber, DateTime ProducedUtc);

/// <summary>
/// Builds a 4x3" ZPL II lot label with a GS1-128 barcode: (240) part number, (10) lot, (30) quantity.
/// Scanning it later fills SKU, lot and quantity in one scan (see ScanParser).
/// </summary>
public static class ZplLabelBuilder
{
    public static string Build(LotLabel label)
    {
        var qty = label.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
        var gs1 = $"(240){label.Sku}(10){label.LotNumber}(30){decimal.Truncate(label.Quantity).ToString(CultureInfo.InvariantCulture)}";

        var zpl = new StringBuilder();
        zpl.Append("^XA^CI28^PW812^LL609");                                           // UTF-8, 4in x 3in at 203 dpi
        zpl.Append("^FO30,30^A0N,38,38").Append(Field(label.Sku)).Append("^FS");
        zpl.Append("^FO30,75^A0N,26,26").Append(Field(Truncate(label.Description, 48))).Append("^FS");
        zpl.Append("^FO30,120^A0N,30,30").Append(Field($"LOT {label.LotNumber}")).Append("^FS");
        zpl.Append("^FO30,160^A0N,30,30").Append(Field($"QTY {qty} {label.Uom}")).Append("^FS");
        zpl.Append("^FO430,160^A0N,24,24").Append(Field($"{label.WorkOrderNumber}  {label.ProducedUtc:yyyy-MM-dd HH:mm}Z")).Append("^FS");
        zpl.Append("^FO30,215^BY2,3,110^BCN,110,Y,N,N,D").Append(Field(gs1)).Append("^FS");   // GS1-128 (mode D)
        zpl.Append("^FO620,20^BQN,2,5").Append(Field($"QA,LOT:{label.LotNumber}")).Append("^FS"); // QR for phone cameras
        zpl.Append("^XZ");
        return zpl.ToString();
    }

    /// <summary>
    /// ZPL treats ^ and ~ as commands. ^FH enables hex escapes so operator-entered text cannot inject printer commands.
    /// </summary>
    public static string Field(string value)
    {
        var escaped = new StringBuilder("^FH\\^FD");
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '^' => "\\5E",
                '~' => "\\7E",
                '\\' => "\\5C",
                _ => c.ToString(),
            });
        }
        return escaped.ToString();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

public interface ILabelPrinter
{
    string Name { get; }
    Task PrintAsync(string zpl, CancellationToken ct = default);
}

/// <summary>Zebra/Honeywell/SATO network printers accept raw ZPL on TCP port 9100.</summary>
public sealed class TcpZplPrinter(string host, int port = 9100) : ILabelPrinter
{
    public string Name => $"{host}:{port}";

    public async Task PrintAsync(string zpl, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(zpl), timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
}

/// <summary>Writes .zpl files (preview at labelary.com) when no printer is attached.</summary>
public sealed class FileLabelPrinter(string folder) : ILabelPrinter
{
    public string Name => $"file:{folder}";

    public string? LastFile { get; private set; }

    public async Task PrintAsync(string zpl, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        LastFile = Path.Combine(folder, $"label-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.zpl");
        await File.WriteAllTextAsync(LastFile, zpl, ct);
    }
}
