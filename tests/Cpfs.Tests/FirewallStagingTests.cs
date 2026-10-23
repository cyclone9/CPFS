using System;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

public class FirewallStagingTests
{
    private static readonly RuleSpec Vpn = FirewallRules.Wanted(new[] { "Tailscale" })[2];

    private static FirewallRuleInfo AsWritten(bool enabled, int profiles = FirewallRules.ProfileAll, params string[] interfaces) =>
        new(FirewallRules.VpnRule, enabled, FirewallRules.Inbound, FirewallRules.Allow, FirewallRules.Tcp, "445", profiles,
            "100.64.0.0/255.192.0.0,fd7a:115c:a1e0::/48", interfaces.Length == 0 ? new[] { "Tailscale" } : interfaces);

    [Fact]
    public void ARuleThatReadsBackAsWrittenAndDisabledMayBeEnabled()
    {
        Assert.Empty(FirewallRules.StagedProblems(Vpn, new[] { AsWritten(false) }, enabled: false));
        Assert.Empty(FirewallRules.StagedProblems(Vpn, new[] { AsWritten(true) }, enabled: true));
    }

    [Fact]
    public void InterfacesWindowsDidNotTakeStopTheRuleBeforeItIsEnabled()
    {
        var unbound = AsWritten(false) with { Interfaces = Array.Empty<string>() };

        Assert.Equal(new[] { "Interfaces" }, FirewallRules.StagedProblems(Vpn, new[] { unbound }, enabled: false));
        Assert.Contains("Interfaces", FirewallRules.StagedProblems(Vpn, new[] { AsWritten(false, interfaces: "Wi-Fi") }, enabled: false));
    }

    [Fact]
    public void EveryFieldThatDiffersAndTheWrongEnabledStateAreProblems()
    {
        var wrong = AsWritten(true, FirewallRules.ProfilePublic) with
        {
            Direction = 2, Protocol = FirewallRules.Udp, LocalPorts = "139", RemoteAddresses = "*",
        };

        var problems = FirewallRules.StagedProblems(Vpn, new[] { wrong }, enabled: false);
        Assert.Equal(new[] { "Enabled", "Direction", "Protocol", "LocalPorts", "Profiles", "RemoteAddresses" }, problems);
        Assert.Equal(new[] { "Enabled" }, FirewallRules.StagedProblems(Vpn, new[] { AsWritten(false) }, enabled: true));
    }

    [Fact]
    public void AMissingOrDoubledRuleIsAProblem()
    {
        Assert.Equal(new[] { "Missing" }, FirewallRules.StagedProblems(Vpn, Array.Empty<FirewallRuleInfo>(), enabled: false));
        Assert.Equal(new[] { "Count" }, FirewallRules.StagedProblems(Vpn, new[] { AsWritten(false), AsWritten(false) }, enabled: false));
    }
}
