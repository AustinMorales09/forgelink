using ForgeLink.Contracts;
using ForgeLink.Contracts.Scanning;
using ForgeLink.Devices;
using ForgeLink.Infrastructure.Ot;

namespace ForgeLink.UnitTests.Devices;

public class ScanParserTests
{
    [Theory]
    [InlineData("wo-24001", ScanKind.WorkOrder, "WO-24001")]
    [InlineData("WO:24001", ScanKind.WorkOrder, "24001")]
    [InlineData("LOC:l1-wip", ScanKind.Location, "L1-WIP")]
    [InlineData("EMP:1042", ScanKind.Badge, "1042")]
    [InlineData("SKU:brk-1001", ScanKind.Item, "BRK-1001")]
    [InlineData("LOT:HEAT-88231", ScanKind.Lot, "HEAT-88231")]
    [InlineData("random text", ScanKind.Unknown, "RANDOM TEXT")]
    public void Prefixed_barcodes(string raw, ScanKind kind, string value)
    {
        var r = ScanParser.Parse(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(value, r.Value);
    }

    [Fact]
    public void Gs1_human_readable_lot_label()
    {
        var r = ScanParser.Parse("(240)BRK-1001(10)WO-24001-261109(30)24");
        Assert.Equal(ScanKind.Gs1, r.Kind);
        Assert.Equal("BRK-1001", r.Sku);
        Assert.Equal("WO-24001-261109", r.Lot);
        Assert.Equal(24, r.Quantity);
    }

    [Fact]
    public void Gs1_raw_with_fnc1_separators_and_aim_prefix()
    {
        const char gs = ScanParser.GroupSeparator;
        // ]C1 = GS1-128; GTIN (fixed 14) + expiry (fixed 6) + lot (variable, GS-terminated) + net weight kg 3 decimals.
        var r = ScanParser.Parse("]C1" + "01" + "09501101530003" + "17" + "281231" + "10" + "ABC123" + gs + "3103" + "001250");
        Assert.Equal(ScanKind.Gs1, r.Kind);
        Assert.Equal("09501101530003", r.Gtin);
        Assert.Equal(new DateOnly(2028, 12, 31), r.Expiry);
        Assert.Equal("ABC123", r.Lot);
        Assert.Equal(1.250m, r.Quantity);
    }

    [Fact]
    public void Blank_scan_is_unknown() => Assert.Equal(ScanKind.Unknown, ScanParser.Parse("  ").Kind);
}

public class LabelAndScaleTests
{
    [Fact]
    public void Zpl_label_contains_gs1_barcode_that_round_trips_through_the_scanner()
    {
        var zpl = ZplLabelBuilder.Build(new LotLabel("BRK-1001", "Caliper", "WO-24001-261109", 24, "EA", "WO-24001", DateTime.UtcNow));
        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ", zpl);
        Assert.Contains("(240)BRK-1001(10)WO-24001-261109(30)24", zpl);

        var scan = ScanParser.Parse("(240)BRK-1001(10)WO-24001-261109(30)24");
        Assert.Equal(("BRK-1001", "WO-24001-261109", 24m), (scan.Sku, scan.Lot, scan.Quantity!.Value));
    }

    [Fact]
    public void Zpl_field_data_cannot_inject_printer_commands()
    {
        var field = ZplLabelBuilder.Field("evil^XZ~JR");
        Assert.DoesNotContain("^XZ", field);
        Assert.DoesNotContain("~JR", field);
        Assert.Contains("\\5EXZ", field);
    }

    [Theory]
    [InlineData("ST,NT,+  12.345 kg", 12.345, true)]
    [InlineData("US,GS,-0000.50kg", -0.5, false)]
    [InlineData("ST,GS,+10.00lb", 4.5359, true)]
    [InlineData("ST,NT,+1250 g", 1.25, true)]
    public void Scale_frames(string frame, double kg, bool stable)
    {
        var r = ScaleProtocol.Parse(frame);
        Assert.Equal((decimal)kg, r.NetKg);
        Assert.Equal(stable, r.Stable);
    }

    [Fact]
    public void Count_by_weight_rounds_down() => Assert.Equal(24, ScaleProtocol.PiecesFromWeight(29.95m, 1.24m));

    [Fact]
    public void Garbage_scale_frame_is_rejected() => Assert.Throws<FormatException>(() => ScaleProtocol.Parse("hello"));
}

public class IntegrationPrimitiveTests
{
    [Fact]
    public void Webhook_signature_round_trip_and_tamper_detection()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        const string body = """{"eventType":"workorder.released","workOrderNumber":"WO-1"}""";
        var sig = WebhookSignature.Compute("secret", ts, body);

        Assert.True(WebhookSignature.Verify("secret", ts.ToString(), sig, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("secret", ts.ToString(), sig, body.Replace("WO-1", "WO-2"), now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("wrong", ts.ToString(), sig, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("secret", ts.ToString(), sig, body, now.AddMinutes(10), TimeSpan.FromMinutes(5))); // replay
    }

    [Fact]
    public void Lot_numbering_is_deterministic_per_work_order_and_day() =>
        Assert.Equal("WO-24001-261109", LotNumbering.ForWorkOrder("wo-24001", new DateTime(2026, 11, 9, 23, 59, 0, DateTimeKind.Utc)));

    [Fact]
    public void Mqtt_topic_and_payload_are_normalised_to_a_signal()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"state":"Down","reason":"CHIP_JAM","ts":"2026-11-09T10:00:00Z"}""");
        var s = MqttTelemetryListener.Parse("plant/L1/L1-CNC-01/state", payload);
        Assert.NotNull(s);
        Assert.Equal(("L1-CNC-01", "state", "Down", "CHIP_JAM"), (s!.MachineCode, s.SignalType, s.State, s.Reason));
        Assert.Null(MqttTelemetryListener.Parse("other/topic", payload));
    }
}
