using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ForgeLink.Contracts;

// A deliberately unreliable ERP: random 503s and latency (Chaos section), strict validation (422),
// and idempotency-key handling, so the ForgeLink outbox, retries and dead-lettering can be exercised end to end.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ErpStore>();
builder.Services.AddSingleton<ChaosSettings>(sp => sp.GetRequiredService<IConfiguration>().GetSection("Chaos").Get<ChaosSettings>() ?? new ChaosSettings());
builder.Services.AddHttpClient();
var app = builder.Build();

var apiKey = app.Configuration["ApiKey"] ?? "dev-erp-key";

// API key + chaos middleware for the ERP API surface.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    if (ctx.Request.Headers["X-Api-Key"] != apiKey)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var chaos = ctx.RequestServices.GetRequiredService<ChaosSettings>();
    if (chaos.LatencyMs > 0) await Task.Delay(Random.Shared.Next(chaos.LatencyMs / 2, chaos.LatencyMs + 1));
    if (HttpMethods.IsPost(ctx.Request.Method) && Random.Shared.NextDouble() < chaos.FailureRate)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("ERP application server busy (simulated)");
        return;
    }
    await next();
});

// ---------- master data ----------
app.MapGet("/api/v1/items", (string? modifiedSince, ErpStore store) => store.Items.Values
    .Where(i => i.ModifiedAtUtc >= ParseUtc(modifiedSince)).OrderBy(i => i.ModifiedAtUtc));

app.MapGet("/api/v1/work-orders", (string? modifiedSince, ErpStore store) => store.WorkOrders.Values
    .Where(w => w.ModifiedAtUtc >= ParseUtc(modifiedSince)).OrderBy(w => w.ModifiedAtUtc));

app.MapGet("/api/v1/work-orders/{number}", (string number, ErpStore store) =>
    store.WorkOrders.TryGetValue(number.ToUpperInvariant(), out var wo) ? Results.Ok(wo) : Results.NotFound());

// ---------- inbound transactions ----------
string[] kinds = ["production-receipts", "inventory-transfers", "inventory-adjustments", "labor", "quality-holds"];
foreach (var kind in kinds)
{
    app.MapPost($"/api/v1/{kind}", async (HttpRequest request, ErpStore store) =>
    {
        var key = request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key)) return Results.Problem("Idempotency-Key header is required.", statusCode: 400);

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        // Same key again: return the original booking (do not post twice).
        if (store.Transactions.TryGetValue(key, out var existing))
            return Results.Ok(new { erpReference = existing.ErpReference, replayed = true });

        var error = Validate(kind, body, store);
        if (error is not null) return Results.Problem(error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "ERP validation failed");

        var booked = new BookedTransaction(kind, key, $"ERP-{Interlocked.Increment(ref store.Sequence):D7}", DateTime.UtcNow, body);
        store.Transactions[key] = booked;
        return Results.Created($"/api/v1/transactions/{booked.ErpReference}", new { erpReference = booked.ErpReference });
    });
}

app.MapGet("/api/v1/transactions", (ErpStore store) =>
    store.Transactions.Values.OrderByDescending(t => t.BookedAtUtc).Take(200)
        .Select(t => new { t.Kind, t.ErpReference, t.IdempotencyKey, t.BookedAtUtc, payload = JsonDocument.Parse(t.Payload).RootElement }));

// ---------- admin (no API key: local demo only) ----------
app.MapGet("/", (ErpStore store, ChaosSettings chaos) => new
{
    name = "ForgeLink ERP simulator",
    chaos,
    items = store.Items.Count,
    workOrders = store.WorkOrders.Count,
    transactionsBooked = store.Transactions.Count,
    byKind = store.Transactions.Values.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count()),
});

app.MapPost("/admin/chaos", (double? failureRate, int? latencyMs, ChaosSettings chaos) =>
{
    if (failureRate is not null) chaos.FailureRate = Math.Clamp(failureRate.Value, 0, 1);
    if (latencyMs is not null) chaos.LatencyMs = Math.Max(0, latencyMs.Value);
    return chaos;
});

// Release a new work order in the "ERP" and push a signed webhook to ForgeLink (event-driven path).
app.MapPost("/admin/work-orders", async (ReleaseRequest req, ErpStore store, IHttpClientFactory http, IConfiguration config) =>
{
    var number = $"WO-{Interlocked.Increment(ref store.WoSequence)}";
    var wo = new ErpWorkOrderModel(number, req.Sku.ToUpperInvariant(), req.Quantity, req.Line.ToUpperInvariant(),
        DateTime.UtcNow.Date.AddDays(req.DueInDays), "Released", DateTime.UtcNow);
    store.WorkOrders[number] = wo;

    var body = JsonSerializer.Serialize(new ErpWebhookEvent("workorder.released", number, wo.Sku), JsonSerializerOptions.Web);
    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var message = new HttpRequestMessage(HttpMethod.Post, config["ForgeLinkWebhookUrl"] ?? "http://localhost:5100/api/erp/webhooks")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    message.Headers.Add(WebhookSignature.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
    message.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(config["WebhookSecret"] ?? "dev-webhook-secret", ts, body));

    string webhook;
    try
    {
        using var response = await http.CreateClient().SendAsync(message);
        webhook = $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }
    catch (HttpRequestException ex)
    {
        webhook = $"failed ({ex.Message}); ForgeLink will pick it up on the next scheduled sync";
    }
    return Results.Ok(new { workOrder = wo, webhook });
});

app.Run();

static DateTime ParseUtc(string? value) =>
    DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
        ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
        : DateTime.MinValue;

// ERP business validation: these become *permanent* failures (dead letters) in ForgeLink.
static string? Validate(string kind, string body, ErpStore store)
{
    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    if (root.TryGetProperty("sku", out var sku) && !store.Items.ContainsKey(sku.GetString()!.ToUpperInvariant()))
        return $"Item {sku.GetString()} does not exist in the ERP item master.";
    if (kind == "production-receipts" && root.TryGetProperty("workOrderNumber", out var wo)
        && store.WorkOrders.TryGetValue(wo.GetString()!, out var order) && order.Status != "Released")
        return $"Work order {wo.GetString()} is {order.Status} in the ERP; production cannot be booked.";
    if (kind == "inventory-adjustments" && root.TryGetProperty("reasonCode", out var reason)
        && reason.GetString() is not ("CYCLE_COUNT" or "DAMAGE" or "FOUND" or "LOST" or "CORRECTION" or "SCRAP"))
        return $"Reason code {reason.GetString()} is not configured in the ERP.";
    return null;
}

public sealed class ChaosSettings
{
    public double FailureRate { get; set; } = 0.15;
    public int LatencyMs { get; set; } = 150;
}

public sealed record ReleaseRequest(string Sku, decimal Quantity, string Line, int DueInDays = 2);

public sealed record ErpItemModel(string Sku, string Description, string Uom, decimal? UnitWeightKg, bool LotControlled, DateTime ModifiedAtUtc);

public sealed record ErpWorkOrderModel(string Number, string Sku, decimal QuantityOrdered, string Line, DateTime DueDateUtc, string Status, DateTime ModifiedAtUtc);

public sealed record BookedTransaction(string Kind, string IdempotencyKey, string ErpReference, DateTime BookedAtUtc, string Payload);

public sealed class ErpStore
{
    public long Sequence;
    public int WoSequence = 24100;

    public ConcurrentDictionary<string, ErpItemModel> Items { get; } = new();
    public ConcurrentDictionary<string, ErpWorkOrderModel> WorkOrders { get; } = new();
    public ConcurrentDictionary<string, BookedTransaction> Transactions { get; } = new();

    public ErpStore()
    {
        var modified = DateTime.UtcNow.AddDays(-30);
        foreach (var item in new[]
                 {
                     new ErpItemModel("AL6061-BAR-50", "Aluminium 6061 bar, 50 mm", "KG", null, true, modified),
                     new ErpItemModel("BRK-1001", "Brake caliper housing, machined", "EA", 1.240m, true, modified),
                     new ErpItemModel("BRK-1002", "Brake caliper bracket", "EA", 0.410m, true, modified),
                     new ErpItemModel("PKG-TRAY-24", "Returnable tray, 24 pocket", "EA", null, false, modified),
                     new ErpItemModel("BRK-2001", "Brake caliper housing, rev B", "EA", 1.180m, true, DateTime.UtcNow),
                 })
            Items[item.Sku] = item;

        var today = DateTime.UtcNow.Date;
        foreach (var wo in new[]
                 {
                     new ErpWorkOrderModel("WO-24001", "BRK-1001", 400, "L1", today.AddDays(1), "Released", modified),
                     new ErpWorkOrderModel("WO-24002", "BRK-1001", 250, "L1", today.AddDays(3), "Released", modified),
                     new ErpWorkOrderModel("WO-24003", "BRK-1002", 600, "L2", today.AddDays(2), "Released", modified),
                     new ErpWorkOrderModel("WO-24004", "BRK-2001", 150, "L1", today.AddDays(4), "Released", DateTime.UtcNow),
                 })
            WorkOrders[wo.Number] = wo;
    }
}
