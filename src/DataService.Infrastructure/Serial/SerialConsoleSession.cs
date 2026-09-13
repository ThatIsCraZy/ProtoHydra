using System.IO.Ports;
using System.Threading.Channels;

namespace DataService.Infrastructure.Serial;

/// <summary>
/// <see cref="SerialPort"/> wrapped in an async read loop and a queued writer, so a
/// stalled line (hardware handshake with CTS low) never blocks the UI thread.
/// </summary>
public sealed class SerialConsoleSession : ISerialConsoleSession
{
    private const int ReadBufferSize = 8 * 1024;

    /// <summary>
    /// How long <see cref="Open"/> waits for a previous close to hand the handle back.
    /// Long enough for a driver that is merely slow, short enough that a wedged one does
    /// not hold the reconnect hostage.
    /// </summary>
    private static readonly TimeSpan HandleReleaseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Attempts and spacing for <see cref="OpenPort"/>, so up to half a second.</summary>
    private const int OpenAttempts = 10;

    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();

    private SerialPort? _port;
    private CancellationTokenSource? _lifetime;
    private Channel<ReadOnlyMemory<byte>>? _outbound;
    private Task? _readLoop;
    private Task? _writeLoop;
    private Task _handleRelease = Task.CompletedTask;
    private int _faultReported;
    private long _bytesReceived;
    private long _bytesSent;

    public event EventHandler<byte[]>? DataReceived;

    public event EventHandler<string>? Faulted;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _port is { IsOpen: true };
            }
        }
    }

    public SerialConnectionSettings? Settings { get; private set; }

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void Open(SerialConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            if (_port is not null)
            {
                throw new InvalidOperationException("The session already holds an open port.");
            }

            // Close releases the handle on a background thread so a dead device cannot
            // freeze the caller. Opening again before that finished would hit our own
            // still-open handle and report the port as taken by another application.
            _handleRelease.Wait(HandleReleaseTimeout);

            var port = OpenPort(settings);

            try
            {
                port.DtrEnable = settings.DataTerminalReady;

                // The driver owns RTS while hardware handshaking is on; writing it then throws.
                if (settings.FlowControl is SerialFlowControl.None or SerialFlowControl.XOnXOff)
                {
                    port.RtsEnable = settings.RequestToSend;
                }

                port.DiscardInBuffer();
                port.DiscardOutBuffer();
            }
            catch
            {
                port.Dispose();
                throw;
            }

            _port = port;
            Settings = settings;
            _faultReported = 0;
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            _lifetime = new CancellationTokenSource();
            _outbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
            {
                SingleReader = true
            });

            var stream = port.BaseStream;
            var token = _lifetime.Token;
            _readLoop = Task.Run(() => ReadLoopAsync(stream, token), CancellationToken.None);
            _writeLoop = Task.Run(() => WriteLoopAsync(stream, _outbound.Reader, token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Opens the port, retrying briefly while the driver reports it as denied. Measured on
    /// a virtual COM port, the handle stays busy for roughly one backoff after our own
    /// close returned, which would otherwise turn every quick reconnect into "port in use".
    /// A port genuinely held by another application only costs this budget before the same
    /// error surfaces.
    /// </summary>
    private static SerialPort OpenPort(SerialConnectionSettings settings)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = new SerialPort(
                settings.PortName,
                settings.BaudRate,
                MapParity(settings.Parity),
                settings.DataBits,
                MapStopBits(settings.StopBits))
            {
                Handshake = MapHandshake(settings.FlowControl),
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 5_000,
                ReadBufferSize = 64 * 1024,
                WriteBufferSize = 16 * 1024,
                DiscardNull = false
            };

            try
            {
                port.Open();
                return port;
            }
            catch (UnauthorizedAccessException) when (attempt < OpenAttempts)
            {
                port.Dispose();
                Thread.Sleep(OpenRetryDelay);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        Channel<ReadOnlyMemory<byte>>? outbound;
        lock (_gate)
        {
            outbound = _outbound;
        }

        outbound?.Writer.TryWrite(data);
    }

    public async Task SendBreakAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _port;
        }

        if (port is null)
        {
            return;
        }

        try
        {
            port.BreakState = true;
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            ReportFault(exception);
            return;
        }

        try
        {
            port.BreakState = false;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            ReportFault(exception);
        }
    }

    /// <summary>
    /// True while hardware handshaking is configured: the driver then owns RTS and
    /// <see cref="SerialPort.RtsEnable"/> throws on both read and write.
    /// </summary>
    public bool DriverOwnsRequestToSend
        => Settings?.FlowControl is SerialFlowControl.RtsCts or SerialFlowControl.RtsCtsXOnXOff;

    public void SetDataTerminalReady(bool value) => ApplySignal(port => port.DtrEnable = value);

    public void SetRequestToSend(bool value)
    {
        if (DriverOwnsRequestToSend)
        {
            return;
        }

        ApplySignal(port => port.RtsEnable = value);
    }

    public SerialLineStatus ReadLineStatus()
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _port;
        }

        if (port is not { IsOpen: true })
        {
            return SerialLineStatus.None;
        }

        try
        {
            return new SerialLineStatus(
                port.CtsHolding,
                port.DsrHolding,
                port.CDHolding,
                port.DtrEnable,
                DriverOwnsRequestToSend || port.RtsEnable);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // Reading the modem lines is the only traffic that flows while a device sits
            // idle. If that fails the port is gone, and a pending read may never return to
            // tell us, so this poll is what turns an unplugged adapter into a disconnect.
            ReportFault(exception);
            return SerialLineStatus.None;
        }
    }

    public void Close()
    {
        SerialPort? port;
        CancellationTokenSource? lifetime;
        Channel<ReadOnlyMemory<byte>>? outbound;
        TaskCompletionSource released;

        lock (_gate)
        {
            port = _port;
            lifetime = _lifetime;
            outbound = _outbound;
            _port = null;
            _lifetime = null;
            _outbound = null;
            _readLoop = null;
            _writeLoop = null;

            if (port is null)
            {
                return;
            }

            // Published while the gate is held, so an Open racing this Close can never
            // slip past an already completed task from an earlier session.
            released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _handleRelease = released.Task;
        }

        outbound?.Writer.TryComplete();
        try
        {
            lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Disposing a SerialPort whose USB adapter was pulled can block for seconds, so
        // it happens off the caller's thread; the UI reports the port as closed at once.
        // Open waits on this task, so a reconnect still sees a released handle.
        _ = Task.Run(() =>
        {
            try
            {
                port.Dispose();
            }
            catch (Exception)
            {
                // Closing the handle of a device that is already gone can fail in ways the
                // driver decides. Nothing useful is left to do, and this runs detached, so
                // an escaping exception would only surface as an unobserved task fault.
            }
            finally
            {
                lifetime?.Dispose();
                released.TrySetResult();
            }
        });

        Settings = null;
    }

    public void Dispose() => Close();

    private void ApplySignal(Action<SerialPort> apply)
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _port;
        }

        if (port is not { IsOpen: true })
        {
            return;
        }

        try
        {
            apply(port);
        }
        catch (InvalidOperationException)
        {
            // The mode does not allow this line to be driven; that is not a broken link.
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            ReportFault(exception);
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    // A serial stream with an infinite read timeout blocks until a byte
                    // arrives, so zero means the handle is gone: the adapter was pulled or
                    // the driver died. Spinning here would burn a core instead of saying so.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        ReportFault(new IOException("The serial port stopped delivering data."));
                    }

                    return;
                }

                Interlocked.Add(ref _bytesReceived, read);
                DataReceived?.Invoke(this, buffer[..read]);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportFault(exception);
            }
        }
    }

    private async Task WriteLoopAsync(
        Stream stream,
        ChannelReader<ReadOnlyMemory<byte>> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, chunk.Length);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportFault(exception);
            }
        }
    }

    private void ReportFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) != 0)
        {
            return;
        }

        var message = exception switch
        {
            UnauthorizedAccessException => "Port access was lost. The device was removed or another application took it.",
            TimeoutException => "The device stopped accepting data (write timeout).",
            _ => exception.Message
        };

        Close();
        Faulted?.Invoke(this, message);
    }

    private static bool IsTransportFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ObjectDisposedException
            or TimeoutException
            or ArgumentException;

    private static Parity MapParity(SerialParity parity) => parity switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits MapStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };

    private static Handshake MapHandshake(SerialFlowControl flowControl) => flowControl switch
    {
        SerialFlowControl.XOnXOff => Handshake.XOnXOff,
        SerialFlowControl.RtsCts => Handshake.RequestToSend,
        SerialFlowControl.RtsCtsXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => Handshake.None
    };
}
