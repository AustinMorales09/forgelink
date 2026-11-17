namespace ForgeLink.Contracts;

public enum ShopFloorChangeKind
{
    Machine,
    WorkOrder,
    Inventory,
}

/// <summary>"Something changed" signal. Deliberately tiny: receivers re-fetch details over the API.</summary>
public sealed record ShopFloorChange(ShopFloorChangeKind Kind, string Key);

/// <summary>
/// Push channel for live views. The web host raises these in-process; devices receive them over SignalR
/// (<c>/hubs/shopfloor</c>). Views keep a slow poll as a safety net, so a dropped connection only makes them slower, never wrong.
/// </summary>
public interface IShopFloorEvents
{
    event Action<ShopFloorChange>? Changed;

    /// <summary>True while change notifications are actually arriving (in-process, or hub connected).</summary>
    bool IsConnected { get; }
}

/// <summary>Hub method names shared by server and clients.</summary>
public static class ShopFloorHubMethods
{
    public const string Path = "hubs/shopfloor";
    public const string MachineChanged = "machineChanged";
    public const string WorkOrderChanged = "workOrderChanged";
    public const string InventoryChanged = "inventoryChanged";
}
