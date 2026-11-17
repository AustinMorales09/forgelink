using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ForgeLink.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace ForgeLink.IntegrationTests;

public sealed class ApiDocsAndPagingTests(ForgeLinkFactory factory) : IClassFixture<ForgeLinkFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OpenApi_document_is_public_and_describes_auth_and_endpoints()
    {
        var response = await factory.CreateClient().GetAsync("openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ForgeLink API", root.GetProperty("info").GetProperty("title").GetString());

        var paths = root.GetProperty("paths");
        foreach (var path in new[] { "/api/work-orders", "/api/inventory/ledger", "/api/machines/signals", "/api/lots/{lotNumber}/trace" })
            Assert.True(paths.TryGetProperty(path, out _), $"missing {path}");

        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("X-Api-Key", schemes.GetProperty("ApiKey").GetProperty("name").GetString());
        Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());

        // XML doc comments flow into the document.
        var ledger = paths.GetProperty("/api/inventory/ledger").GetProperty("get");
        var text = (ledger.TryGetProperty("summary", out var s) ? s.GetString() : "") + (ledger.TryGetProperty("description", out var d) ? d.GetString() : "");
        Assert.True(text.Contains("cursor", StringComparison.OrdinalIgnoreCase), ledger.GetRawText());
    }

    [Fact]
    public async Task Api_reference_ui_is_served()
    {
        var response = await factory.CreateClient().GetAsync("docs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Ledger_pages_through_the_seeded_history_without_gaps_or_repeats()
    {
        var client = factory.CreateClient();
        // Make sure there is a multi-page history: a few adjustments on top of whatever the seed contains.
        for (var i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/inventory/adjustments")
            {
                Content = JsonContent.Create(new AdjustInventoryRequest("PKG-TRAY-24", "", "FG-01", 1, "FOUND")),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
        }
        var all = await client.GetFromJsonAsync<PageDto<InventoryTransactionDto>>("api/inventory/ledger?limit=500", Json);
        Assert.NotNull(all);
        Assert.True(all.Items.Count > 3, "seed data should contain several transactions");

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = "api/inventory/ledger?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await client.GetFromJsonAsync<PageDto<InventoryTransactionDto>>(url, Json);
            Assert.NotNull(page);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null && seen.Count <= all.Items.Count);

        Assert.Equal(all.Items.Select(i => i.Id), seen);
    }

    [Fact]
    public async Task Ledger_rejects_a_bad_cursor_with_problem_details()
    {
        var response = await factory.CreateClient().GetAsync("api/inventory/ledger?cursor=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation", doc.RootElement.GetProperty("code").GetString());
    }
}

/// <summary>Same host with a tiny request budget so the limiter can be observed.</summary>
public sealed class RateLimitedFactory : ForgeLinkFactory
{
    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("RateLimiting:PermitsPerMinute", "3");
        builder.UseSetting("RateLimiting:TelemetryPermitsPerMinute", "50");
    }
}

public sealed class RateLimitTests(RateLimitedFactory factory) : IClassFixture<RateLimitedFactory>
{
    [Fact]
    public async Task Caller_over_budget_gets_429_with_retry_after_while_health_and_telemetry_stay_open()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/machines")).StatusCode);

        var limited = await client.GetAsync("api/machines");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After should be set");
        using (var doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()))
            Assert.Equal("rate-limited", doc.RootElement.GetProperty("code").GetString());

        // Not part of the API budget.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("openapi/v1.json")).StatusCode);

        // Telemetry ingestion has its own, larger bucket, so a chatty gateway is not starved by (or starving) other calls.
        var signal = new[] { new MachineSignalDto("L1-CNC-01", "heartbeat", null, null, null, null, DateTime.UtcNow) };
        var gateway = factory.CreateClient();
        gateway.DefaultRequestHeaders.Add("X-Api-Key", "dev-ot-gateway");
        var ingest = await gateway.PostAsJsonAsync("api/machines/signals", signal);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, ingest.StatusCode);
    }
}
