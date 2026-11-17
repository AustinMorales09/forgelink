using ForgeLink.Application.Equipment;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Inventory;
using ForgeLink.Application.Production;
using ForgeLink.Application.Quality;
using ForgeLink.Application.Reporting;
using ForgeLink.Domain;
using ForgeLink.Contracts;
using ForgeLink.Infrastructure.Erp;
using ForgeLink.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ForgeLink.Web.Api;

[ApiController]
[Route("api/work-orders")]
[Authorize(Policy = Policies.Operator)]
public sealed class WorkOrdersController(WorkOrderService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<WorkOrderDto>> List([FromQuery] string? line, [FromQuery] bool includeClosed, CancellationToken ct) =>
        service.ListAsync(line, includeClosed, ct);

    [HttpGet("{number}")]
    public async Task<ActionResult<WorkOrderDto>> Get(string number, CancellationToken ct) =>
        await service.GetAsync(number, ct) is { } wo ? wo : NotFound();

    [HttpPost("{number}/start")]
    public async Task<ActionResult<WorkOrderDto>> Start(string number, StartWorkOrderRequest request, CancellationToken ct) =>
        Ok(await service.StartAsync(number, request, ct));

    [HttpPost("{number}/stop")]
    public async Task<ActionResult<WorkOrderDto>> Stop(string number, StopWorkOrderRequest request, CancellationToken ct) =>
        Ok(await service.StopAsync(number, request, ct));

    /// <summary>Report good/scrap quantity. Requires Idempotency-Key: stations replay this after network outages.</summary>
    [HttpPost("{number}/production")]
    [Idempotent]
    public async Task<ActionResult<ProductionReportResult>> ReportProduction(string number, ReportProductionRequest request, CancellationToken ct) =>
        Ok(await service.ReportProductionAsync(number, request, ct));
}

[ApiController]
[Route("api/inventory")]
[Authorize(Policy = Policies.Operator)]
public sealed class InventoryController(InventoryService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<InventoryBalanceDto>> Query([FromQuery] string? sku, [FromQuery] string? location, CancellationToken ct) =>
        service.QueryAsync(sku, location, ct);

    /// <summary>Most recent inventory transactions (simple list kept for existing station clients; prefer <c>/ledger</c>).</summary>
    [HttpGet("transactions")]
    public Task<IReadOnlyList<InventoryTransactionDto>> Transactions([FromQuery] int take = 50, CancellationToken ct = default) =>
        service.RecentTransactionsAsync(take, ct);

    /// <summary>
    /// The inventory ledger, newest first, with cursor pagination. Pass <c>nextCursor</c> from a response as
    /// <c>cursor</c> to fetch the next page; <c>nextCursor</c> is null on the last page. <c>limit</c> is 1–500 (default 50).
    /// </summary>
    [HttpGet("ledger")]
    public Task<PageDto<InventoryTransactionDto>> Ledger([FromQuery] string? sku, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        service.LedgerAsync(sku, limit, cursor, ct);

    [HttpPost("moves")]
    [Idempotent]
    public async Task<ActionResult<InventoryTransactionDto>> Move(MoveInventoryRequest request, CancellationToken ct) =>
        Ok(await service.MoveAsync(request, ct));

    [HttpPost("adjustments")]
    [Idempotent]
    [Authorize(Policy = Policies.Supervisor)]
    public async Task<ActionResult<InventoryTransactionDto>> Adjust(AdjustInventoryRequest request, CancellationToken ct) =>
        Ok(await service.AdjustAsync(request, ct));

    [HttpPost("cycle-counts")]
    [Idempotent]
    public async Task<ActionResult<CycleCountDto>> SubmitCycleCount(SubmitCycleCountRequest request, CancellationToken ct) =>
        Ok(await service.SubmitCycleCountAsync(request, ct));

    [HttpGet("cycle-counts/pending")]
    public Task<IReadOnlyList<CycleCountDto>> PendingCycleCounts(CancellationToken ct) => service.PendingCycleCountsAsync(ct);

    [HttpPost("cycle-counts/{id:guid}/review")]
    [Authorize(Policy = Policies.Supervisor)]
    public async Task<ActionResult<CycleCountDto>> Review(Guid id, [FromQuery] bool approve, CancellationToken ct) =>
        Ok(await service.ReviewCycleCountAsync(id, approve, ct));
}

[ApiController]
[Route("api")]
[Authorize(Policy = Policies.Operator)]
public sealed class QualityController(QualityService quality, LotTraceService trace) : ControllerBase
{
    [HttpPost("quality/checks")]
    [Idempotent]
    public async Task<ActionResult<QualityCheckDto>> Record(RecordQualityCheckRequest request, CancellationToken ct) =>
        Ok(await quality.RecordAsync(request, ct));

    [HttpGet("lots/{lotNumber}/trace")]
    public Task<LotTraceDto> Trace(string lotNumber, CancellationToken ct) => trace.TraceAsync(lotNumber, ct);
}

[ApiController]
[Route("api/machines")]
public sealed class MachinesController(TelemetryService telemetry) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.Operator)]
    public Task<IReadOnlyList<MachineStatusDto>> List([FromQuery] string? line, CancellationToken ct) => telemetry.GetMachinesAsync(line, ct);

    /// <summary>HTTP ingestion for OT gateways that cannot speak MQTT (e.g. a PLC with only an HTTP client). Batched.</summary>
    [HttpPost("signals")]
    [Authorize(Policy = Policies.Integration)]
    public async Task<IActionResult> Ingest(IReadOnlyList<MachineSignalDto> signals, CancellationToken ct)
    {
        var accepted = 0;
        foreach (var signal in signals.Take(1000))
            if (await telemetry.HandleAsync(signal, ct)) accepted++;
        return Accepted(new { accepted, ignored = signals.Count - accepted });
    }
}

[ApiController]
[Route("api")]
[Authorize(Policy = Policies.Operator)]
public sealed class KpiController(KpiService kpis, IntegrationAdminService integration) : ControllerBase
{
    [HttpGet("kpis/lines/{line}")]
    public Task<LineKpiDto> Line(string line, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken ct)
    {
        var to = toUtc ?? DateTime.UtcNow;
        return kpis.GetLineKpisAsync(line, fromUtc ?? to.AddHours(-8), to, ct);
    }

    [HttpGet("integration/health")]
    public Task<IntegrationHealthDto> Health(CancellationToken ct) => integration.GetHealthAsync(ct);
}

/// <summary>
/// Event-driven inbound path from the ERP (e.g. "work order released"). Authenticated by HMAC signature rather than
/// a user, so it is anonymous at the auth layer and verified here. The body is only a hint: we re-fetch the record.
/// </summary>
[ApiController]
[Route("api/erp/webhooks")]
[AllowAnonymous]
public sealed class ErpWebhookController(ErpSyncService sync, IOptions<ErpClientOptions> options, TimeProvider clock, ILogger<ErpWebhookController> logger)
    : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        if (!WebhookSignature.Verify(options.Value.WebhookSecret, Request.Headers[WebhookSignature.TimestampHeader],
                Request.Headers[WebhookSignature.SignatureHeader], body, clock.GetUtcNow(), TimeSpan.FromMinutes(5)))
        {
            logger.LogWarning("Rejected ERP webhook with invalid signature from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        var evt = System.Text.Json.JsonSerializer.Deserialize<ErpWebhookEvent>(body, System.Text.Json.JsonSerializerOptions.Web);
        if (evt is null) return BadRequest();

        switch (evt.EventType)
        {
            case "workorder.released" or "workorder.changed" or "workorder.cancelled" when evt.WorkOrderNumber is not null:
                await sync.SyncWorkOrderAsync(evt.WorkOrderNumber, ct);
                break;
            case "item.changed":
                await sync.SyncMasterDataAsync(ct);
                break;
            default:
                logger.LogInformation("Ignoring ERP event {EventType}", evt.EventType);
                break;
        }
        return Accepted();
    }
}

/// <summary>Daily OEE reporting, fed by the rollup pipeline (<see cref="KpiRollupService"/>).</summary>
[ApiController]
[Route("api/reports/oee")]
[Authorize(Policy = Policies.Operator)]
public sealed class ReportsController(KpiRollupService rollup) : ControllerBase
{
    /// <summary>
    /// One row per machine per plant-local day. <c>from</c>/<c>to</c> are inclusive dates (default: the last 14 days).
    /// <c>format=csv</c> returns a spreadsheet-ready download; the default is JSON. Today's rows have <c>isPartial=true</c>.
    /// </summary>
    [HttpGet("daily")]
    [ProducesResponseType<IReadOnlyList<DailyOeeDto>>(StatusCodes.Status200OK, "application/json", "text/csv")]
    public async Task<IActionResult> Daily([FromQuery] string? line, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? format, CancellationToken ct)
    {
        var today = rollup.LocalDay(DateTime.UtcNow);
        var end = to ?? today;
        var start = from ?? end.AddDays(-13);
        var rows = await rollup.GetDailyAsync(line, start, end, ct);
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)) return Ok(rows);

        var name = $"oee-daily-{(string.IsNullOrWhiteSpace(line) ? "all" : line.Trim().ToUpperInvariant())}-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.csv";
        return File(System.Text.Encoding.UTF8.GetBytes(Csv.DailyOee(rows)), "text/csv; charset=utf-8", name);
    }

    /// <summary>Runs the rollup now instead of waiting for the next scheduled run (supervisors only).</summary>
    [HttpPost("rollup")]
    [Authorize(Policy = Policies.Supervisor)]
    public Task<KpiRollupResultDto> Rollup(CancellationToken ct) => rollup.RunAsync(ct);
}
