using System;
using Cpfs.Core.Monitor;
using Cpfs.Core.Validation;
using Xunit;

namespace Cpfs.Tests;

public class DeviceNamesTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(5u, 5u)]
    [InlineData(0, 0u)]
    [InlineData(2L, 2u)]
    [InlineData("0", 0u)]
    public void ReadsAWmiReturnValue(object value, uint expected) => Assert.Equal(expected, MonitorMath.ReturnCode(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("done")]
    [InlineData(-1L)]
    public void AMissingOrOddReturnValueIsNoCode(object? value) => Assert.Null(MonitorMath.ReturnCode(value));

    [Theory]
    [InlineData(null, true, DisconnectVerdict.Done)]
    [InlineData(null, false, DisconnectVerdict.StillConnected)]
    [InlineData(0u, true, DisconnectVerdict.Done)]
    [InlineData(0u, false, DisconnectVerdict.StillConnected)]
    [InlineData(5u, true, DisconnectVerdict.Failed)]
    [InlineData(5u, false, DisconnectVerdict.Failed)]
    public void ADisconnectIsJudgedByItsCodeOrTheSessionList(uint? code, bool gone, DisconnectVerdict expected)
    {
        var checks = 0;
        Assert.Equal(expected, MonitorMath.JudgeDisconnect(code, () =>
        {
            checks++;
            return gone;
        }));
        Assert.Equal(code is > 0 ? 0 : 1, checks);
    }

    private static byte[] Row(ushort family, byte[] address, byte[] mac, uint macLength = 6, int state = 5)
    {
        var row = new byte[DeviceIdentity.NeighborRowSize];
        BitConverter.GetBytes(family).CopyTo(row, 0);
        address.CopyTo(row, family == 2 ? 4 : 8);
        mac.CopyTo(row, 40);
        BitConverter.GetBytes(macLength).CopyTo(row, 72);
        BitConverter.GetBytes(state).CopyTo(row, 76);
        return row;
    }

    [Fact]
    public void ParsesAnIpv4NeighborRow()
    {
        var row = Row(2, new byte[] { 192, 168, 1, 20 }, new byte[] { 0x5a, 0x0b, 0x1c, 0x2d, 0x3e, 0x4f });
        Assert.True(DeviceIdentity.TryParseNeighborRow(row, out var address, out var mac));
        Assert.Equal("192.168.1.20", address);
        Assert.Equal("5A-0B-1C-2D-3E-4F", mac);
    }

    [Fact]
    public void ParsesAnIpv6NeighborRow()
    {
        var ip = System.Net.IPAddress.Parse("fd00::1234").GetAddressBytes();
        Assert.True(DeviceIdentity.TryParseNeighborRow(Row(23, ip, new byte[] { 2, 0, 0, 0, 0, 1 }), out var address, out var mac));
        Assert.Equal("fd00::1234", address);
        Assert.Equal("02-00-00-00-00-01", mac);
    }

    [Fact]
    public void SkipsRowsWithoutAUsableMac()
    {
        var ip = new byte[] { 192, 168, 1, 20 };
        var mac = new byte[] { 0x5a, 0x0b, 0x1c, 0x2d, 0x3e, 0x4f };
        Assert.False(DeviceIdentity.TryParseNeighborRow(Row(2, ip, mac, state: 1), out _, out _));
        Assert.False(DeviceIdentity.TryParseNeighborRow(Row(2, ip, mac, macLength: 0), out _, out _));
        Assert.False(DeviceIdentity.TryParseNeighborRow(Row(2, ip, new byte[6]), out _, out _));
        Assert.False(DeviceIdentity.TryParseNeighborRow(Row(2, new byte[] { 224, 0, 0, 251 }, new byte[] { 1, 0, 0x5e, 0, 0, 0xfb }), out _, out _));
        Assert.False(DeviceIdentity.TryParseNeighborRow(Row(99, ip, mac), out _, out _));
    }

    [Fact]
    public void KeysByMacWhenThereIsOneAndByAddressOtherwise()
    {
        Assert.Equal("mac:5A-0B-1C-2D-3E-4F", DeviceIdentity.Key("5A-0B-1C-2D-3E-4F", "192.168.1.20"));
        Assert.Equal("ip:100.64.0.7", DeviceIdentity.Key(null, "100.64.0.7"));
        Assert.Equal("ip:fe80::1", DeviceIdentity.Key(null, "[fe80::1%12]"));
        Assert.Equal("ip:192.168.1.20", DeviceIdentity.Key(null, "::ffff:192.168.1.20"));
    }

    [Theory]
    [InlineData("Galaxy-A17.lan.", "192.168.1.20", "Galaxy-A17.lan")]
    [InlineData("192.168.1.20", "192.168.1.20", null)]
    [InlineData("fe80::1", "192.168.1.20", null)]
    [InlineData("  ", "192.168.1.20", null)]
    [InlineData(null, "192.168.1.20", null)]
    public void CleansAReverseLookupAnswer(string? name, string address, string? expected) =>
        Assert.Equal(expected, DeviceIdentity.CleanHostName(name, address));

    [Theory]
    [InlineData("  iPhone 13 ", ValidationError.None)]
    [InlineData("   ", ValidationError.Empty)]
    [InlineData("Phone\u0007", ValidationError.ControlCharacters)]
    public void ValidatesADeviceName(string input, ValidationError expected) =>
        Assert.Equal(expected, Validators.DeviceName(input).Error);

    [Fact]
    public void ADeviceNameIsAtMost64Characters()
    {
        Assert.True(Validators.DeviceName(new string('a', 64)).IsValid);
        Assert.Equal(ValidationError.TooLong, Validators.DeviceName(new string('a', 65)).Error);
    }

    [Fact]
    public void TheStoreRoundTripsAndDropsInvalidEntries()
    {
        var store = new DeviceNameStore();
        Assert.True(store.Set("mac:5A-0B-1C-2D-3E-4F", " آیفون سارا "));
        Assert.True(store.Set("ip:100.64.0.7", "Laptop"));
        Assert.False(store.Set("ip:100.64.0.8", ""));

        var back = DeviceNameStore.Parse(store.Serialize());
        Assert.Equal(2, back.Count);
        Assert.Equal("آیفون سارا", back.Get("mac:5a-0b-1c-2d-3e-4f"));
        Assert.True(back.Remove("ip:100.64.0.7"));
        Assert.Null(back.Get("ip:100.64.0.7"));

        var mixed = DeviceNameStore.Parse("""{"devices":{"key":"ip:1.2.3.4","name":"One"},"x":1}""");
        Assert.Equal("One", mixed.Get("ip:1.2.3.4"));
        Assert.Equal(0, DeviceNameStore.Parse("not json").Count);
        Assert.Equal(0, DeviceNameStore.Parse("""{"Devices":[{"Key":"ip:1.2.3.4","Name":"\u0001"}]}""").Count);
    }
}
