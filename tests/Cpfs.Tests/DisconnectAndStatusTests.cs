using System;
using System.Collections.Generic;
using System.Linq;
using Cpfs.Core.Connections;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

public class DisconnectAndStatusTests
{
    private static ClientConnection Row(string server, string share, string? drive, bool signIn = false,
        ClientConnectionState state = ClientConnectionState.Connected) =>
        new(server, share, drive, "PC\\user", state, drive is not null, signIn, 0);

    [Fact]
    public void ADriveIsCancelledByItsLetterAndForgotten()
    {
        var t = ClientDisconnect.TargetFor(Row("nas", "media", "P:"));
        Assert.Equal("P:", t.Name);
        Assert.True(t.UpdateProfile);
    }

    [Fact]
    public void ADevicelessConnectionIsCancelledByItsRemoteNameWithoutTheFlag()
    {
        var t = ClientDisconnect.TargetFor(Row("192.168.1.20", "IPC$", null, signIn: true));
        Assert.Equal(@"\\192.168.1.20\IPC$", t.Name);
        Assert.False(t.UpdateProfile);
    }

    [Theory]
    [InlineData(0, true, DisconnectVerdict.Done)]
    [InlineData(2250, true, DisconnectVerdict.Done)]
    [InlineData(2401, false, DisconnectVerdict.OpenFiles)]
    [InlineData(2404, false, DisconnectVerdict.OpenFiles)]
    [InlineData(0, false, DisconnectVerdict.StillListed)]
    [InlineData(1206, false, DisconnectVerdict.Failed)]
    [InlineData(2250, false, DisconnectVerdict.Failed)]
    public void SuccessIsJudgedByTheListReadAgain(int code, bool gone, DisconnectVerdict verdict) =>
        Assert.Equal(verdict, ClientDisconnect.Judge(code, gone));

    [Fact]
    public void GoneMeansNoRowWithTheSameServerShareAndLetter()
    {
        var drive = Row("nas", "media", "P:");
        var after = new[] { new ClientConnectionGroup("NAS", false, new[] { Row("NAS", "MEDIA", null) }) };
        Assert.True(ClientDisconnect.IsGone(drive, after));
        Assert.False(ClientDisconnect.IsGone(Row("nas", "media", null), after));
        Assert.True(ClientDisconnect.IsGone(drive, Array.Empty<ClientConnectionGroup>()));
    }

    [Fact]
    public void DisconnectAllGoesDrivesFirstSignInLastAndOnceEach()
    {
        var group = new ClientConnectionGroup("nas", false, new[]
        {
            Row("nas", "IPC$", null, signIn: true),
            Row("nas", "docs", null),
            Row("nas", "docs", null),
            Row("nas", "media", "P:"),
        });
        var all = ClientDisconnect.AllFor(group).Select(x => x.Target.Name).ToList();
        Assert.Equal(new[] { "P:", @"\\nas\docs", @"\\nas\IPC$" }, all);
    }

    [Theory]
    [InlineData(new[] { 1 }, "Connections_Count_One")]
    [InlineData(new[] { 3 }, "Connections_Count_OneServer")]
    [InlineData(new[] { 2, 1 }, "Connections_Count_Many")]
    public void CountLinePicksTheSentence(int[] perServer, string key)
    {
        var groups = perServer.Select((n, i) => new ClientConnectionGroup("s" + i, false,
            Enumerable.Range(0, n).Select(j => Row("s" + i, "x" + j, null)).ToList())).ToList();
        Assert.Equal(key, ClientDisconnect.CountLine(groups).Key);
    }

    private static readonly IReadOnlyList<VpnAdapterInfo> NoVpn = Array.Empty<VpnAdapterInfo>();

    private static Reading<IReadOnlyList<ShareInfo>> Shares(params ShareInfo[] shares) => Reading.Ok<IReadOnlyList<ShareInfo>>(shares);

    private static Reading<IReadOnlyList<NetworkInfo>> Nets(params NetworkInfo[] nets) => Reading.Ok<IReadOnlyList<NetworkInfo>>(nets);

    private static ShareInfo Share(string name, bool? encrypt = false) => new(name, @"D:\Shared", null, encrypt, 0, 0, false);

    private static FirewallRuleInfo Lan(bool enabled = true, int profiles = FirewallRules.ProfilePrivate) =>
        new(FirewallRules.LanRule, enabled, 1, 1, 6, "445", profiles, "LocalSubnet", Array.Empty<string>());

    [Fact]
    public void NoConfigIsTheWelcome()
    {
        var v = SharingStatus.Decide(false, null, "PC", Shares(), true, Nets(), NoVpn, Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(Array.Empty<FirewallRuleInfo>()));
        Assert.Equal(SharingState.NotSetUp, v.State);
        Assert.Empty(v.Facts);
        Assert.Null(v.Address);
    }

    [Fact]
    public void OnOnlyWhenShareAndServiceWereBothRead()
    {
        var rules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[] { Lan() });
        var on = SharingStatus.Decide(true, "Shared", "MyLaptop", Shares(Share("shared", true)), true, Nets(new NetworkInfo("Home", "Wi-Fi", 1)), NoVpn, rules);
        Assert.Equal(SharingState.On, on.State);
        Assert.Equal(@"\\MyLaptop.local\Shared", on.Address);
        Assert.Equal(new[] { "Status_Net_Private", "Status_Encrypt_On", "Status_Firewall_Private" }, on.Facts.Select(f => f.Key));

        var unknown = SharingStatus.Decide(true, "Shared", "MyLaptop", Reading.NeedsAdmin<IReadOnlyList<ShareInfo>>(), true,
            Reading.Failed<IReadOnlyList<NetworkInfo>>("x"), NoVpn, Reading.NeedsAdmin<IReadOnlyList<FirewallRuleInfo>>());
        Assert.Equal(SharingState.Unknown, unknown.State);
        Assert.Null(unknown.Address);
        Assert.Empty(unknown.Facts);

        Assert.Equal(SharingState.Unknown, SharingStatus.Decide(true, "Shared", "PC", Shares(Share("Shared")), null, Nets(), NoVpn, rules).State);
    }

    [Fact]
    public void AMissingShareOrAStoppedServiceIsNotRunning()
    {
        var rules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[] { Lan(enabled: false) });
        var missing = SharingStatus.Decide(true, "Shared", "PC", Shares(Share("Other")), true, Nets(), NoVpn, rules);
        Assert.Equal(SharingState.NotRunning, missing.State);
        Assert.Contains(missing.Facts, f => f.Key == "Status_Firewall_Changed" && f.Tone == StatusTone.Caution);
        Assert.Contains(missing.Facts, f => f.Key == "Status_Net_None");

        var stopped = SharingStatus.Decide(true, "Shared", "PC", Reading.NeedsAdmin<IReadOnlyList<ShareInfo>>(), false, Nets(), NoVpn, rules);
        Assert.Equal(SharingState.NotRunning, stopped.State);
    }

    [Fact]
    public void APublicNetworkIsACautionButAVpnAdapterIsNotCounted()
    {
        var rules = Reading.NeedsAdmin<IReadOnlyList<FirewallRuleInfo>>();
        var vpn = new[] { new VpnAdapterInfo("tailscale0", "100.64.0.7") };
        var withVpn = SharingStatus.Decide(true, "S", "PC", Shares(Share("S")), true,
            Nets(new NetworkInfo("Home", "Wi-Fi", 1), new NetworkInfo("ts", "tailscale0", 0)), vpn, rules);
        Assert.Equal(new[] { "Status_Net_Private", "Status_Encrypt_Off" }, withVpn.Facts.Select(f => f.Key));

        var cafe = SharingStatus.Decide(true, "S", "PC", Shares(Share("S", null)), true, Nets(new NetworkInfo("Cafe", "Wi-Fi", 0)), NoVpn, rules);
        Assert.Equal(new[] { "Status_Net_Public" }, cafe.Facts.Select(f => f.Key));
        Assert.Equal(StatusTone.Caution, cafe.Facts[0].Tone);
    }
}
