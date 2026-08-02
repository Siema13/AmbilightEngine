namespace AmbilightEngine.Services;

public sealed class WledDiagnosticsResult
{
    public bool IsReachable { get; set; }
    public string StatusText { get; set; } = "Brak danych";
    public string DeviceName { get; set; } = "-";
    public string Version { get; set; } = "-";
    public string PowerState { get; set; } = "-";
    public string Brightness { get; set; } = "-";
    public string LedInfo { get; set; } = "-";
}