using ForgeLink.Client;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Same client as the WPF station: HTTP + resilience + offline SQLite queue.
        builder.Services.AddForgeLinkClient(o =>
        {
            // Android emulator reaches the host machine on 10.0.2.2. Real devices: set via MDM/app config.
            o.BaseUrl = Preferences.Default.Get("ForgeLinkBaseUrl",
                DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5100/" : "http://localhost:5100/");
            // Demo key. In production the device key comes from SecureStorage, provisioned by MDM.
            o.ApiKey = Preferences.Default.Get("ForgeLinkApiKey", "dev-handheld-01");
            o.OfflineDatabasePath = Path.Combine(FileSystem.AppDataDirectory, "handheld-offline.db");
        });
        builder.Services.AddSingleton<ConnectivitySync>();

        return builder.Build();
    }
}
