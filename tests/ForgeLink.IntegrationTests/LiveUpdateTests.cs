using System.Collections.Concurrent;
using System.Net.Http.Json;
using ForgeLink.Client;
using ForgeLink.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeLink.IntegrationTests;

public sealed class LiveUpdateTests(ForgeLinkFactory factory) : IClassFixture<ForgeLinkFactory>
{
    private static async Task<T> WithinAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == tcs.Task, $"timed out waiting for {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task Device_client_receives_hub_events_when_a_machine_signal_is_ingested()
    {
        var http = factory.CreateClient(); // boots the server
        var options = Options.Create(new ForgeLinkClientOptions { BaseUrl = factory.Server.BaseAddress.ToString(), ApiKey = "dev-handheld-01" });
        await using var events = new HubShopFloorEvents(options, NullLogger<HubShopFloorEvents>.Instance, o =>
        {
            // TestServer has no real sockets: route the SignalR transport through its in-memory handler.
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        });

        var received = new TaskCompletionSource<ShopFloorChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Changed += c => { if (c is { Kind: ShopFloorChangeKind.Machine, Key: "L2-CNC-01" }) received.TrySetResult(c); };
        await events.WaitUntilConnectedAsync(TimeSpan.FromSeconds(10));
        Assert.True(events.IsConnected);

        var signal = new[] { new MachineSignalDto("L2-CNC-01", "count", GoodDelta: 3) };
        (await http.PostAsJsonAsync("api/machines/signals", signal)).EnsureSuccessStatusCode();

        Assert.Equal("L2-CNC-01", (await WithinAsync(received, "machineChanged")).Key);
    }

    [Fact]
    public async Task Dashboard_circuits_get_the_same_change_in_process()
    {
        var http = factory.CreateClient();
        var local = factory.Services.GetRequiredService<IShopFloorEvents>();
        Assert.True(local.IsConnected);

        var seen = new ConcurrentBag<ShopFloorChange>();
        var got = new TaskCompletionSource<ShopFloorChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ShopFloorChange c)
        {
            seen.Add(c);
            if (c.Kind == ShopFloorChangeKind.Machine && c.Key == "L1-WASH-01") got.TrySetResult(c);
        }

        local.Changed += Handler;
        try
        {
            var signal = new[] { new MachineSignalDto("L1-WASH-01", "count", GoodDelta: 2, RejectDelta: 1) };
            (await http.PostAsJsonAsync("api/machines/signals", signal)).EnsureSuccessStatusCode();
            await WithinAsync(got, "in-process machine change");
        }
        finally
        {
            local.Changed -= Handler;
        }
    }
}
