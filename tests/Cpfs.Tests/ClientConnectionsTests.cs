using System.Linq;
using System.Net;
using Cpfs.Core.Connect;
using Cpfs.Core.Connections;
using Xunit;

namespace Cpfs.Tests;

public class ClientConnectionsTests
{
    private const string LanMan = "Microsoft Windows Network";

    [Theory]
    [InlineData(@"\\nas\media", "nas", "media")]
    [InlineData(@"\\192.168.1.5\IPC$", "192.168.1.5", "IPC$")]
    [InlineData(@"\\host.local\share\sub\", "host.local", @"share\sub")]
    [InlineData(@"\\server", "server", "")]
    public void SplitsUncPaths(string remote, string server, string share)
    {
        var unc = ClientConnections.SplitUnc(remote);
        Assert.NotNull(unc);
        Assert.Equal(server, unc!.Value.Server);
        Assert.Equal(share, unc.Value.Share);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\folder")]
    [InlineData(@"\\")]
    public void RejectsNonUnc(string? remote) => Assert.Null(ClientConnections.SplitUnc(remote));

    [Theory]
    [InlineData(0u, ClientConnectionState.Connected)]
    [InlineData(2u, ClientConnectionState.Disconnected)]
    [InlineData(3u, ClientConnectionState.Disconnected)]
    [InlineData(5u, ClientConnectionState.Reconnecting)]
    [InlineData(99u, ClientConnectionState.Disconnected)]
    public void MapsUseStatus(uint status, ClientConnectionState state) => Assert.Equal(state, ClientConnections.StateOf(status));

    [Fact]
    public void DropsRememberedDrivesOfOtherProviders()
    {
        var uses = new[] { new SmbUse("Z:", @"\\nas\media", 0, 0, 0, "bob", "NAS") };

        // SSHFS-Win's X: is a UNC path too; only the provider tells it apart.
        Assert.False(ClientConnections.KeepRemembered(new RememberedMapping("X:", @"\\sshfs\me@server", "SSHFS-Win"), LanMan, uses));
        Assert.True(ClientConnections.KeepRemembered(new RememberedMapping("Y:", @"\\pc\docs", LanMan.ToUpperInvariant()), LanMan, uses));

        // Without the provider name, only a drive NetUseEnum also lists is kept.
        Assert.True(ClientConnections.KeepRemembered(new RememberedMapping("z:", @"\\NAS\media\", "Other"), null, uses));
        Assert.False(ClientConnections.KeepRemembered(new RememberedMapping("Y:", @"\\pc\docs", LanMan), null, uses));
        Assert.False(ClientConnections.KeepRemembered(new RememberedMapping("W:", "http://dav", LanMan), LanMan, uses));
    }

    [Fact]
    public void GroupsByServerAsHeldAndMarksStates()
    {
        var uses = new[]
        {
            new SmbUse(null, @"\\nas\IPC$", 0, SmbUse.TypeIpc, 0, "bob", "NAS"),
            new SmbUse("Z:", @"\\nas\media", 0, 0, 2, "bob", "NAS"),
            new SmbUse(null, @"\\NAS\photos", 2, 0, 0, "bob", null),
            new SmbUse(null, @"\\192.168.1.5\media", 0, 0, 0, "alice", "NAS"),
        };
        var remembered = new[]
        {
            new RememberedMapping("Z:", @"\\nas\media", LanMan),
            new RememberedMapping("Y:", @"\\nas.local\backup", LanMan),
            new RememberedMapping("X:", @"\\sshfs\me@server", "SSHFS-Win"),
        };

        var groups = ClientConnections.Build(uses, remembered, LanMan, s => s == "192.168.1.5");

        Assert.Equal(new[] { "192.168.1.5", "nas", "nas.local" }, groups.Select(g => g.Server).ToArray());
        Assert.Equal(new[] { true, false, false }, groups.Select(g => g.IsThisPc).ToArray());

        var nas = groups[1].Connections;
        Assert.Equal(new[] { "media", "photos", "IPC$" }, nas.Select(c => c.Share).ToArray());
        Assert.Equal("Z:", nas[0].DriveLetter);
        Assert.Equal(@"NAS\bob", nas[0].Account);
        Assert.True(nas[0].ReconnectsAtSignIn);
        Assert.Equal(2u, nas[0].OpenCount);
        Assert.Equal(ClientConnectionState.Disconnected, nas[1].State);
        Assert.Equal("bob", nas[1].Account);
        Assert.True(nas[2].IsSignIn);
        Assert.False(nas[2].ReconnectsAtSignIn);

        var saved = Assert.Single(groups[2].Connections);
        Assert.Equal(ClientConnectionState.SavedNotConnected, saved.State);
        Assert.Equal("Y:", saved.DriveLetter);
        Assert.Null(saved.Account);
        Assert.True(saved.ReconnectsAtSignIn);

        Assert.DoesNotContain(groups, g => g.Server == "sshfs");
    }

    [Theory]
    [InlineData("HOST-01", true)]
    [InlineData("host-01.local", true)]
    [InlineData("HOST01", true)]
    [InlineData("host-01.corp.example", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("host-01.example.ts.net", true)]
    [InlineData("192.168.1.11", false)]
    [InlineData("nas", false)]
    [InlineData("1", false)]
    public void RecognisesThisPc(string server, bool expected)
    {
        var names = new ThisPcNames("HOST-01", "HOST01", "corp.example", new[]
        {
            new AdapterAddress("Wi-Fi", IPAddress.Parse("192.168.1.10")),
            new AdapterAddress("Wi-Fi", IPAddress.Parse("fe80::1")),
            new AdapterAddress("Tailscale", IPAddress.Parse("100.101.102.103"), "example.ts.net"),
        });

        Assert.Equal(expected, names.Matches(server));
    }

    [Fact]
    public void ThisPcMatchIgnoresTurkishCasing()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var names = new ThisPcNames("office-pc", null, null, System.Array.Empty<AdapterAddress>());
            Assert.True(names.Matches("OFFICE-PC"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
