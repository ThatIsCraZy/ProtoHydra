namespace DataService.Infrastructure.Firewall;

/// <summary>
/// A port probe enriched with its network scope: loopback binds bypass the firewall,
/// binds to a specific interface address are evaluated only against that interface's profile.
/// </summary>
public sealed record FirewallProbeTarget(
    FirewallPortProbe Probe,
    bool IsLoopback,
    FirewallProfile? Profile);
