using ForgeLink.Application.Abstractions;
using ForgeLink.Infrastructure.Erp;
using ForgeLink.Infrastructure.Ot;
using ForgeLink.Infrastructure.Persistence;
using ForgeLink.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeLink.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddForgeLinkInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("ForgeLink") ?? "Data Source=forgelink.db";
        var useSqlServer = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);

        services.AddDbContext<ForgeLinkDbContext>(options =>
        {
            if (useSqlServer)
                options.UseSqlServer(connectionString, sql => sql.CommandTimeout(30));
            else
                options.UseSqlite(connectionString);
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<ForgeLinkDbContext>());

        if (useSqlServer)
            services.AddScoped<IDowntimeParetoQuery, SqlServerDowntimeParetoQuery>();

        // ERP adapter with the standard resilience pipeline: retry (exp. backoff + jitter), per-attempt timeout,
        // circuit breaker and total timeout. Retrying POSTs is safe because of Idempotency-Key.
        services.Configure<ErpClientOptions>(configuration.GetSection(ErpClientOptions.Section));
        services.AddHttpClient<IErpClient, HttpErpClient>((sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<ErpClientOptions>>().Value;
                client.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
                if (!string.IsNullOrEmpty(o.ApiKey)) client.DefaultRequestHeaders.Add("X-Api-Key", o.ApiKey);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            });

        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.Section));
        var mqtt = configuration.GetSection(MqttOptions.Section).Get<MqttOptions>() ?? new MqttOptions();

        if (configuration.GetValue("Workers:Enabled", true))
        {
            if (mqtt.EmbeddedBroker) services.AddHostedService<EmbeddedMqttBroker>();
            if (mqtt.Enabled) services.AddHostedService<MqttTelemetryListener>();
            services.AddHostedService<OutboxDispatcherWorker>();
            services.AddHostedService<MachineWatchdogWorker>();
            services.AddHostedService<KpiRollupWorker>();
            if (configuration.GetValue("Erp:SyncEnabled", true)) services.AddHostedService<ErpSyncWorker>();
        }

        return services;
    }
}
