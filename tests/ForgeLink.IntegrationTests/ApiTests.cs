using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ForgeLink.Application.Abstractions;
using ForgeLink.Client;
using ForgeLink.Client.Offline;
using ForgeLink.Contracts;
using ForgeLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeLink.IntegrationTests;

/// <summary>Boots the real web host (Development auth, SQLite file, workers off) with a fake ERP.</summary>
public class ForgeLinkFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"forgelink-it-{Guid.NewGuid():N}.db");

    public FakeErp Erp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:ForgeLink", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("Erp:WebhookSecret", "test-secret");
        builder.ConfigureTestServices(services => services.AddSingleton<IErpClient>(Erp));
        ConfigureMore(builder);
    }

    /// <summary>Hook for fixtures that need extra settings (e.g. a tiny rate limit).</summary>
    protected virtual void ConfigureMore(IWebHostBuilder builder) { }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public sealed class FakeErp : IErpClient
{
    public List<ErpWorkOrder> WorkOrders { get; } = [];

    public Task<ErpPostResult> PostAsync(string messageType, string payloadJson, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ErpPostResult(ErpPostOutcome.Accepted, "ERP-T"));

    public Task<IReadOnlyList<ErpItem>> GetItemsChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErpItem>>([]);

    public Task<IReadOnlyList<ErpWorkOrder>> GetWorkOrdersChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErpWorkOrder>>(WorkOrders);

    public Task<ErpWorkOrder?> GetWorkOrderAsync(string number, CancellationToken ct) =>
        Task.FromResult(WorkOrders.FirstOrDefault(w => w.Number == number));
}

public sealed class ApiTests(ForgeLinkFactory factory) : IClassFixture<ForgeLinkFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string? apiKey = null)
    {
        var client = factory.CreateClient();
        if (apiKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private async Task EnsureStartedAsync(string wo, string machine)
    {
        var response = await Client().PostAsJsonAsync($"api/work-orders/{wo}/start", new StartWorkOrderRequest(machine, "EMP-1"));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("health/ready")).StatusCode);
    }

    [Fact]
    public async Task Production_report_is_idempotent_over_http()
    {
        await EnsureStartedAsync("WO-24002", "L1-CNC-02");
        var client = Client();
        var body = new ReportProductionRequest(12, 0);

        async Task<HttpResponseMessage> Post()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/work-orders/WO-24002/production") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", "station-1-report-0001");
            return await client.SendAsync(request);
        }

        var first = await Post();
        var second = await Post(); // e.g. the station timed out and retried
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var a = await first.Content.ReadFromJsonAsync<ProductionReportResult>(Json);
        var b = await second.Content.ReadFromJsonAsync<ProductionReportResult>(Json);
        Assert.Equal(a!.TransactionId, b!.TransactionId);
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeLinkDbContext>();
        Assert.Equal(12, (await db.WorkOrders.SingleAsync(w => w.Number == "WO-24002")).QuantityCompleted);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.PartitionKey == "wo:WO-24002"));
    }

    [Fact]
    public async Task Idempotency_key_is_required_and_cannot_be_reused_for_a_different_body()
    {
        await EnsureStartedAsync("WO-24003", "L2-CNC-01");
        var client = Client();

        var missing = await client.PostAsJsonAsync("api/work-orders/WO-24003/production", new ReportProductionRequest(1, 0));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        async Task<HttpResponseMessage> Post(decimal qty)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/work-orders/WO-24003/production")
            {
                Content = JsonContent.Create(new ReportProductionRequest(qty, 0)),
            };
            request.Headers.Add("Idempotency-Key", "reuse-test");
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await Post(1)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(2)).StatusCode);
    }

    [Fact]
    public async Task Business_rule_errors_are_problem_details_with_stable_codes()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/inventory/moves")
        {
            Content = JsonContent.Create(new MoveInventoryRequest("AL6061-BAR-50", "HEAT-88233", "RM-01", "L1-WIP", 999_999)),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("inventory.insufficient", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Api_keys_authenticate_devices_and_roles_are_enforced()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("not-a-key").GetAsync("api/machines")).StatusCode);

        var station = Client("dev-station-L1-01");
        Assert.Equal(HttpStatusCode.OK, (await station.GetAsync("api/machines")).StatusCode);

        // Operators cannot make free-form inventory adjustments; supervisors can.
        var adjust = new HttpRequestMessage(HttpMethod.Post, "api/inventory/adjustments")
        {
            Content = JsonContent.Create(new AdjustInventoryRequest("PKG-TRAY-24", "", "FG-01", -1, "DAMAGE")),
        };
        adjust.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await station.SendAsync(adjust)).StatusCode);

        // OT gateways may post telemetry but cannot read work orders.
        var gateway = Client("dev-ot-gateway");
        Assert.Equal(HttpStatusCode.Forbidden, (await gateway.GetAsync("api/work-orders")).StatusCode);
        var signals = await gateway.PostAsJsonAsync("api/machines/signals", new[] { new MachineSignalDto("L1-WASH-01", "heartbeat") });
        Assert.Equal(HttpStatusCode.Accepted, signals.StatusCode);
    }

    [Fact]
    public async Task Erp_webhook_requires_a_valid_signature_and_pulls_the_order()
    {
        factory.Erp.WorkOrders.Add(new ErpWorkOrder("WO-24999", "BRK-1002", 50, "L2", DateTime.UtcNow.AddDays(2), "Released", DateTime.UtcNow));
        var body = JsonSerializer.Serialize(new ErpWebhookEvent("workorder.released", "WO-24999", null), Json);

        HttpRequestMessage Build(string secret)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var request = new HttpRequestMessage(HttpMethod.Post, "api/erp/webhooks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add(WebhookSignature.TimestampHeader, ts.ToString());
            request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(secret, ts, body));
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().SendAsync(Build("wrong-secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Client().SendAsync(Build("test-secret"))).StatusCode);

        var wo = await Client().GetFromJsonAsync<WorkOrderDto>("api/work-orders/WO-24999", Json);
        Assert.Equal(("Released", 50m), (wo!.Status, wo.QuantityOrdered));
    }

    [Fact]
    public async Task Mvc_console_and_blazor_shell_render()
    {
        var console = await Client().GetStringAsync("integration");
        Assert.Contains("ERP integration console", console);

        var audit = await Client().GetStringAsync("integration/audit");
        Assert.Contains("Audit trail", audit);

        var blazor = await Client().GetStringAsync("/");
        Assert.Contains("_framework/blazor.web.js", blazor);
    }

    [Fact]
    public async Task Shared_http_client_works_end_to_end_and_queues_when_server_unreachable()
    {
        // Online: the same client the WPF/MAUI apps use, pointed at the test server.
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", "dev-handheld-01");
        var online = new ForgeLinkHttpClient(http);
        var inventory = await online.GetInventoryAsync("AL6061-BAR-50");
        Assert.Contains(inventory, b => b.LotNumber == "HEAT-88233");

        // Offline: a client whose server is unreachable queues the command instead of losing it.
        var dead = new ForgeLinkHttpClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(2) });
        var queuePath = Path.Combine(Path.GetTempPath(), $"forgelink-queue-{Guid.NewGuid():N}.db");
        var queue = new OfflineCommandQueue(queuePath);
        var current = dead;
        var api = new StoreAndForwardForgeLinkApi(() => current, queue, NullLogger<StoreAndForwardForgeLinkApi>.Instance);

        var move = new MoveInventoryRequest("AL6061-BAR-50", "HEAT-88233", "RM-01", "L2-WIP", 25);
        var queued = await Assert.ThrowsAsync<QueuedForSyncException>(() => api.MoveInventoryAsync(move));
        Assert.False(api.IsOnline);
        Assert.Equal(1, await queue.CountAsync());

        // Connectivity returns: replay with the original idempotency key.
        current = online;
        var (sent, rejected, remaining) = await api.FlushAsync();
        Assert.Equal((1, 0, 0), (sent, rejected, remaining));

        var after = await online.GetInventoryAsync("AL6061-BAR-50", "L2-WIP");
        Assert.Contains(after, b => b.LotNumber == "HEAT-88233" && b.Quantity == 25);
        Assert.NotEmpty(queued.IdempotencyKey);
    }
}
