using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ForgeLink.Contracts;

namespace ForgeLink.Client;

public sealed class ForgeLinkClientOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5100/";

    /// <summary>Device API key (station PCs / handhelds). Keep it in a secure store, not in source.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Path of the local SQLite queue used while offline.</summary>
    public string OfflineDatabasePath { get; set; } = "forgelink-offline.db";

    public int RequestTimeoutSeconds { get; set; } = 10;
}

/// <summary>Problem-details error returned by the ForgeLink API.</summary>
public sealed class ForgeLinkApiException(int statusCode, string title, string? detail, string? code)
    : Exception(string.IsNullOrEmpty(detail) ? title : detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Code { get; } = code;

    /// <summary>5xx, 408 and 429 are worth retrying later; 4xx means the request itself is wrong.</summary>
    public bool IsTransient => StatusCode is >= 500 or 408 or 429;
}

/// <summary>Plain HTTP implementation of <see cref="IForgeLinkApi"/>.</summary>
public sealed class ForgeLinkHttpClient(HttpClient http) : IForgeLinkApi
{
    public const string IdempotencyHeader = "Idempotency-Key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<MachineStatusDto>> GetMachinesAsync(string? line = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MachineStatusDto>>($"api/machines{Query(("line", line))}", ct);

    public Task<LineKpiDto> GetLineKpisAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        GetAsync<LineKpiDto>($"api/kpis/lines/{Esc(line)}{Query(("fromUtc", fromUtc.ToString("O")), ("toUtc", toUtc.ToString("O")))}", ct);

    public Task<IReadOnlyList<DailyOeeDto>> GetDailyOeeAsync(string? line, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DailyOeeDto>>($"api/reports/oee/daily{Query(("line", line), ("from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), ("to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))}", ct);

    public Task<IntegrationHealthDto> GetIntegrationHealthAsync(CancellationToken ct = default) =>
        GetAsync<IntegrationHealthDto>("api/integration/health", ct);

    public Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(string? line = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkOrderDto>>($"api/work-orders{Query(("line", line))}", ct);

    public async Task<WorkOrderDto?> GetWorkOrderAsync(string number, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/work-orders/{Esc(number)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<WorkOrderDto>(response, ct);
    }

    public Task<WorkOrderDto> StartWorkOrderAsync(string number, StartWorkOrderRequest request, CancellationToken ct = default) =>
        PostAsync<WorkOrderDto>($"api/work-orders/{Esc(number)}/start", request, null, ct);

    public Task<WorkOrderDto> StopWorkOrderAsync(string number, StopWorkOrderRequest request, CancellationToken ct = default) =>
        PostAsync<WorkOrderDto>($"api/work-orders/{Esc(number)}/stop", request, null, ct);

    public Task<ProductionReportResult> ReportProductionAsync(string number, ReportProductionRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<ProductionReportResult>($"api/work-orders/{Esc(number)}/production", request, idempotencyKey ?? NewKey(), ct);

    public Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryAsync(string? sku = null, string? location = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<InventoryBalanceDto>>($"api/inventory{Query(("sku", sku), ("location", location))}", ct);

    public Task<IReadOnlyList<InventoryTransactionDto>> GetRecentTransactionsAsync(int take = 50, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<InventoryTransactionDto>>($"api/inventory/transactions?take={take}", ct);

    public Task<InventoryTransactionDto> MoveInventoryAsync(MoveInventoryRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<InventoryTransactionDto>("api/inventory/moves", request, idempotencyKey ?? NewKey(), ct);

    public Task<CycleCountDto> SubmitCycleCountAsync(SubmitCycleCountRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<CycleCountDto>("api/inventory/cycle-counts", request, idempotencyKey ?? NewKey(), ct);

    public Task<IReadOnlyList<CycleCountDto>> GetPendingCycleCountsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CycleCountDto>>("api/inventory/cycle-counts/pending", ct);

    public Task<CycleCountDto> ApproveCycleCountAsync(Guid id, bool approve, CancellationToken ct = default) =>
        PostAsync<CycleCountDto>($"api/inventory/cycle-counts/{id}/review?approve={(approve ? "true" : "false")}", new { }, null, ct);

    public Task<QualityCheckDto> RecordQualityCheckAsync(RecordQualityCheckRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<QualityCheckDto>("api/quality/checks", request, idempotencyKey ?? NewKey(), ct);

    public Task<LotTraceDto> TraceLotAsync(string lotNumber, CancellationToken ct = default) =>
        GetAsync<LotTraceDto>($"api/lots/{Esc(lotNumber)}/trace", ct);

    /// <summary>Cheap reachability probe for connectivity indicators.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("health/live", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string url, object body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        if (idempotencyKey is not null) request.Headers.Add(IdempotencyHeader, idempotencyKey);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;

        string title = response.ReasonPhrase ?? "Request failed", code = "";
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
            if (root.TryGetProperty("detail", out var d)) detail = d.GetString();
            if (root.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
        }
        catch (JsonException)
        {
            // non-JSON error body (proxy, load balancer): fall back to status text
        }
        throw new ForgeLinkApiException((int)response.StatusCode, title, detail, code);
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string Query(params (string Name, string? Value)[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToList();
        return present.Count == 0 ? "" : "?" + string.Join("&", present);
    }
}
