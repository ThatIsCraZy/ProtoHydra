namespace DataService.Infrastructure.Firewall;

public interface IFirewallTemporaryRuleService
{
    Task<FirewallTemporaryRuleResult> StartTemporaryApplicationRuleAsync(
        string executablePath,
        int ownerProcessId,
        IReadOnlyCollection<FirewallPortProbe> ports,
        CancellationToken cancellationToken);

    Task<FirewallTemporaryRuleResult> StopTemporaryApplicationRuleAsync(
        CancellationToken cancellationToken);
}
