using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Equipment;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Inventory;
using ForgeLink.Application.Production;
using ForgeLink.Application.Quality;
using ForgeLink.Application.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeLink.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddForgeLinkApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForgeLinkOptions>(configuration.GetSection(ForgeLinkOptions.Section));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));
        services.Configure<ReportingOptions>(configuration.GetSection(ReportingOptions.Section));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IShopFloorNotifier, NullShopFloorNotifier>();
        services.TryAddScoped<IDowntimeParetoQuery, EfDowntimeParetoQuery>();

        services.AddScoped<WorkOrderService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<QualityService>();
        services.AddScoped<LotTraceService>();
        services.AddScoped<TelemetryService>();
        services.AddScoped<KpiService>();
        services.AddScoped<KpiRollupService>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<ErpSyncService>();
        services.AddScoped<IntegrationAdminService>();
        return services;
    }
}
