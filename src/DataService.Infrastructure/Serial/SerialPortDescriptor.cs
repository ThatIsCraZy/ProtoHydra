namespace DataService.Infrastructure.Serial;

/// <summary>
/// One serial port offered in the port picker. <see cref="Description"/> carries the
/// device friendly name from the Windows device tree when it could be resolved, which
/// is what tells three identical-looking COM numbers apart.
/// </summary>
public sealed record SerialPortDescriptor(string PortName, string? Description)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description)
        ? PortName
        : $"{PortName} — {Description}";
}
