using System.Linq;
using System.Net;
using Cpfs.Core.Connect;
using Xunit;

namespace Cpfs.Tests;

public class ConnectTests
{
    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("::ffff:100.100.1.2", true)]
    public void VpnRanges(string address, bool vpn) => Assert.Equal(vpn, ConnectAddresses.IsVpn(IPAddress.Parse(address)));

    [Fact]
    public void AddressListFollowsTheScriptOrderAndSkipsUnreachableAddresses()
    {
        var list = ConnectAddresses.Build("HOST-01", new[]
        {
            new AdapterAddress("Wi-Fi", IPAddress.Parse("192.168.1.10")),
            new AdapterAddress("Wi-Fi", IPAddress.Parse("fe80::1")),
            new AdapterAddress("Ethernet", IPAddress.Parse("169.254.3.4")),
            new AdapterAddress("Loopback", IPAddress.Loopback),
            new AdapterAddress("Tailscale", IPAddress.Parse("100.101.102.103"), "example.ts.net"),
            new AdapterAddress("Tailscale", IPAddress.Parse("fd7a:115c:a1e0::5"), "example.ts.net"),
            new AdapterAddress("Ethernet 2", IPAddress.Parse("192.168.1.10")),
        });

        Assert.Equal(
            new[] { "HOST-01.local", "192.168.1.10", "100.101.102.103", "host-01", "host-01.example.ts.net" },
            list.Select(a => a.Host).ToArray());
        Assert.Equal(
            new[] { AddressKind.Mdns, AddressKind.Lan, AddressKind.Vpn, AddressKind.MagicDns, AddressKind.MagicDns },
            list.Select(a => a.Kind).ToArray());
        Assert.Equal("Wi-Fi", list[1].Adapter);
        Assert.False(list[0].ThroughVpn);
        Assert.True(list[2].ThroughVpn);
    }

    [Fact]
    public void NoVpnMeansNoMagicDnsName()
    {
        var list = ConnectAddresses.Build("pc", new[] { new AdapterAddress("Wi-Fi", IPAddress.Parse("10.0.0.2")) });
        Assert.DoesNotContain(list, a => a.ThroughVpn);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void ValuesCarryNoPasswordAndAddSealInStrictMode()
    {
        var v = new ConnectValues("HOST-01.local", "Shared", "cpfsuser", "HOST-01", strict: false);
        Assert.Equal(@"\\HOST-01.local\Shared", v.Unc);
        Assert.Equal("smb://HOST-01.local/Shared", v.SmbUrl);
        Assert.Equal("smb://cpfsuser@HOST-01.local/Shared", v.IosUrl);
        Assert.Equal(@"net use Z: \\HOST-01.local\Shared * /user:HOST-01\cpfsuser /persistent:yes", v.NetUse);
        Assert.Equal("sudo mount -t cifs //HOST-01.local/Shared /mnt/cpfs -o username=cpfsuser,domain=HOST-01,vers=3.0,uid=$(id -u),gid=$(id -g)", v.Mount);
        Assert.True(v.IsLocalName);
        Assert.DoesNotContain("password", v.Mount, System.StringComparison.OrdinalIgnoreCase);

        var strict = new ConnectValues("100.101.102.103", "Shared", "cpfsuser", "HOST-01", strict: true);
        Assert.Contains(",vers=3.0,seal,", strict.Mount, System.StringComparison.Ordinal);
        Assert.False(strict.IsLocalName);
    }

    [Fact]
    public void UrlsEscapeWhatAHandEditedConfigCouldHold()
    {
        var v = new ConnectValues("pc.local", "My Share", "a b", "PC", strict: false);
        Assert.Equal("smb://a%20b@pc.local/My%20Share", v.IosUrl);
        Assert.Equal("smb://pc.local/My%20Share", v.SmbUrl);
    }
}
