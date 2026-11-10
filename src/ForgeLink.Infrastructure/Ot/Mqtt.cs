using System.Text;
using System.Text.Json;
using ForgeLink.Application.Equipment;
using ForgeLink.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace ForgeLink.Infrastructure.Ot;

public sealed class MqttOptions
{
    public const string Section = "Mqtt";

    public bool Enabled { get; set; } = true;

    /// <summary>Run an in-process broker (local dev / single-box installs). Production points at Mosquitto, HiveMQ or Azure Event Grid MQTT.</summary>
    public bool EmbeddedBroker { get; set; }

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Topic scheme: plant/{line}/{machine}/{state|count|heartbeat}.</summary>
    public string TopicFilter { get; set; } = "plant/+/+/+";
}

/// <summary>
/// Subscribes to machine telemetry and feeds <see cref="TelemetryService"/>.
/// Persistent session (clean session = false) + QoS 1 means messages published while ForgeLink restarts are
/// delivered when it reconnects; the reconnect loop backs off so a broker outage does not spin the CPU.
/// </summary>
public sealed class MqttTelemetryListener(IServiceScopeFactory scopes, IOptions<MqttOptions> options, ILogger<MqttTelemetryListener> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var signal = Parse(e.ApplicationMessage.Topic, e.ApplicationMessage.PayloadSegment);
                if (signal is null) return;
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TelemetryService>().HandleAsync(signal, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Poison message: log and acknowledge so it does not block the stream.
                logger.LogWarning(ex, "Failed to process MQTT message on {Topic}", e.ApplicationMessage.Topic);
            }
        };

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(opts.Host, opts.Port)
            .WithClientId($"forgelink-ingest-{Environment.MachineName}")
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));
        if (!string.IsNullOrEmpty(opts.Username)) clientOptions.WithCredentials(opts.Username, opts.Password);
        var built = clientOptions.Build();

        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(built, stoppingToken);
                    var subscribe = factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(opts.TopicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();
                    await client.SubscribeAsync(subscribe, stoppingToken);
                    logger.LogInformation("MQTT connected to {Host}:{Port}, subscribed to {Topic}", opts.Host, opts.Port, opts.TopicFilter);
                    delay = TimeSpan.FromSeconds(1);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("MQTT connection to {Host}:{Port} failed ({Error}); retrying in {Delay}", opts.Host, opts.Port, ex.Message, delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }

        if (client.IsConnected)
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
    }

    /// <summary>Topic plant/{line}/{machine}/{type}; JSON payload {"state","reason","good","reject","ts"}.</summary>
    public static MachineSignalDto? Parse(string topic, ArraySegment<byte> payload)
    {
        var parts = topic.Split('/');
        if (parts.Length != 4 || parts[0] != "plant") return null;
        var machine = parts[2];
        var type = parts[3].ToLowerInvariant();

        TelemetryPayload body = new();
        if (payload.Count > 0)
            body = JsonSerializer.Deserialize<TelemetryPayload>(Encoding.UTF8.GetString(payload), JsonOptions) ?? new();

        return type switch
        {
            MachineSignalTypes.State => new MachineSignalDto(machine, type, body.State, body.Reason, TimestampUtc: body.Ts),
            MachineSignalTypes.Count => new MachineSignalDto(machine, type, GoodDelta: body.Good, RejectDelta: body.Reject, TimestampUtc: body.Ts),
            MachineSignalTypes.Heartbeat => new MachineSignalDto(machine, type, TimestampUtc: body.Ts),
            _ => null,
        };
    }

    private sealed class TelemetryPayload
    {
        public string? State { get; set; }
        public string? Reason { get; set; }
        public int? Good { get; set; }
        public int? Reject { get; set; }
        public DateTime? Ts { get; set; }
    }
}

/// <summary>In-process MQTT broker so the whole system runs with one command on a laptop or an edge PC.</summary>
public sealed class EmbeddedMqttBroker(IOptions<MqttOptions> options, ILogger<EmbeddedMqttBroker> logger) : IHostedService, IDisposable
{
    private MqttServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(options.Value.Port)
            .WithPersistentSessions()
            .Build();
        _server = new MqttFactory().CreateMqttServer(serverOptions);
        await _server.StartAsync();
        logger.LogInformation("Embedded MQTT broker listening on port {Port}", options.Value.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync();
    }

    public void Dispose() => _server?.Dispose();
}
