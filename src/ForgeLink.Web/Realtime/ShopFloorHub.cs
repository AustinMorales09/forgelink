using ForgeLink.Application.Abstractions;
using ForgeLink.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ForgeLink.Web.Realtime;

/// <summary>
/// Push channel for andon boards, HMIs, handhelds and line-side displays. Clients receive lightweight
/// "something changed" events, then fetch details over REST (keeps the hub contract tiny and versionable).
/// Clients that only care about one line can join its group.
/// </summary>
public sealed class ShopFloorHub : Hub
{
    public Task JoinLine(string line) => Groups.AddToGroupAsync(Context.ConnectionId, Group(line));

    public Task LeaveLine(string line) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(line));

    internal static string Group(string line) => $"line:{line.ToUpperInvariant()}";
}

/// <summary>In-process fan-out for Blazor Server circuits, which run inside the web host and need no socket of their own.</summary>
public sealed class InProcessShopFloorEvents(ILogger<InProcessShopFloorEvents> logger) : IShopFloorEvents
{
    public event Action<ShopFloorChange>? Changed;

    public bool IsConnected => true;

    public void Publish(ShopFloorChange change)
    {
        // One misbehaving subscriber (a circuit being torn down) must not stop the others.
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<ShopFloorChange>>() ?? [])
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Live-update subscriber failed for {Kind} {Key}", change.Kind, change.Key);
            }
        }
    }
}

/// <summary>Called by application services after a commit: notifies in-process dashboards and SignalR clients.</summary>
public sealed class SignalRShopFloorNotifier(IHubContext<ShopFloorHub> hub, InProcessShopFloorEvents local, ILogger<SignalRShopFloorNotifier> logger)
    : IShopFloorNotifier
{
    public Task MachineChangedAsync(string machineCode, CancellationToken ct = default) =>
        Send(ShopFloorHubMethods.MachineChanged, new ShopFloorChange(ShopFloorChangeKind.Machine, machineCode), ct);

    public Task WorkOrderChangedAsync(string workOrderNumber, CancellationToken ct = default) =>
        Send(ShopFloorHubMethods.WorkOrderChanged, new ShopFloorChange(ShopFloorChangeKind.WorkOrder, workOrderNumber), ct);

    public Task InventoryChangedAsync(string sku, CancellationToken ct = default) =>
        Send(ShopFloorHubMethods.InventoryChanged, new ShopFloorChange(ShopFloorChangeKind.Inventory, sku), ct);

    private async Task Send(string method, ShopFloorChange change, CancellationToken ct)
    {
        local.Publish(change);
        try
        {
            await hub.Clients.All.SendAsync(method, change.Key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Notifications are best-effort: never fail a committed shop-floor transaction because a display is offline.
            logger.LogDebug(ex, "SignalR notification {Method} failed", method);
        }
    }
}
