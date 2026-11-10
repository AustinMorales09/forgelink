namespace ForgeLink.Domain.Equipment;

public enum MachineState
{
    Offline,
    Idle,
    Running,
    Down,
}

/// <summary>A piece of shop-floor equipment whose state arrives from OT telemetry (MQTT, PLC gateway, HMI).</summary>
public class Machine
{
    private Machine() { }

    public Machine(string code, string line, string description, double idealCycleTimeSeconds)
    {
        Code = Guard.NotBlank(code, nameof(code)).ToUpperInvariant();
        Line = Guard.NotBlank(line, nameof(line)).ToUpperInvariant();
        Description = Guard.NotBlank(description, nameof(description));
        IdealCycleTimeSeconds = idealCycleTimeSeconds > 0
            ? idealCycleTimeSeconds
            : throw new DomainException("validation", "Ideal cycle time must be positive.");
        State = MachineState.Offline;
    }

    public string Code { get; private set; } = "";
    public string Line { get; private set; } = "";
    public string Description { get; private set; } = "";
    public double IdealCycleTimeSeconds { get; private set; }
    public MachineState State { get; private set; }
    public string? StateReason { get; private set; }
    public DateTime StateChangedAtUtc { get; private set; }
    public DateTime? LastHeartbeatUtc { get; private set; }
    public string? CurrentWorkOrder { get; private set; }

    /// <summary>Returns true when the state actually changed. Out-of-order (older) messages are ignored.</summary>
    public bool ApplyState(MachineState state, string? reason, DateTime atUtc)
    {
        atUtc = Guard.Utc(atUtc);
        Heartbeat(atUtc);
        if (atUtc < StateChangedAtUtc) return false;
        if (state == State && reason == StateReason) return false;

        State = state;
        StateReason = state is MachineState.Down or MachineState.Offline or MachineState.Idle ? reason : null;
        StateChangedAtUtc = atUtc;
        return true;
    }

    public void Heartbeat(DateTime atUtc)
    {
        atUtc = Guard.Utc(atUtc);
        if (LastHeartbeatUtc is null || atUtc > LastHeartbeatUtc) LastHeartbeatUtc = atUtc;
    }

    public bool IsStale(DateTime nowUtc, TimeSpan timeout) =>
        State != MachineState.Offline && (LastHeartbeatUtc is null || nowUtc - LastHeartbeatUtc > timeout);

    public void AssignWorkOrder(string? workOrderNumber) => CurrentWorkOrder = workOrderNumber;

    public static bool CountsAsDowntime(MachineState state) => state is MachineState.Down or MachineState.Offline;
}

/// <summary>A period during which a machine was unavailable. <see cref="EndUtc"/> is null while ongoing.</summary>
public class DowntimeEvent
{
    private DowntimeEvent() { }

    public DowntimeEvent(string machineCode, DateTime startUtc, string reasonCode)
    {
        MachineCode = Guard.NotBlank(machineCode, nameof(machineCode));
        StartUtc = Guard.Utc(startUtc);
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? "UNSPECIFIED" : reasonCode.Trim().ToUpperInvariant();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string MachineCode { get; private set; } = "";
    public DateTime StartUtc { get; private set; }
    public DateTime? EndUtc { get; private set; }
    public string ReasonCode { get; private set; } = "";

    public void Close(DateTime endUtc)
    {
        if (EndUtc is not null) return;
        endUtc = Guard.Utc(endUtc);
        EndUtc = endUtc < StartUtc ? StartUtc : endUtc;
    }

    /// <summary>Portion of this event that overlaps [fromUtc, toUtc). Open events run to <paramref name="toUtc"/>.</summary>
    public TimeSpan OverlapWith(DateTime fromUtc, DateTime toUtc)
    {
        var start = StartUtc > fromUtc ? StartUtc : fromUtc;
        var end = EndUtc is { } e && e < toUtc ? e : toUtc;
        return end > start ? end - start : TimeSpan.Zero;
    }
}

/// <summary>Hourly production counter per machine, aggregated from high-frequency count signals.</summary>
public class ProductionCounter
{
    private ProductionCounter() { }

    public ProductionCounter(string machineCode, DateTime bucketStartUtc)
    {
        MachineCode = Guard.NotBlank(machineCode, nameof(machineCode));
        BucketStartUtc = BucketFor(bucketStartUtc);
    }

    public static DateTime BucketFor(DateTime atUtc)
    {
        atUtc = Guard.Utc(atUtc);
        return new DateTime(atUtc.Year, atUtc.Month, atUtc.Day, atUtc.Hour, 0, 0, DateTimeKind.Utc);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string MachineCode { get; private set; } = "";
    public DateTime BucketStartUtc { get; private set; }
    public int GoodCount { get; private set; }
    public int RejectCount { get; private set; }

    public void Add(int good, int reject)
    {
        if (good < 0 || reject < 0) throw new DomainException("validation", "Counts cannot be negative.");
        GoodCount += good;
        RejectCount += reject;
    }
}
