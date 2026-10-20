using DataService.Core.Events;
using DataService.Infrastructure.Firewall;

namespace DataService.Core.Tests.Infrastructure;

public sealed class FirewallProbeFactoryTests
{
    [Theory]
    [InlineData(ProtocolKind.Ftp, 21)]
    [InlineData(ProtocolKind.Ftps, 990)]
    public void Create_IncludesControlAndAllPassiveDataPorts(ProtocolKind protocol, int port)
    {
        var probes = FirewallProbeFactory.Create(protocol, "0.0.0.0", port, "41000-41002").ToArray();

        Assert.Equal([port, 41000, 41001, 41002], probes.Select(probe => probe.Port));
        Assert.All(probes, probe =>
        {
            Assert.Equal("0.0.0.0", probe.BindAddress);
            Assert.Equal(FirewallTransportProtocol.Tcp, probe.TransportProtocol);
        });
    }

    [Theory]
    [InlineData(ProtocolKind.Http, FirewallTransportProtocol.Tcp)]
    [InlineData(ProtocolKind.Sftp, FirewallTransportProtocol.Tcp)]
    [InlineData(ProtocolKind.Tftp, FirewallTransportProtocol.Udp)]
    public void Create_OtherProtocols_OnlyIncludesListener(ProtocolKind protocol, FirewallTransportProtocol transport)
    {
        var probe = Assert.Single(FirewallProbeFactory.Create(protocol, "127.0.0.1", 12345));

        Assert.Equal(12345, probe.Port);
        Assert.Equal(transport, probe.TransportProtocol);
    }
}
