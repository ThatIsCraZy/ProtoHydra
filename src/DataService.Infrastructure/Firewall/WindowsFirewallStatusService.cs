using System.Runtime.Versioning;

namespace DataService.Infrastructure.Firewall;

/// <summary>
/// Reads the modern Windows Firewall policy (INetFwPolicy2) — the same API the Temp-Fix
/// writes its rule through — and evaluates it per active profile via <see cref="FirewallPolicyEvaluator"/>.
/// </summary>
public sealed class WindowsFirewallStatusService : IFirewallStatusService
{
    public Task<FirewallStatusSnapshot> CheckAsync(
        string executablePath,
        IReadOnlyCollection<FirewallPortProbe> ports,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(FirewallStatusSnapshot.Unavailable("Windows Firewall is only available on Windows."));
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return Task.FromResult(FirewallStatusSnapshot.Unavailable("Executable path is not available."));
        }

        return Task.Run(
            () => OperatingSystem.IsWindows()
                ? CheckWindows(executablePath, ports)
                : FirewallStatusSnapshot.Unavailable("Windows Firewall is only available on Windows."),
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static FirewallStatusSnapshot CheckWindows(
        string executablePath,
        IReadOnlyCollection<FirewallPortProbe> ports)
    {
        try
        {
            var policy = ReadPolicyState();
            var targets = WindowsNetworkProfileResolver.CreateTargets(ports);
            return FirewallPolicyEvaluator.Evaluate(executablePath, targets, policy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FirewallStatusSnapshot.Unavailable($"Windows Firewall check failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static FirewallPolicyState ReadPolicyState()
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall COM API HNetCfg.FwPolicy2 is not registered.");
        dynamic policy = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Windows Firewall COM policy could not be created.");

        var activeProfileMask = (int)policy.CurrentProfileTypes;
        var profiles = new List<FirewallProfileState>();
        foreach (var profile in new[] { FirewallProfile.Domain, FirewallProfile.Private, FirewallProfile.Public })
        {
            if ((activeProfileMask & (int)profile) == 0)
            {
                continue;
            }

            profiles.Add(new FirewallProfileState(
                profile,
                (bool)policy.FirewallEnabled((int)profile),
                (bool)policy.BlockAllInboundTraffic((int)profile),
                (int)policy.DefaultInboundAction((int)profile) == 1));
        }

        FirewallLocalPolicyModifyState modifyState;
        try
        {
            modifyState = (int)policy.LocalPolicyModifyState switch
            {
                0 => FirewallLocalPolicyModifyState.Ok,
                1 => FirewallLocalPolicyModifyState.GroupPolicyOverride,
                2 => FirewallLocalPolicyModifyState.InboundBlocked,
                _ => FirewallLocalPolicyModifyState.Unknown
            };
        }
        catch (Exception)
        {
            modifyState = FirewallLocalPolicyModifyState.Unknown;
        }

        return new FirewallPolicyState(profiles, modifyState, ReadInboundRules(policy));
    }

    private static IReadOnlyList<FirewallRuleInfo> ReadInboundRules(dynamic policy)
    {
        const int directionInbound = 1;
        const int actionAllow = 1;
        var rules = new List<FirewallRuleInfo>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if ((int)rule.Direction != directionInbound)
                {
                    continue;
                }

                rules.Add(new FirewallRuleInfo(
                    (string)(rule.Name ?? ""),
                    (bool)rule.Enabled,
                    (int)rule.Action == actionAllow,
                    (int)rule.Protocol,
                    rule.LocalPorts as string,
                    rule.ApplicationName as string,
                    rule.ServiceName as string,
                    (int)rule.Profiles,
                    rule.RemoteAddresses as string));
            }
            catch (Exception)
            {
                // Skip rules whose properties cannot be read (e.g. protocol-specific accessors).
            }
        }

        return rules;
    }
}
