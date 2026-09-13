namespace DataService.Infrastructure.Serial;

/// <summary>
/// Modem control lines as of the last poll. CTS/DSR/DCD are driven by the device,
/// DTR/RTS by this application.
/// </summary>
public readonly record struct SerialLineStatus(
    bool ClearToSend,
    bool DataSetReady,
    bool CarrierDetect,
    bool DataTerminalReady,
    bool RequestToSend)
{
    public static readonly SerialLineStatus None = new(false, false, false, false, false);
}
