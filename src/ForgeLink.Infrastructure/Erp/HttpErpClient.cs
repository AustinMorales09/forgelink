using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Integration;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Infrastructure.Erp;

public sealed class ErpClientOptions
{
    public const string Section = "Erp";

    public string BaseUrl { get; set; } = "http://localhost:5080/";
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public int SyncIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// REST adapter for the ERP. Transport resilience (short retries, attempt timeout, circuit breaker) comes from the
/// HttpClient pipeline; this class classifies the final outcome so the outbox can decide retry vs dead-letter.
/// POST retries are safe because every request carries an Idempotency-Key.
/// </summary>
public sealed class HttpErpClient(HttpClient http, ILogger<HttpErpClient> logger) : IErpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> Routes = new()
    {
        [ErpMessageTypes.ProductionReceipt] = "api/v1/production-receipts",
        [ErpMessageTypes.InventoryTransfer] = "api/v1/inventory-transfers",
        [ErpMessageTypes.InventoryAdjustment] = "api/v1/inventory-adjustments",
        [ErpMessageTypes.Labor] = "api/v1/labor",
        [ErpMessageTypes.QualityHold] = "api/v1/quality-holds",
    };

    public async Task<ErpPostResult> PostAsync(string messageType, string payloadJson, string idempotencyKey, CancellationToken ct)
    {
        if (!Routes.TryGetValue(messageType, out var route))
            return new ErpPostResult(ErpPostOutcome.PermanentFailure, Error: $"No ERP endpoint is mapped for message type '{messageType}'.");

        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return new ErpPostResult(ErpPostOutcome.Accepted, ErpReference: TryReadReference(body));

            if (response.StatusCode == HttpStatusCode.Conflict)
                return new ErpPostResult(ErpPostOutcome.Duplicate, ErpReference: TryReadReference(body));

            var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}";
            var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            return new ErpPostResult(transient ? ErpPostOutcome.TransientFailure : ErpPostOutcome.PermanentFailure, Error: error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ErpPostResult(ErpPostOutcome.TransientFailure, Error: "ERP request timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // HttpRequestException (network), Polly BrokenCircuitException / TimeoutRejectedException, etc.
            logger.LogDebug(ex, "ERP call to {Route} failed", route);
            return new ErpPostResult(ErpPostOutcome.TransientFailure, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ErpItem>> GetItemsChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ErpItem>>($"api/v1/items?modifiedSince={Iso(sinceUtc)}", JsonOptions, ct) ?? [];

    public async Task<IReadOnlyList<ErpWorkOrder>> GetWorkOrdersChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ErpWorkOrder>>($"api/v1/work-orders?modifiedSince={Iso(sinceUtc)}", JsonOptions, ct) ?? [];

    public async Task<ErpWorkOrder?> GetWorkOrderAsync(string number, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/v1/work-orders/{Uri.EscapeDataString(number)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ErpWorkOrder>(JsonOptions, ct);
    }

    private static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static string? TryReadReference(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("erpReference", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string body) => body.Length <= 500 ? body : body[..500];
}
