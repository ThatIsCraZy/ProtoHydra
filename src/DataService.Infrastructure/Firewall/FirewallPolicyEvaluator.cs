using System.Globalization;

namespace DataService.Infrastructure.Firewall;

/// <summary>
/// Pure evaluation of Windows Firewall policy state against the configured listeners.
/// Evaluates every probe per active profile so multi-NIC systems with mixed profiles
/// get a consistent, explainable answer instead of a single global guess.
/// </summary>
public static class FirewallPolicyEvaluator
{
    public static FirewallStatusSnapshot Evaluate(
        string executablePath,
        IReadOnlyList<FirewallProbeTarget> targets,
        FirewallPolicyState policy)
    {
        if (policy.ActiveProfiles.Count == 0)
        {
            return FirewallStatusSnapshot.Unavailable("No firewall profile is currently active (no connected network).");
        }

        var warnings = BuildWarnings(policy);
        var results = new List<FirewallPortProbeResult>();
        var relevantResults = new List<FirewallPortProbeResult>();

        foreach (var target in targets)
        {
            var result = EvaluateTarget(executablePath, target, policy);
            results.Add(result);
            if (!target.IsLoopback)
            {
                relevantResults.Add(result);
            }
        }

        var isApplicationAllowed = policy.ActiveProfiles.All(profile =>
            IsApplicationWideAllowed(executablePath, profile, policy.InboundRules));

        return BuildSnapshot(policy, results, relevantResults, isApplicationAllowed, warnings);
    }

    private static FirewallStatusSnapshot BuildSnapshot(
        FirewallPolicyState policy,
        List<FirewallPortProbeResult> results,
        List<FirewallPortProbeResult> relevantResults,
        bool isApplicationAllowed,
        IReadOnlyList<string> warnings)
    {
        var profileNames = string.Join(", ", policy.ActiveProfiles.Select(profile => profile.Profile.ToString()));

        if (results.Count == 0)
        {
            return new FirewallStatusSnapshot(
                FirewallStatusLevel.Yellow,
                "Firewall status unavailable",
                AppendWarnings("No startable protocol ports are available for checking.", warnings),
                isApplicationAllowed,
                results,
                warnings);
        }

        if (relevantResults.Count == 0)
        {
            return new FirewallStatusSnapshot(
                FirewallStatusLevel.Green,
                "Firewall not relevant",
                AppendWarnings("All listeners bind to loopback addresses; Windows Firewall does not affect them.", warnings),
                isApplicationAllowed,
                results,
                warnings);
        }

        var known = relevantResults.Where(result => result.IsAllowed.HasValue).ToArray();
        if (known.Length == 0)
        {
            return new FirewallStatusSnapshot(
                FirewallStatusLevel.Yellow,
                "Firewall status unavailable",
                AppendWarnings("Windows Firewall did not return a usable port status.", warnings),
                isApplicationAllowed,
                results,
                warnings);
        }

        var open = known.Count(result => result.IsAllowed == true && result.IsRestricted != true);
        var partial = known.Count(result => result.IsAllowed == true && result.IsRestricted == true);
        var blocked = known.Count(result => result.IsAllowed == false);
        var counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{open} open, {partial} partial, {blocked} blocked · Active profiles: {profileNames}");
        var problems = known
            .Where(result => result.IsAllowed != true || result.IsRestricted == true)
            .Take(3)
            .Select(result => $"{result.Probe.Name} {result.Probe.Port}: {result.Message}");
        var detail = AppendWarnings(string.Join("\n", new[] { counts }.Concat(problems)), warnings);

        var level = blocked == 0 && partial == 0
            ? FirewallStatusLevel.Green
            : open + partial > 0
                ? FirewallStatusLevel.Yellow
                : FirewallStatusLevel.Red;
        var summary = level switch
        {
            FirewallStatusLevel.Green => "Firewall allows all checked ports",
            FirewallStatusLevel.Yellow => "Firewall partially allows ports",
            _ => "Firewall blocks all checked ports"
        };

        return new FirewallStatusSnapshot(level, summary, detail, isApplicationAllowed, results, warnings);
    }

    private static FirewallPortProbeResult EvaluateTarget(
        string executablePath,
        FirewallProbeTarget target,
        FirewallPolicyState policy)
    {
        if (target.IsLoopback)
        {
            return new FirewallPortProbeResult(
                target.Probe,
                IsAllowed: true,
                IsRestricted: false,
                "Loopback bind; not filtered by Windows Firewall.");
        }

        var profiles = target.Profile is { } specific
            ? policy.ActiveProfiles.Where(profile => profile.Profile == specific).ToArray()
            : [.. policy.ActiveProfiles];
        if (profiles.Length == 0)
        {
            // The interface's profile is not in the active set; fall back to all active profiles.
            profiles = [.. policy.ActiveProfiles];
        }

        var evaluations = profiles
            .Select(profile => (profile.Profile, Result: EvaluateForProfile(executablePath, target.Probe, profile, policy.InboundRules)))
            .ToArray();

        var allowedEverywhere = evaluations.All(evaluation => evaluation.Result.Allowed);
        var allowedAnywhere = evaluations.Any(evaluation => evaluation.Result.Allowed);
        var restricted = evaluations.Any(evaluation => evaluation.Result.Restricted) || (allowedAnywhere && !allowedEverywhere);
        var message = string.Join(" · ", evaluations.Select(evaluation =>
            $"{evaluation.Profile}: {(evaluation.Result.Allowed ? evaluation.Result.Restricted ? "restricted" : "open" : "blocked")}"));

        return new FirewallPortProbeResult(
            target.Probe,
            allowedAnywhere,
            allowedAnywhere && restricted,
            message);
    }

    private static (bool Allowed, bool Restricted) EvaluateForProfile(
        string executablePath,
        FirewallPortProbe probe,
        FirewallProfileState profile,
        IReadOnlyList<FirewallRuleInfo> rules)
    {
        if (!profile.FirewallEnabled)
        {
            return (true, false);
        }

        if (profile.BlockAllInbound)
        {
            // Shields-up mode: allow rules are ignored entirely.
            return (false, false);
        }

        var applicable = rules
            .Where(rule => rule.Enabled
                && AppliesToProfile(rule, profile.Profile)
                && AppliesToProbe(rule, probe, executablePath))
            .ToArray();

        if (applicable.Any(rule => !rule.IsAllow))
        {
            return (false, false);
        }

        var allow = applicable.FirstOrDefault(rule => rule.IsAllow);
        if (allow is not null)
        {
            return (true, IsAddressScoped(allow.RemoteAddresses));
        }

        return (profile.DefaultInboundAllow, false);
    }

    private static bool IsApplicationWideAllowed(
        string executablePath,
        FirewallProfileState profile,
        IReadOnlyList<FirewallRuleInfo> rules)
    {
        if (!profile.FirewallEnabled)
        {
            return true;
        }

        if (profile.BlockAllInbound)
        {
            return false;
        }

        return rules.Any(rule => rule.Enabled
            && rule.IsAllow
            && AppliesToProfile(rule, profile.Profile)
            && MatchesApplication(rule.ApplicationName, executablePath)
            && rule.Protocol == FirewallRuleInfo.ProtocolAny
            && IsAnyPort(rule.LocalPorts));
    }

    private static IReadOnlyList<string> BuildWarnings(FirewallPolicyState policy)
    {
        var warnings = new List<string>();
        foreach (var profile in policy.ActiveProfiles)
        {
            if (profile.FirewallEnabled && profile.BlockAllInbound)
            {
                warnings.Add(
                    $"{profile.Profile} profile: 'Block all incoming connections' is active — allow rules (including the Temp-Fix) are ignored.");
            }
        }

        if (warnings.Count == 0 && policy.ModifyState == FirewallLocalPolicyModifyState.InboundBlocked)
        {
            warnings.Add(
                "All inbound connections are blocked by policy — allow rules (including the Temp-Fix) are ignored.");
        }

        if (policy.ModifyState == FirewallLocalPolicyModifyState.GroupPolicyOverride)
        {
            warnings.Add(
                "Group Policy overrides local firewall rules — locally added rules (including the Temp-Fix) may have no effect.");
        }

        return warnings;
    }

    private static string AppendWarnings(string detail, IReadOnlyList<string> warnings)
        => warnings.Count == 0
            ? detail
            : detail + "\n" + string.Join("\n", warnings.Select(warning => "⚠ " + warning));

    private static bool AppliesToProfile(FirewallRuleInfo rule, FirewallProfile profile)
        => (rule.Profiles & (int)profile) != 0;

    private static bool AppliesToProbe(FirewallRuleInfo rule, FirewallPortProbe probe, string executablePath)
    {
        if (!ProtocolMatches(rule.Protocol, probe.TransportProtocol))
        {
            return false;
        }

        if (!CoversPort(rule.LocalPorts, probe.Port))
        {
            return false;
        }

        // Service-scoped rules never apply to this desktop process.
        if (!string.IsNullOrWhiteSpace(rule.ServiceName) && rule.ServiceName.Trim() != "*")
        {
            return false;
        }

        // Application-scoped rules only apply when they target this executable.
        if (!string.IsNullOrWhiteSpace(rule.ApplicationName) && !MatchesApplication(rule.ApplicationName, executablePath))
        {
            return false;
        }

        return true;
    }

    private static bool ProtocolMatches(int ruleProtocol, FirewallTransportProtocol transport)
        => ruleProtocol switch
        {
            FirewallRuleInfo.ProtocolAny => true,
            FirewallRuleInfo.ProtocolTcp => transport == FirewallTransportProtocol.Tcp,
            FirewallRuleInfo.ProtocolUdp => transport == FirewallTransportProtocol.Udp,
            _ => false
        };

    private static bool IsAnyPort(string? localPorts)
        => string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*";

    private static bool CoversPort(string? localPorts, int port)
    {
        if (IsAnyPort(localPorts))
        {
            return true;
        }

        foreach (var token in localPorts!.Split(','))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var range = trimmed.Split('-', 2);
            if (range.Length == 2
                && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low)
                && int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var high))
            {
                if (port >= low && port <= high)
                {
                    return true;
                }

                continue;
            }

            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAddressScoped(string? remoteAddresses)
        => !string.IsNullOrWhiteSpace(remoteAddresses)
            && remoteAddresses.Trim() is not ("*" or "any");

    private static bool MatchesApplication(string? ruleApplication, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(ruleApplication) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        return string.Equals(
            NormalizePath(Environment.ExpandEnvironmentVariables(ruleApplication.Trim())),
            NormalizePath(executablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
