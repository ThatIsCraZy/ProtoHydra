namespace DataService.Infrastructure.Firewall;

/// <summary>Matches the NET_FW_PROFILE_TYPE2 bit values of the Windows Firewall API.</summary>
[Flags]
public enum FirewallProfile
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4
}
