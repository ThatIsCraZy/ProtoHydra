namespace DataService.Infrastructure.Firewall;

public interface IFirewallStatusService
{
    Task<FirewallStatusSnapshot> CheckAsync(
        string executablePath,
        IReadOnlyCollection<FirewallPortProbe> ports,
        CancellationToken cancellationToken);
}
