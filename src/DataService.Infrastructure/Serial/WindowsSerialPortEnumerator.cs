using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DataService.Infrastructure.Serial;

/// <summary>
/// Lists the serial ports from <see cref="SerialPort.GetPortNames"/> and adds the device
/// friendly name ("USB Serial Port", "Standard Serial over Bluetooth link") by walking the
/// COM-port device interface class in the registry. A failed lookup only costs the extra
/// label, never the port itself.
/// </summary>
public sealed class WindowsSerialPortEnumerator : ISerialPortEnumerator
{
    private const string ComPortInterfaceClass =
        @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{86e0d1e0-8089-11d0-9ce4-08003e301f73}";

    private const string DeviceEnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    public Task<IReadOnlyList<SerialPortDescriptor>> ListAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<SerialPortDescriptor>>(
            () =>
            {
                var names = SerialPort.GetPortNames()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var descriptions = OperatingSystem.IsWindows()
                    ? ReadFriendlyNames()
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                return names
                    .Select(name => new SerialPortDescriptor(
                        name,
                        descriptions.TryGetValue(name, out var description) ? description : null))
                    .OrderBy(descriptor => PortSortKey(descriptor.PortName))
                    .ThenBy(descriptor => descriptor.PortName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            },
            cancellationToken);

    /// <summary>
    /// COM10 must sort after COM9, so ports are ordered by their number and unnumbered
    /// names fall to the end.
    /// </summary>
    private static int PortSortKey(string portName)
    {
        var digits = new string(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> ReadFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var interfaceClass = Registry.LocalMachine.OpenSubKey(ComPortInterfaceClass);
            if (interfaceClass is null)
            {
                return result;
            }

            foreach (var interfaceKeyName in interfaceClass.GetSubKeyNames())
            {
                var instancePath = ToDeviceInstancePath(interfaceKeyName);
                if (instancePath is null)
                {
                    continue;
                }

                using var deviceKey = Registry.LocalMachine.OpenSubKey($@"{DeviceEnumRoot}\{instancePath}");
                if (deviceKey is null)
                {
                    continue;
                }

                using var parameters = deviceKey.OpenSubKey("Device Parameters");
                if (parameters?.GetValue("PortName") is not string portName || string.IsNullOrWhiteSpace(portName))
                {
                    continue;
                }

                var friendlyName = deviceKey.GetValue("FriendlyName") as string
                    ?? deviceKey.GetValue("DeviceDesc") as string;
                var description = CleanDescription(friendlyName, portName);
                if (description is not null)
                {
                    result[portName.Trim()] = description;
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException)
        {
            // Without the device tree the picker still lists every COM port, just unlabelled.
        }

        return result;
    }

    /// <summary>
    /// Turns a device interface key name such as
    /// "##?#USB#VID_0403&amp;PID_6001#A6008isP#{86e0d1e0-...}" into the device instance
    /// path "USB\VID_0403&amp;PID_6001\A6008isP" used under the Enum hive.
    /// </summary>
    private static string? ToDeviceInstancePath(string interfaceKeyName)
    {
        if (!interfaceKeyName.StartsWith("##?#", StringComparison.Ordinal))
        {
            return null;
        }

        var body = interfaceKeyName[4..];
        var guidStart = body.IndexOf("#{", StringComparison.Ordinal);
        if (guidStart > 0)
        {
            body = body[..guidStart];
        }

        return body.Length == 0 ? null : body.Replace('#', '\\');
    }

    /// <summary>
    /// Strips the "(COM3)" suffix Windows appends to most friendly names; the port number
    /// is already the first thing shown in the picker.
    /// </summary>
    private static string? CleanDescription(string? friendlyName, string portName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return null;
        }

        var suffix = $"({portName.Trim()})";
        var cleaned = friendlyName.Trim();
        if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^suffix.Length].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }
}
