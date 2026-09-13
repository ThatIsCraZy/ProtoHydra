using System.Globalization;

namespace DataService.Infrastructure.Serial;

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

/// <summary>
/// Flow control offered to the user. DSR/DTR handshaking is deliberately absent:
/// <see cref="System.IO.Ports.SerialPort"/> cannot drive it, so offering it would
/// promise something the transport does not deliver.
/// </summary>
public enum SerialFlowControl
{
    None,
    XOnXOff,
    RtsCts,
    RtsCtsXOnXOff
}

/// <summary>
/// Everything needed to open one serial console connection.
/// </summary>
public sealed record SerialConnectionSettings(
    string PortName,
    int BaudRate = 115_200,
    int DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    SerialFlowControl FlowControl = SerialFlowControl.None,
    bool DataTerminalReady = true,
    bool RequestToSend = true)
{
    /// <summary>Compact line such as "COM3 · 115200 8N1" for status displays.</summary>
    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"{PortName} · {BaudRate} {DataBits}{ParityLetter}{StopBitsLabel}");

    private char ParityLetter => Parity switch
    {
        SerialParity.Odd => 'O',
        SerialParity.Even => 'E',
        SerialParity.Mark => 'M',
        SerialParity.Space => 'S',
        _ => 'N'
    };

    private string StopBitsLabel => StopBits switch
    {
        SerialStopBits.OnePointFive => "1.5",
        SerialStopBits.Two => "2",
        _ => "1"
    };
}
