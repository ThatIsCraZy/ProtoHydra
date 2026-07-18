namespace DataService.Infrastructure.Firewall;

public sealed record FirewallTemporaryRuleResult(
    bool Started,
    string Message);
