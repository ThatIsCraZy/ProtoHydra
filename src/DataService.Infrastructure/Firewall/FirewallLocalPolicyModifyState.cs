namespace DataService.Infrastructure.Firewall;

/// <summary>Matches NET_FW_MODIFY_STATE: whether locally added rules take effect.</summary>
public enum FirewallLocalPolicyModifyState
{
    Ok = 0,
    GroupPolicyOverride = 1,
    InboundBlocked = 2,
    Unknown = 3
}
