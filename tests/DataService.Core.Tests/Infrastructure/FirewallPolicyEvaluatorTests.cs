using DataService.Infrastructure.Firewall;

namespace DataService.Core.Tests.Infrastructure;

public sealed class FirewallPolicyEvaluatorTests
{
    private const string ExecutablePath = @"C:\Apps\FileHydra\app.exe";

    private static FirewallProfileState Profile(
        FirewallProfile profile,
        bool enabled = true,
        bool blockAllInbound = false,
        bool defaultInboundAllow = false)
        => new(profile, enabled, blockAllInbound, defaultInboundAllow);

    private static FirewallRuleInfo AllowRule(
        int profiles,
        int protocol = FirewallRuleInfo.ProtocolAny,
        string? localPorts = null,
        string? applicationName = ExecutablePath,
        bool enabled = true,
        bool isAllow = true,
        string? remoteAddresses = null,
        string? serviceName = null)
        => new("test-rule", enabled, isAllow, protocol, localPorts, applicationName, serviceName, profiles, remoteAddresses);

    private static FirewallProbeTarget Target(
        int port = 8080,
        FirewallTransportProtocol transport = FirewallTransportProtocol.Tcp,
        bool isLoopback = false,
        FirewallProfile? profile = null)
        => new(new FirewallPortProbe("HTTP", port, transport, "0.0.0.0"), isLoopback, profile);

    private static FirewallPolicyState Policy(
        IReadOnlyList<FirewallProfileState> profiles,
        FirewallLocalPolicyModifyState modifyState = FirewallLocalPolicyModifyState.Ok,
        params FirewallRuleInfo[] rules)
        => new(profiles, modifyState, rules);

    [Fact]
    public void Evaluate_MixedProfiles_ReportsPartialWithPerProfileDetail()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private), Profile(FirewallProfile.Public)],
            rules: AllowRule(profiles: (int)FirewallProfile.Private));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Yellow, snapshot.Level);
        Assert.Contains("Private: open", snapshot.Ports[0].Message);
        Assert.Contains("Public: blocked", snapshot.Ports[0].Message);
    }

    [Fact]
    public void Evaluate_ProbeBoundToSpecificProfile_UsesOnlyThatProfile()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private), Profile(FirewallProfile.Public)],
            rules: AllowRule(profiles: (int)FirewallProfile.Private));

        var snapshot = FirewallPolicyEvaluator.Evaluate(
            ExecutablePath,
            [Target(profile: FirewallProfile.Private)],
            policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
        Assert.True(snapshot.Ports[0].IsAllowed);
        Assert.NotEqual(true, snapshot.Ports[0].IsRestricted);
    }

    [Fact]
    public void Evaluate_BlockAllInbound_IgnoresAllowRulesAndWarns()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Public, blockAllInbound: true)],
            rules: AllowRule(profiles: (int)FirewallProfile.Public));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Red, snapshot.Level);
        Assert.False(snapshot.Ports[0].IsAllowed);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Block all incoming connections"));
        Assert.Contains("Temp-Fix", snapshot.Detail);
    }

    [Fact]
    public void Evaluate_GroupPolicyOverride_AddsWarningWithoutChangingLevel()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Domain)],
            FirewallLocalPolicyModifyState.GroupPolicyOverride,
            AllowRule(profiles: (int)FirewallProfile.Domain));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Group Policy overrides local firewall rules"));
    }

    [Fact]
    public void Evaluate_LoopbackOnlyListeners_AreNotFirewallRelevant()
    {
        var policy = Policy([Profile(FirewallProfile.Public)]);

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target(isLoopback: true)], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
        Assert.Equal("Firewall not relevant", snapshot.Summary);
        Assert.True(snapshot.Ports[0].IsAllowed);
    }

    [Fact]
    public void Evaluate_BlockRuleWinsOverAllowRule()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules:
            [
                AllowRule(profiles: (int)FirewallProfile.Private),
                AllowRule(profiles: (int)FirewallProfile.Private, isAllow: false, applicationName: null)
            ]);

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Red, snapshot.Level);
        Assert.False(snapshot.Ports[0].IsAllowed);
    }

    [Fact]
    public void Evaluate_PortListsAndRanges_AreMatched()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules: AllowRule(
                profiles: (int)FirewallProfile.Private,
                protocol: FirewallRuleInfo.ProtocolTcp,
                localPorts: "80,8000-8100",
                applicationName: null));

        var inRange = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target(port: 8080)], policy);
        var outOfRange = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target(port: 9000)], policy);

        Assert.Equal(FirewallStatusLevel.Green, inRange.Level);
        Assert.Equal(FirewallStatusLevel.Red, outOfRange.Level);
    }

    [Fact]
    public void Evaluate_RuleForOtherApplication_DoesNotApply()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules: AllowRule(profiles: (int)FirewallProfile.Private, applicationName: @"C:\Other\other.exe"));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Red, snapshot.Level);
    }

    [Fact]
    public void Evaluate_FirewallDisabledProfile_AllowsEverything()
    {
        var policy = Policy([Profile(FirewallProfile.Public, enabled: false)]);

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
    }

    [Fact]
    public void Evaluate_DefaultInboundAllow_AllowsWithoutRules()
    {
        var policy = Policy([Profile(FirewallProfile.Domain, defaultInboundAllow: true)]);

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
    }

    [Fact]
    public void Evaluate_AddressScopedAllowRule_IsReportedAsPartial()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules: AllowRule(profiles: (int)FirewallProfile.Private, remoteAddresses: "192.168.0.0/24"));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Yellow, snapshot.Level);
        Assert.True(snapshot.Ports[0].IsRestricted);
    }

    [Fact]
    public void Evaluate_ServiceScopedRule_DoesNotApplyToDesktopProcess()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules: AllowRule(profiles: (int)FirewallProfile.Private, applicationName: null, serviceName: "Spooler"));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Red, snapshot.Level);
    }

    [Fact]
    public void Evaluate_NoActiveProfiles_IsUnavailable()
    {
        var policy = Policy(Array.Empty<FirewallProfileState>());

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Yellow, snapshot.Level);
        Assert.Equal("Firewall status unavailable", snapshot.Summary);
    }
}
