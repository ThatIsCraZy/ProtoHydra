namespace DataService.Infrastructure.Firewall;

public sealed record FirewallProfileState(
    FirewallProfile Profile,
    bool FirewallEnabled,
    bool BlockAllInbound,
    bool DefaultInboundAllow);
