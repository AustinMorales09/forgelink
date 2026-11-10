using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

// Content root = output folder, so appsettings.json is found whatever directory it is launched from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddHostedService<PlantSimulator>();
await builder.Build().RunAsync();

public sealed class SimulatorOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;

    /// <summary>Downtime compression for demos: 5 = a 10-minute stop lasts 2 real minutes. Parts are always produced at real cycle speed so Performance stays honest.</summary>
    public double DowntimeCompression { get; set; } = 5;

    public double RejectRate { get; set; } = 0.03;
    public double BreakdownChancePerCycle { get; set; } = 0.04;
    public List<SimMachine> Machines { get; set; } = [];
}

public sealed class SimMachine
{
    public string Code { get; set; } = "";
    public string Line { get; set; } = "";
    public double CycleSeconds { get; set; } = 45;
}

/// <summary>
/// Each machine runs a loop: cycle → publish count → occasionally break down with a reason code → recover.
/// Heartbeats go out every 5 s. Publishing uses QoS 1 so the broker holds messages if ForgeLink is restarting.
/// </summary>
public sealed class PlantSimulator(Microsoft.Extensions.Options.IOptions<SimulatorOptions> options, ILogger<PlantSimulator> logger) : BackgroundService
{
    private static readonly string[] DownReasons = ["TOOL_CHANGE", "MATERIAL_SHORTAGE", "CHIP_JAM", "QUALITY_ADJUST", "PLANNED_PM", "OPERATOR_BREAK"];
    private static readonly double[] DownWeights = [0.30, 0.20, 0.20, 0.12, 0.08, 0.10];
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _lastState = new();
    private IMqttClient? _client;
    private MqttClientOptions? _clientOptions;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        _client = new MqttFactory().CreateMqttClient();
        var clientOptions = _clientOptions = new MqttClientOptionsBuilder().WithTcpServer(opts.Host, opts.Port).WithClientId("forgelink-ot-sim").Build();

        while (!_client.IsConnected && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAsync(clientOptions, stoppingToken);
                logger.LogInformation("Connected to MQTT {Host}:{Port}; simulating {Count} machines (downtime compressed {Compression}x)", opts.Host, opts.Port, opts.Machines.Count, opts.DowntimeCompression);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("MQTT broker not reachable ({Error}); retrying in 3 s. Is ForgeLink.Web (embedded broker) running?", ex.Message);
                await Task.Delay(3000, stoppingToken);
            }
        }

        await Task.WhenAll(opts.Machines.Select(m => RunMachineAsync(m, opts, stoppingToken)).Append(HeartbeatAsync(opts, stoppingToken)));
    }

    private async Task RunMachineAsync(SimMachine m, SimulatorOptions opts, CancellationToken ct)
    {
        var rng = new Random(m.Code.GetHashCode());
        await PublishAsync(m, "state", new { state = "Running" }, ct);

        while (!ct.IsCancellationRequested)
        {
            var cycle = m.CycleSeconds * (1 + (rng.NextDouble() * 0.25)); // real machines run slower than ideal
            await Task.Delay(TimeSpan.FromSeconds(cycle), ct);

            var reject = rng.NextDouble() < opts.RejectRate;
            await PublishAsync(m, "count", new { good = reject ? 0 : 1, reject = reject ? 1 : 0 }, ct);

            if (rng.NextDouble() < opts.BreakdownChancePerCycle)
            {
                var reason = Pick(rng);
                logger.LogInformation("{Machine} DOWN: {Reason}", m.Code, reason);
                await PublishAsync(m, "state", new { state = "Down", reason }, ct);
                await Delay(rng.Next(120, 900), opts, ct);
                await PublishAsync(m, "state", new { state = "Running" }, ct);
                logger.LogInformation("{Machine} running again", m.Code);
            }
        }
    }

    private async Task HeartbeatAsync(SimulatorOptions opts, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            await EnsureConnectedAsync(opts, ct);
            // Every 30 s also re-send the current state (like a Sparkplug rebirth), so a consumer that subscribed late or
            // restarted recovers the real state instead of staying Offline. ForgeLink ignores unchanged states.
            var resendState = ++tick % 6 == 0;
            foreach (var m in opts.Machines)
            {
                await PublishAsync(m, "heartbeat", new { }, ct);
                if (resendState && _lastState.TryGetValue(m.Code, out var state)) await PublishAsync(m, "state", state, ct);
            }
        }
    }

    /// <summary>
    /// A broker restart (e.g. ForgeLink.Web redeploy with the embedded broker) drops the connection. Reconnect and
    /// re-announce each machine's current state, as a real edge gateway does after a link outage.
    /// </summary>
    private async Task EnsureConnectedAsync(SimulatorOptions opts, CancellationToken ct)
    {
        if (_client is null || _clientOptions is null || _client.IsConnected) return;
        try
        {
            await _client.ConnectAsync(_clientOptions, ct);
            logger.LogInformation("Reconnected to MQTT {Host}:{Port}", opts.Host, opts.Port);
            foreach (var m in opts.Machines)
                if (_lastState.TryGetValue(m.Code, out var state)) await PublishAsync(m, "state", state, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("MQTT reconnect failed ({Error}); will retry", ex.Message);
        }
    }

    private async Task PublishAsync(SimMachine m, string type, object payload, CancellationToken ct)
    {
        if (type == "state") _lastState[m.Code] = payload;
        var json = JsonSerializer.Serialize(payload);
        // Add a device timestamp, as a real edge gateway would.
        json = json.Length > 2 ? json[..^1] + $",\"ts\":\"{DateTime.UtcNow:O}\"}}" : $"{{\"ts\":\"{DateTime.UtcNow:O}\"}}";
        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"plant/{m.Line}/{m.Code}/{type}")
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _publishLock.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true }) await _client.PublishAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Publish failed: {Error}", ex.Message);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private static Task Delay(double simulatedSeconds, SimulatorOptions opts, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(simulatedSeconds / Math.Max(0.1, opts.DowntimeCompression)), ct);

    private static string Pick(Random rng)
    {
        var roll = rng.NextDouble() * DownWeights.Sum();
        for (var i = 0; i < DownReasons.Length; i++)
        {
            roll -= DownWeights[i];
            if (roll <= 0) return DownReasons[i];
        }
        return DownReasons[^1];
    }
}
