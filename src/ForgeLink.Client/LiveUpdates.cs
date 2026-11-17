using ForgeLink.Contracts;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Client;

/// <summary>
/// Receives shop-floor change events from the server's SignalR hub so device screens refresh as soon as something changes.
/// The connection starts on the first subscriber and keeps retrying (capped backoff) for as long as the app runs:
/// a handheld that walks out of Wi-Fi range reconnects by itself, and its views fall back to slow polling meanwhile.
/// </summary>
public sealed class HubShopFloorEvents : IShopFloorEvents, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<HubShopFloorEvents> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    public HubShopFloorEvents(IOptions<ForgeLinkClientOptions> options, ILogger<HubShopFloorEvents> logger, Action<HttpConnectionOptions>? configure = null)
    {
        _logger = logger;
        var o = options.Value;
        var url = new Uri(new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/"), ShopFloorHubMethods.Path);
        _connection = new HubConnectionBuilder()
            .WithUrl(url, http =>
            {
                if (!string.IsNullOrEmpty(o.ApiKey)) http.Headers["X-Api-Key"] = o.ApiKey;
                configure?.Invoke(http);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.On<string>(ShopFloorHubMethods.MachineChanged, key => Raise(ShopFloorChangeKind.Machine, key));
        _connection.On<string>(ShopFloorHubMethods.WorkOrderChanged, key => Raise(ShopFloorChangeKind.WorkOrder, key));
        _connection.On<string>(ShopFloorHubMethods.InventoryChanged, key => Raise(ShopFloorChangeKind.Inventory, key));
        _connection.Reconnecting += ex =>
        {
            _logger.LogInformation(ex, "Live updates disconnected; reconnecting");
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            // Anything could have changed while we were away: tell views to reload once.
            Raise(ShopFloorChangeKind.Machine, "*");
            return Task.CompletedTask;
        };
    }

    private event Action<ShopFloorChange>? ChangedCore;

    public event Action<ShopFloorChange>? Changed
    {
        add
        {
            ChangedCore += value;
            EnsureStarted();
        }
        remove => ChangedCore -= value;
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Completes once connected (used by tests and by apps that want to wait at startup).</summary>
    public async Task WaitUntilConnectedAsync(TimeSpan timeout)
    {
        EnsureStarted();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        cts.CancelAfter(timeout);
        while (!IsConnected) await Task.Delay(50, cts.Token);
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = ConnectLoopAsync();
    }

    private async Task ConnectLoopAsync()
    {
        var policy = new ForeverRetryPolicy();
        var attempt = 0;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(_stopping.Token);
                _logger.LogInformation("Live updates connected");
                return; // from here WithAutomaticReconnect takes over
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = policy.Delay(attempt++);
                _logger.LogDebug(ex, "Live updates unavailable; retrying in {Delay}", delay);
                try { await Task.Delay(delay, _stopping.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void Raise(ShopFloorChangeKind kind, string key)
    {
        var change = new ShopFloorChange(kind, key);
        foreach (var handler in ChangedCore?.GetInvocationList().Cast<Action<ShopFloorChange>>() ?? [])
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live-update subscriber failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _connection.DisposeAsync();
        _stopping.Dispose();
    }

    /// <summary>0.5 s, 1 s, 2 s … capped at 30 s, never giving up (a device may be out of range for a whole shift).</summary>
    internal sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(attempt, 10))));

        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay((int)Math.Min(int.MaxValue, retryContext.PreviousRetryCount));
    }
}
