using ForgeLink.Application.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace ForgeLink.Web.Realtime;

/// <summary>
/// Push channel for andon boards, HMIs and line-side displays. Clients join a line group and receive
/// lightweight "something changed" events, then fetch details over REST (keeps the hub contract tiny and versionable).
/// </summary>
public sealed class ShopFloorHub : Hub
{
    public Task JoinLine(string line) => Groups.AddToGroupAsync(Context.ConnectionId, Group(line));

    public Task LeaveLine(string line) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(line));

    internal static string Group(string line) => $"line:{line.ToUpperInvariant()}";
}

public sealed class SignalRShopFloorNotifier(IHubContext<ShopFloorHub> hub, ILogger<SignalRShopFloorNotifier> logger) : IShopFloorNotifier
{
    public Task MachineChangedAsync(string machineCode, CancellationToken ct = default) => Send("machineChanged", machineCode, ct);

    public Task WorkOrderChangedAsync(string workOrderNumber, CancellationToken ct = default) => Send("workOrderChanged", workOrderNumber, ct);

    public Task InventoryChangedAsync(string sku, CancellationToken ct = default) => Send("inventoryChanged", sku, ct);

    private async Task Send(string method, string key, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync(method, key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Notifications are best-effort: never fail a committed shop-floor transaction because a display is offline.
            logger.LogDebug(ex, "SignalR notification {Method} failed", method);
        }
    }
}
