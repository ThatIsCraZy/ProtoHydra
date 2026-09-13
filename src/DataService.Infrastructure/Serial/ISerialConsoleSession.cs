namespace DataService.Infrastructure.Serial;

/// <summary>
/// One open serial console connection. Reading runs on a background loop and hands
/// raw chunks to <see cref="DataReceived"/>; subscribers marshal to their own thread.
/// </summary>
public interface ISerialConsoleSession : IDisposable
{
    /// <summary>Raw bytes as they arrived, in order.</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>The connection dropped unexpectedly; the argument is a display message.</summary>
    event EventHandler<string>? Faulted;

    bool IsOpen { get; }

    SerialConnectionSettings? Settings { get; }

    long BytesReceived { get; }

    long BytesSent { get; }

    /// <summary>
    /// True while hardware handshaking runs: RTS then belongs to the driver and
    /// <see cref="SetRequestToSend"/> does nothing.
    /// </summary>
    bool DriverOwnsRequestToSend { get; }

    /// <summary>
    /// Opens the port. Throws <see cref="UnauthorizedAccessException"/> when another
    /// application holds it and <see cref="IOException"/> when the parameters are
    /// rejected by the driver.
    /// </summary>
    void Open(SerialConnectionSettings settings);

    /// <summary>Queues bytes for transmission; returns without waiting for the wire.</summary>
    void Send(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Holds the line in break state, the interrupt many bootloaders and ROM monitors
    /// listen for.
    /// </summary>
    Task SendBreakAsync(TimeSpan duration, CancellationToken cancellationToken);

    void SetDataTerminalReady(bool value);

    void SetRequestToSend(bool value);

    SerialLineStatus ReadLineStatus();

    void Close();
}
