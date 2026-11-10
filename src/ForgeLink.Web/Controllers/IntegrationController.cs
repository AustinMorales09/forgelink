using ForgeLink.Application.Integration;
using ForgeLink.Contracts;
using ForgeLink.Domain.Integration;
using ForgeLink.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ForgeLink.Web.Controllers;

public sealed record IntegrationIndexViewModel(
    IntegrationHealthDto Health, IReadOnlyList<OutboxMessageDto> Messages, OutboxStatus? Filter, string? Flash);

/// <summary>
/// ASP.NET Core MVC operations console for the integration team: outbox backlog, dead letters,
/// retry/cancel with audit, on-demand ERP sync, and the audit trail. Server-rendered so it works
/// from any locked-down plant browser with no client framework.
/// </summary>
[Authorize(Policy = Policies.Supervisor)]
public sealed class IntegrationController(IntegrationAdminService admin, ErpSyncService sync) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(OutboxStatus? status, CancellationToken ct)
    {
        var health = await admin.GetHealthAsync(ct);
        var messages = await admin.ListAsync(status, 200, ct);
        return View(new IntegrationIndexViewModel(health, messages, status, TempData["Flash"] as string));
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken ct) =>
        await admin.GetAsync(id, ct) is { } message ? View(message) : NotFound();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(long id, CancellationToken ct)
    {
        await admin.RetryAsync(id, ct);
        TempData["Flash"] = $"Message {id} re-queued.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(long id, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Flash"] = "A reason is required to cancel a message (it is written to the audit trail).";
            return RedirectToAction(nameof(Details), new { id });
        }
        await admin.CancelAsync(id, reason, ct);
        TempData["Flash"] = $"Message {id} cancelled.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        var result = await sync.SyncMasterDataAsync(ct);
        TempData["Flash"] = $"ERP sync: {result.Items} items, {result.WorkOrders} work orders"
            + (result.Conflicts.Count > 0 ? $"; {result.Conflicts.Count} conflict(s): {string.Join(" ", result.Conflicts)}" : ".");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Audit(string? entityType, CancellationToken ct)
    {
        ViewBag.EntityType = entityType;
        return View(await admin.AuditTrailAsync(entityType, 200, ct));
    }
}
