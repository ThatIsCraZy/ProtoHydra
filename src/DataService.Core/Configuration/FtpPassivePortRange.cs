using System.Globalization;

namespace DataService.Core.Configuration;

public readonly record struct FtpPassivePortRange(int MinPort, int MaxPort)
{
    public const string Default = "50000-50100";

    public static bool TryParse(string? value, out FtpPassivePortRange range)
    {
        range = default;
        var parts = value?.Split('-');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minPort)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var maxPort)
            || minPort is < 1024 or > 65535
            || maxPort < minPort || maxPort > 65535)
        {
            return false;
        }

        range = new FtpPassivePortRange(minPort, maxPort);
        return true;
    }
}
