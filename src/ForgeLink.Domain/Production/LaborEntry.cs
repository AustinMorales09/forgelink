namespace ForgeLink.Domain.Production;

/// <summary>Direct labor captured at the station: an operator clocked on to a work order at a machine.</summary>
public class LaborEntry
{
    private LaborEntry() { }

    public LaborEntry(string employeeBadge, string workOrderNumber, string machineCode, DateTime startUtc)
    {
        EmployeeBadge = Guard.NotBlank(employeeBadge, nameof(employeeBadge));
        WorkOrderNumber = Guard.NotBlank(workOrderNumber, nameof(workOrderNumber));
        MachineCode = Guard.NotBlank(machineCode, nameof(machineCode));
        StartUtc = Guard.Utc(startUtc);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EmployeeBadge { get; private set; } = "";
    public string WorkOrderNumber { get; private set; } = "";
    public string MachineCode { get; private set; } = "";
    public DateTime StartUtc { get; private set; }
    public DateTime? EndUtc { get; private set; }

    public bool IsOpen => EndUtc is null;

    public decimal Hours => EndUtc is null ? 0 : Math.Round((decimal)(EndUtc.Value - StartUtc).TotalHours, 4);

    public void ClockOut(DateTime endUtc)
    {
        if (!IsOpen) return;
        endUtc = Guard.Utc(endUtc);
        EndUtc = endUtc < StartUtc ? StartUtc : endUtc;
    }
}
