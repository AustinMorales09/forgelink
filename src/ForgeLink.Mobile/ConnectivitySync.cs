using ForgeLink.Client.Offline;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Mobile;

/// <summary>
/// Replays the offline queue when the device regains network (walking back into Wi-Fi coverage)
/// and every 30 s as a fallback, since OS connectivity events can lie about captive or flaky APs.
/// </summary>
public sealed class ConnectivitySync(StoreAndForwardForgeLinkApi api, ILogger<ConnectivitySync> logger) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        _ = LoopAsync(_cts.Token);
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess is NetworkAccess.Internet or NetworkAccess.Local)
            await FlushAsync();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await FlushAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushAsync()
    {
        try
        {
            await api.FlushAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Offline flush failed");
        }
    }

    public void Dispose()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
