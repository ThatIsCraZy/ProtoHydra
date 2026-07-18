namespace DataService.Infrastructure.Firewall;

public sealed record FirewallPolicyState(
    IReadOnlyList<FirewallProfileState> ActiveProfiles,
    FirewallLocalPolicyModifyState ModifyState,
    IReadOnlyList<FirewallRuleInfo> InboundRules);
