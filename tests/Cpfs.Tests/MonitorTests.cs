using System;
using Cpfs.Core.Monitor;
using Xunit;

namespace Cpfs.Tests;

public class MonitorTests
{
    // Shape of a 4625 record as EventRecord.ToXml() returns it; values are made up.
    private const string Event4625 = """
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
          <System>
            <Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-a5ba-3e3b0328c30d}'/>
            <EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task>
            <TimeCreated SystemTime='2026-10-23T15:02:38.9930000Z'/>
            <EventRecordID>123456</EventRecordID>
            <Channel>Security</Channel><Computer>HOST-01</Computer>
          </System>
          <EventData>
            <Data Name='SubjectUserSid'>S-1-0-0</Data>
            <Data Name='SubjectUserName'>-</Data>
            <Data Name='TargetUserSid'>S-1-0-0</Data>
            <Data Name='TargetUserName'>cpfsuser</Data>
            <Data Name='TargetDomainName'>HOST-01</Data>
            <Data Name='Status'>0xc000006d</Data>
            <Data Name='FailureReason'>%%2313</Data>
            <Data Name='SubStatus'>0xc000006a</Data>
            <Data Name='LogonType'>3</Data>
            <Data Name='WorkstationName'>PHONE</Data>
            <Data Name='IpAddress'>192.168.1.20</Data>
            <Data Name='IpPort'>0</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void ReadsA4625EventByDataName()
    {
        var e = FailedLogon.Parse(Event4625);
        Assert.Equal(123456, e.RecordId);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 15, 2, 38, 993, TimeSpan.Zero), e.Time);
        Assert.Equal("cpfsuser", e.User);
        Assert.Equal("HOST-01", e.Domain);
        Assert.Equal("192.168.1.20", e.Source);
        Assert.Equal("PHONE", e.Workstation);
        Assert.Equal(3, e.LogonType);
        Assert.Equal("0xC000006D", e.Status);
        Assert.Equal("0xC000006A", e.ReasonCode);
        Assert.Equal("FailReason_C000006A", e.ReasonKey);
        Assert.Equal("record 123456, time 2026-10-23 15:02:38Z, user cpfsuser, reason 0xC000006A (status 0xC000006D)", e.LogText);
    }

    [Fact]
    public void StatusIsTheReasonWhenTheSubstatusIsEmpty()
    {
        var e = FailedLogon.Parse(Event4625.Replace("0xc000006a", "0x0", StringComparison.Ordinal).Replace("0xc000006d", "0xc0000234", StringComparison.Ordinal));
        Assert.Equal("0xC0000234", e.ReasonCode);
        Assert.Equal("FailReason_C0000234", e.ReasonKey);
    }

    [Fact]
    public void UnknownCodesHaveNoReasonKeyAndDashesAreEmpty()
    {
        var e = FailedLogon.Parse(Event4625.Replace("0xc000006a", "0xc0001234", StringComparison.Ordinal).Replace(">192.168.1.20<", ">-<", StringComparison.Ordinal));
        Assert.Equal("0xC0001234", e.ReasonCode);
        Assert.Null(e.ReasonKey);
        Assert.Null(e.Source);
    }

    [Fact]
    public void BrokenXmlIsAFormatException() => Assert.Throws<FormatException>(() => FailedLogon.Parse("<Event"));

    [Fact]
    public void RatesSkipACounterReset()
    {
        Assert.Equal(512, MonitorMath.Rate(1000, 2024, 2));
        Assert.Null(MonitorMath.Rate(5000, 10, 2));
        Assert.Null(MonitorMath.Rate(null, 10, 2));
        Assert.Null(MonitorMath.Rate(1, 10, 0));
    }

    [Theory]
    [InlineData(@"\\*\Shared", "Shared")]
    [InlineData("Family-Files", "Family-Files")]
    [InlineData("_Total", null)]
    [InlineData(@"\\*\IPC$", null)]
    [InlineData(@"\\*\C$", null)]
    [InlineData("", null)]
    public void ShareInstanceNames(string instance, string? share) => Assert.Equal(share, MonitorMath.ShareFromInstance(instance));

    [Fact]
    public void ShareInstancesOfOneShareAreSummedAndTakeTheFilledCounter()
    {
        var totals = new System.Collections.Generic.Dictionary<string, (double Down, double Up)>(System.StringComparer.OrdinalIgnoreCase);
        MonitorMath.AddShareInstance(totals, @"\\*\Shared", sent: 1000, read: 0, received: null, written: 40);
        MonitorMath.AddShareInstance(totals, @"\\SCOPE\Shared", sent: 0, read: 500, received: 10, written: 0);
        MonitorMath.AddShareInstance(totals, "_Total", sent: 9999, read: 9999, received: 9999, written: 9999);

        Assert.Single(totals);
        Assert.Equal((1500d, 50d), totals["Shared"]);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", false)]
    [InlineData("[fd7a:115c:a1e0::9]", "fd7a:115c:a1e0::9", true)]
    [InlineData("::ffff:100.100.1.2", "100.100.1.2", true)]
    [InlineData("PHONE", "PHONE", false)]
    public void ClientAddresses(string reported, string shown, bool vpn)
    {
        var address = MonitorMath.ClientAddress(reported);
        Assert.Equal(shown, address);
        Assert.Equal(vpn, MonitorMath.IsVpnClient(address));
    }

    [Fact]
    public void BytesAndDurations()
    {
        Assert.Equal((1.5, 2), MonitorMath.ScaleBytes(1.5 * 1024 * 1024));
        Assert.Equal((0d, 0), MonitorMath.ScaleBytes(-5));
        Assert.Equal((1L, 5, 7), MonitorMath.SplitDuration(3907));
        Assert.Equal("cpfsuser", MonitorMath.UserName(@"HOST-01\cpfsuser"));
    }
}
