using DataService.Infrastructure.Firewall;

namespace DataService.Core.Tests.Infrastructure;

public sealed class FirewallPolicyEvaluatorTests
{
    private const string ExecutablePath = @"C:\Apps\ProtoHydra\app.exe";

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
        string? serviceName = null,
        string? localAppPackageId = null,
        string? localUserOwner = null)
        => new(
            "test-rule",
            enabled,
            isAllow,
            protocol,
            localPorts,
            applicationName,
            serviceName,
            profiles,
            remoteAddresses,
            localAppPackageId,
            localUserOwner);

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

    // Regression: on a stock Windows 11 machine the Store/AppContainer rules Windows creates
    // for packaged apps ("MSTeams_<pkg><userSid>-In-Allow-ServerCapability", Microsoft Store,
    // Game Bar, ChatGPT, ...) carry no ApplicationName, no ServiceName, protocol Any and no
    // port restriction. Treated as generic port rules they matched every probe, so the UI
    // reported "Firewall allows all checked ports" (green) although no rule allowed this
    // executable at all and the default inbound action was Block.
    [Theory]
    [InlineData("S-1-15-2-1239072475-3687740317-1842194888-1639165560-3849322199-3011300420-1", null)]
    [InlineData(null, "S-1-12-1-2289391400-1092131886-1257453956-1094209902")]
    [InlineData(null, "S-1-5-18")]
    public void Evaluate_PrincipalScopedWildcardRule_DoesNotOpenPortsForThisProcess(
        string? localAppPackageId,
        string? localUserOwner)
    {
        var policy = Policy(
            [Profile(FirewallProfile.Public)],
            rules: AllowRule(
                profiles: (int)FirewallProfile.Public,
                applicationName: null,
                localAppPackageId: localAppPackageId,
                localUserOwner: localUserOwner));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Red, snapshot.Level);
        Assert.False(snapshot.Ports[0].IsAllowed);
        Assert.False(snapshot.IsApplicationAllowed);
    }

    [Fact]
    public void Evaluate_PrincipalScopedBlockRule_DoesNotBlockThisProcess()
    {
        var policy = Policy(
            [Profile(FirewallProfile.Private)],
            rules:
            [
                AllowRule(profiles: (int)FirewallProfile.Private),
                AllowRule(
                    profiles: (int)FirewallProfile.Private,
                    applicationName: null,
                    isAllow: false,
                    localUserOwner: "S-1-5-18")
            ]);

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target()], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
        Assert.True(snapshot.Ports[0].IsAllowed);
    }

    [Fact]
    public void Evaluate_GenuinelyGenericPortRule_StillOpensPort()
    {
        // Guard against over-correcting: an admin-created "any program on TCP 8080"
        // rule has no principal scope and must keep counting as open.
        var policy = Policy(
            [Profile(FirewallProfile.Public)],
            rules: AllowRule(
                profiles: (int)FirewallProfile.Public,
                protocol: FirewallRuleInfo.ProtocolTcp,
                localPorts: "8080",
                applicationName: null));

        var snapshot = FirewallPolicyEvaluator.Evaluate(ExecutablePath, [Target(port: 8080)], policy);

        Assert.Equal(FirewallStatusLevel.Green, snapshot.Level);
        Assert.True(snapshot.Ports[0].IsAllowed);
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
