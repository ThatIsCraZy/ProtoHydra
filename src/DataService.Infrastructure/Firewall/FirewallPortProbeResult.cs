namespace DataService.Infrastructure.Firewall;

public sealed record FirewallPortProbeResult(
    FirewallPortProbe Probe,
    bool? IsAllowed,
    bool? IsRestricted,
    string? Message);
