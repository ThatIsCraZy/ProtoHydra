using DataService.Core.Configuration;
using DataService.Core.Events;

namespace DataService.Infrastructure.Firewall;

public static class FirewallProbeFactory
{
    public static IEnumerable<FirewallPortProbe> Create(
        ProtocolKind protocol,
        string bindAddress,
        int port,
        string ftpPassivePortRange = FtpPassivePortRange.Default)
    {
        var name = protocol.ToString().ToUpperInvariant();
        yield return new FirewallPortProbe(
            name, port,
            protocol == ProtocolKind.Tftp ? FirewallTransportProtocol.Udp : FirewallTransportProtocol.Tcp,
            bindAddress);

        if (protocol is not (ProtocolKind.Ftp or ProtocolKind.Ftps)
            || !FtpPassivePortRange.TryParse(ftpPassivePortRange, out var range))
        {
            yield break;
        }

        for (var dataPort = range.MinPort; dataPort <= range.MaxPort; dataPort++)
        {
            yield return new FirewallPortProbe(
                $"{name} passive data", dataPort, FirewallTransportProtocol.Tcp, bindAddress);
        }
    }
}
