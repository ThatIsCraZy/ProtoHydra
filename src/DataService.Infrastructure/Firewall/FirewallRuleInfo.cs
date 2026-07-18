namespace DataService.Infrastructure.Firewall;

public sealed record FirewallRuleInfo(
    string Name,
    bool Enabled,
    bool IsAllow,
    int Protocol,
    string? LocalPorts,
    string? ApplicationName,
    string? ServiceName,
    int Profiles,
    string? RemoteAddresses)
{
    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;
    public const int ProtocolAny = 256;
}
