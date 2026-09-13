namespace DataService.Infrastructure.Serial;

public interface ISerialPortEnumerator
{
    /// <summary>
    /// Lists the serial ports currently present, ordered by COM number. Runs off the
    /// calling thread because the device-tree lookup touches the registry.
    /// </summary>
    Task<IReadOnlyList<SerialPortDescriptor>> ListAsync(CancellationToken cancellationToken);
}
