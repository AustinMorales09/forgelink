using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeLink.Client.Offline;
using ForgeLink.Contracts;
using ForgeLink.Contracts.Scanning;
using ForgeLink.Devices;
using Microsoft.Extensions.Logging;

namespace ForgeLink.Desktop.ViewModels;

public sealed record LogLine(DateTime At, string Text, string Level)
{
    public string Display => $"{At:HH:mm:ss}  {Text}";
}

/// <summary>
/// Operator station workflow: badge in → scan work order → start → (weigh / count) → report good &amp; scrap →
/// print lot label → pause / complete. Every server call goes through the store-and-forward client, so a
/// network outage never stops production reporting.
/// </summary>
public sealed partial class StationViewModel : ObservableObject
{
    private readonly StoreAndForwardForgeLinkApi _api;
    private readonly IWeighScale _scale;
    private readonly ILabelPrinter _printer;
    private readonly ILogger<StationViewModel> _logger;
    private readonly SynchronizationContext _ui;

    // Kept until the report succeeds or is queued, so a double-press or retry cannot double-count.
    private string _reportKey = NewKey();

    public StationViewModel(StationOptions station, StoreAndForwardForgeLinkApi api, IWeighScale scale, ILabelPrinter printer, ILogger<StationViewModel> logger)
    {
        Station = station;
        _api = api;
        _scale = scale;
        _printer = printer;
        _logger = logger;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _api.ConnectivityChanged += (_, online) => _ui.Post(_ => IsOnline = online, null);
        _api.Queue.Changed += (_, _) => _ui.Post(async _ => await RefreshQueueAsync(), null);
    }

    public StationOptions Station { get; }

    public ObservableCollection<LogLine> Log { get; } = [];

    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private int _pendingSync;
    [ObservableProperty] private int _rejectedSync;
    [ObservableProperty] private string _machineState = "Unknown";
    [ObservableProperty] private string? _machineReason;
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string? _lastZpl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _operatorBadge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(HasWorkOrder))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(CompleteCommand), nameof(ReportCommand), nameof(ReadScaleCommand))]
    private WorkOrderDto? _workOrder;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ReportCommand))] private decimal _goodQuantity;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ReportCommand))] private decimal _scrapQuantity;
    [ObservableProperty] private string _scrapReason = "";
    [ObservableProperty] private string _componentLot = "";
    [ObservableProperty] private decimal _componentQuantity;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(CompleteCommand), nameof(ReportCommand), nameof(ReadScaleCommand))]
    private bool _isBusy;

    public bool HasWorkOrder => WorkOrder is not null;

    public double Progress => WorkOrder is { QuantityOrdered: > 0 } w ? (double)Math.Min(1, w.QuantityCompleted / w.QuantityOrdered) * 100 : 0;

    private bool IsRunningHere => WorkOrder is { Status: "InProgress" } w && w.CurrentMachine == Station.MachineCode;

    /// <summary>Background refresh: machine state, work order progress, queue depth. Runs on the UI context.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Info($"Station {Station.StationName} ready on {Station.MachineCode}. Scan your badge (EMP:…) and a work order.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await RefreshAsync(ct);
        }
        while (await WaitAsync(timer, ct));
    }

    [RelayCommand]
    private async Task ProcessScanAsync()
    {
        var scan = ScanParser.Parse(ScanText);
        ScanText = "";
        switch (scan.Kind)
        {
            case ScanKind.Badge:
                OperatorBadge = scan.Value;
                Info($"Operator {scan.Value} signed in.");
                break;
            case ScanKind.WorkOrder:
                await LoadWorkOrderAsync(scan.Value);
                break;
            case ScanKind.Lot:
            case ScanKind.Gs1 when scan.Lot is not null:
                ComponentLot = scan.Lot ?? scan.Value;
                if (scan.Quantity is { } q) ComponentQuantity = q;
                Info($"Component lot {ComponentLot} staged for backflush.");
                break;
            default:
                Warn($"Unrecognised barcode: {scan.Raw}");
                break;
        }
    }

    private bool CanStart() => !IsBusy && WorkOrder is not null && !IsRunningHere && !string.IsNullOrEmpty(OperatorBadge);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => Busy(async () =>
    {
        WorkOrder = await _api.StartWorkOrderAsync(WorkOrder!.Number, new StartWorkOrderRequest(Station.MachineCode, OperatorBadge));
        Info($"Started {WorkOrder.Number} on {Station.MachineCode}; {OperatorBadge} clocked on.");
    });

    private bool CanStop() => !IsBusy && IsRunningHere;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task PauseAsync() => Busy(async () =>
    {
        WorkOrder = await _api.StopWorkOrderAsync(WorkOrder!.Number, new StopWorkOrderRequest(false, OperatorBadge));
        Info($"Paused {WorkOrder.Number}; labor clocked off.");
    });

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task CompleteAsync() => Busy(async () =>
    {
        WorkOrder = await _api.StopWorkOrderAsync(WorkOrder!.Number, new StopWorkOrderRequest(true, OperatorBadge));
        Info($"Completed {WorkOrder.Number}.");
    });

    private bool CanReadScale() => !IsBusy && WorkOrder?.UnitWeightKg is > 0;

    [RelayCommand(CanExecute = nameof(CanReadScale))]
    private Task ReadScaleAsync() => Busy(async () =>
    {
        var reading = await _scale.ReadAsync();
        var pieces = ScaleProtocol.PiecesFromWeight(reading.NetKg, WorkOrder!.UnitWeightKg!.Value);
        GoodQuantity = pieces;
        Info($"Scale {_scale.Name}: {reading.NetKg} kg → {pieces} pcs at {WorkOrder.UnitWeightKg} kg/pc.");
    });

    private bool CanReport() => !IsBusy && IsRunningHere && GoodQuantity + ScrapQuantity > 0;

    [RelayCommand(CanExecute = nameof(CanReport))]
    private Task ReportAsync() => Busy(async () =>
    {
        var wo = WorkOrder!;
        // Lot number is deterministic, so the label printed now matches what the server records, even offline.
        var lot = LotNumbering.ForWorkOrder(wo.Number, DateTime.UtcNow);
        var consumed = string.IsNullOrWhiteSpace(ComponentLot) || ComponentQuantity <= 0
            ? null
            : new[] { new ConsumedLotDto(ComponentLot, ComponentQuantity) };
        var request = new ReportProductionRequest(GoodQuantity, ScrapQuantity, string.IsNullOrWhiteSpace(ScrapReason) ? null : ScrapReason,
            lot, consumed, OperatorBadge);

        try
        {
            var result = await _api.ReportProductionAsync(wo.Number, request, _reportKey);
            Info($"Reported {GoodQuantity} good / {ScrapQuantity} scrap → lot {result.OutputLotNumber}. {result.QuantityRemaining} remaining.");
            WorkOrder = await _api.GetWorkOrderAsync(wo.Number) ?? wo;
        }
        catch (QueuedForSyncException queued)
        {
            Warn(queued.Message);
        }

        if (GoodQuantity > 0)
            await PrintLabelAsync(new LotLabel(wo.Sku, wo.ItemDescription, lot, GoodQuantity, wo.Uom, wo.Number, DateTime.UtcNow));

        GoodQuantity = 0;
        ScrapQuantity = 0;
        ScrapReason = "";
        _reportKey = NewKey();
    });

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        var (sent, rejected, remaining) = await _api.FlushAsync();
        Info($"Sync: {sent} sent, {rejected} rejected, {remaining} still queued.");
        await RefreshQueueAsync();
    }

    private async Task LoadWorkOrderAsync(string number)
    {
        await Busy(async () =>
        {
            var wo = await _api.GetWorkOrderAsync(number);
            if (wo is null)
            {
                Warn($"Work order {number} not found (not released in ERP?).");
                return;
            }
            if (!string.Equals(wo.Line, Station.Line, StringComparison.OrdinalIgnoreCase))
                Warn($"{wo.Number} is scheduled on line {wo.Line}, this station is on {Station.Line}.");
            WorkOrder = wo;
            _reportKey = NewKey();
            Info($"Loaded {wo.Number}: {wo.Sku} {wo.ItemDescription}, {wo.QuantityCompleted}/{wo.QuantityOrdered} {wo.Uom}, {wo.Status}.");
        });
    }

    private async Task PrintLabelAsync(LotLabel label)
    {
        LastZpl = ZplLabelBuilder.Build(label);
        try
        {
            await _printer.PrintAsync(LastZpl);
            Info($"Label printed on {_printer.Name}.");
        }
        catch (Exception ex)
        {
            Warn($"Label printer {_printer.Name} failed: {ex.Message}. Use Reprint once fixed.");
        }
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (LastZpl is null) return;
        try
        {
            await _printer.PrintAsync(LastZpl);
            Info("Label reprinted.");
        }
        catch (Exception ex)
        {
            Warn($"Reprint failed: {ex.Message}");
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var machine = (await _api.GetMachinesAsync(Station.Line, ct)).FirstOrDefault(m => m.Code == Station.MachineCode);
            if (machine is not null)
            {
                MachineState = machine.State;
                MachineReason = machine.Reason;
            }
            if (WorkOrder is { } wo && !IsBusy)
                WorkOrder = await _api.GetWorkOrderAsync(wo.Number, ct) ?? wo;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Refresh failed");
            MachineState = "Unknown";
        }
        await RefreshQueueAsync();
    }

    private async Task RefreshQueueAsync()
    {
        PendingSync = await _api.Queue.CountAsync(QueuedCommandState.Queued);
        RejectedSync = await _api.Queue.CountAsync(QueuedCommandState.Rejected);
    }

    private async Task Busy(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Station action failed");
            Warn(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>One-tap scrap reason codes (the operator can still type a different code).</summary>
    [RelayCommand]
    private void SetScrapReason(string reason) => ScrapReason = reason;

    private void Info(string text) => Add(text, "info");

    private void Warn(string text) => Add(text, "warn");

    private void Add(string text, string level)
    {
        Log.Insert(0, new LogLine(DateTime.Now, text, level));
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");
}
