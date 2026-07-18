namespace DataService.Infrastructure.Firewall;

public sealed record FirewallPortProbe(
    string Name,
    int Port,
    FirewallTransportProtocol TransportProtocol,
    string? BindAddress);
