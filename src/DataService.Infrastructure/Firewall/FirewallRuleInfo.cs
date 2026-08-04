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
    string? RemoteAddresses,
    // INetFwRule3 (Windows 8+). A rule bound to an AppContainer package or to an
    // owning user SID only applies to that principal — never to this Win32 process,
    // even though such rules carry no ApplicationName and match any port.
    string? LocalAppPackageId = null,
    string? LocalUserOwner = null)
{
    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;
    public const int ProtocolAny = 256;
}
