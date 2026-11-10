using System.IO;
using System.Windows;
using System.Windows.Threading;
using ForgeLink.Client;
using ForgeLink.Client.Offline;
using ForgeLink.Desktop.ViewModels;
using ForgeLink.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Desktop;

/// <summary>
/// Composition root. The WPF app runs on the .NET Generic Host so it gets the same DI, configuration,
/// logging and background services (offline replay) as the server.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private CancellationTokenSource? _shutdown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeLink");
        Directory.CreateDirectory(dataFolder);

        var station = builder.Configuration.GetSection("Station").Get<StationOptions>() ?? new StationOptions();
        builder.Services.AddSingleton(station);

        builder.Services.AddForgeLinkClient(o =>
        {
            builder.Configuration.GetSection("ForgeLink").Bind(o);
            if (string.IsNullOrWhiteSpace(o.OfflineDatabasePath))
                o.OfflineDatabasePath = Path.Combine(dataFolder, $"station-{station.MachineCode}-offline.db");
        });
        builder.Services.AddHostedService<OfflineSyncWorker>();

        builder.Services.AddSingleton<IWeighScale>(_ => station.Scale.Mode.Equals("Serial", StringComparison.OrdinalIgnoreCase)
            ? new SerialWeighScale(station.Scale.PortName, station.Scale.BaudRate)
            : new SimulatedWeighScale());
        builder.Services.AddSingleton<ILabelPrinter>(_ => station.LabelPrinter.Mode.Equals("Tcp", StringComparison.OrdinalIgnoreCase)
            ? new TcpZplPrinter(station.LabelPrinter.Host, station.LabelPrinter.Port)
            : new FileLabelPrinter(string.IsNullOrWhiteSpace(station.LabelPrinter.Folder) ? Path.Combine(dataFolder, "labels") : station.LabelPrinter.Folder));

        builder.Services.AddSingleton<StationViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();

        _shutdown = new CancellationTokenSource();
        await _host.Services.GetRequiredService<StationViewModel>().RunAsync(_shutdown.Token);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _shutdown?.Cancel();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>A station must keep running through a shift: log, tell the operator, carry on.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nYour work is saved. Call a supervisor if this repeats.",
            "ForgeLink Station", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
