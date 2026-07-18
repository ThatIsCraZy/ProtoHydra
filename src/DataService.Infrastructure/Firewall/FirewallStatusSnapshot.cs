namespace DataService.Infrastructure.Firewall;

public sealed record FirewallStatusSnapshot(
    FirewallStatusLevel Level,
    string Summary,
    string Detail,
    bool IsApplicationAllowed,
    IReadOnlyList<FirewallPortProbeResult> Ports,
    IReadOnlyList<string> Warnings)
{
    public static FirewallStatusSnapshot Unavailable(string detail)
        => new(
            FirewallStatusLevel.Yellow,
            "Firewall status unavailable",
            detail,
            IsApplicationAllowed: false,
            Array.Empty<FirewallPortProbeResult>(),
            Array.Empty<string>());
}
