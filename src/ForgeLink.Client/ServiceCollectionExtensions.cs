using ForgeLink.Client.Offline;
using ForgeLink.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeLink.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IForgeLinkApi"/> as an offline-capable client: typed HttpClient with short retries
    /// (so a flaky AP does not immediately push work offline), wrapped in the store-and-forward decorator.
    /// </summary>
    public static IServiceCollection AddForgeLinkClient(this IServiceCollection services, Action<ForgeLinkClientOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<ForgeLinkHttpClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<ForgeLinkClientOptions>>().Value;
                http.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
                if (!string.IsNullOrEmpty(o.ApiKey)) http.DefaultRequestHeaders.Add("X-Api-Key", o.ApiKey);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.FromMilliseconds(300);
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(4);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(12);
            });

        services.AddSingleton(sp => new OfflineCommandQueue(sp.GetRequiredService<IOptions<ForgeLinkClientOptions>>().Value.OfflineDatabasePath));
        services.AddSingleton(sp => new StoreAndForwardForgeLinkApi(
            () => sp.GetRequiredService<ForgeLinkHttpClient>(),
            sp.GetRequiredService<OfflineCommandQueue>(),
            sp.GetRequiredService<ILogger<StoreAndForwardForgeLinkApi>>()));
        services.AddSingleton<IForgeLinkApi>(sp => sp.GetRequiredService<StoreAndForwardForgeLinkApi>());
        return services;
    }
}
