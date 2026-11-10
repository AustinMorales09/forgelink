namespace ForgeLink.Desktop;

public sealed class StationOptions
{
    public string StationName { get; set; } = "Station";
    public string MachineCode { get; set; } = "";
    public string Line { get; set; } = "";
    public ScaleOptions Scale { get; set; } = new();
    public PrinterOptions LabelPrinter { get; set; } = new();
}

public sealed class ScaleOptions
{
    /// <summary>Simulated | Serial</summary>
    public string Mode { get; set; } = "Simulated";
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
}

public sealed class PrinterOptions
{
    /// <summary>File | Tcp</summary>
    public string Mode { get; set; } = "File";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9100;
    public string Folder { get; set; } = "";
}
