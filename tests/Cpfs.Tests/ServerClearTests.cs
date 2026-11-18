using System.Linq;
using Cpfs.Core.Connections;
using Cpfs.Core.Elevation;
using Xunit;

namespace Cpfs.Tests;

public class ServerClearTests
{
    [Theory]
    [InlineData("MyPC", "MyPC", null)]
    [InlineData("  my-pc.local  ", "my-pc.local", null)]
    [InlineData(@"\\192.168.1.20", "192.168.1.20", null)]
    [InlineData(@"\\nas\Media", "nas", "Media")]
    [InlineData(@"\\nas\Media\", "nas", "Media")]
    [InlineData(@"\\fe80--1s4.ipv6-literal.net\IPC$", "fe80--1s4.ipv6-literal.net", "IPC$")]
    [InlineData("لپ‌تاپ", "لپ‌تاپ", null)]
    public void AcceptsAServerAndAnOptionalShare(string input, string server, string? share)
    {
        Assert.Equal(ServerClearError.None, ServerClear.Parse(input, out var request));
        Assert.Equal(server, request!.Server);
        Assert.Equal(share, request.Share);
    }

    [Theory]
    [InlineData(null, ServerClearError.Empty)]
    [InlineData("   ", ServerClearError.Empty)]
    [InlineData("X:", ServerClearError.Device)]
    [InlineData(@"X:\", ServerClearError.Device)]
    [InlineData("fe80::1", ServerClearError.Device)]
    [InlineData("*", ServerClearError.Wildcard)]
    [InlineData(@"\\nas\*", ServerClearError.Wildcard)]
    [InlineData(@"\\a\b\c", ServerClearError.Path)]
    [InlineData(@"nas\share", ServerClearError.Path)]
    [InlineData("//nas/share", ServerClearError.Path)]
    [InlineData(@"\\\nas", ServerClearError.Path)]
    [InlineData(@"\\sshfs\a", ServerClearError.NotSmb)]
    [InlineData(@"\\SSHFS.KR\me@host", ServerClearError.NotSmb)]
    [InlineData("-nas", ServerClearError.Server)]
    [InlineData("nas.", ServerClearError.Server)]
    [InlineData("na s", ServerClearError.Server)]
    [InlineData("300.1.1.1", ServerClearError.Server)]
    [InlineData(@"\\nas\..", ServerClearError.Share)]
    [InlineData(@"\\nas\a<b", ServerClearError.Share)]
    public void RefusesAnythingButAServerOrOneShare(string? input, ServerClearError error)
    {
        Assert.Equal(error, ServerClear.Parse(input, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void TargetsAreDevicelessNamesWithIpcLastAndNeverADrive()
    {
        Assert.Equal(ServerClearError.None, ServerClear.Parse(@"\\NAS\docs", out var request));
        var listed = new[]
        {
            new ClientConnection("nas", "media", "Z:", "u", ClientConnectionState.Connected, true, false, 0),
            new ClientConnection("nas", "photos", null, "u", ClientConnectionState.Connected, false, false, 0),
            new ClientConnection("nas", "IPC$", null, "u", ClientConnectionState.Connected, false, true, 0),
            new ClientConnection("other", "x", null, "u", ClientConnectionState.Connected, false, false, 0),
        };

        var targets = ServerClear.Targets(request!, listed);

        Assert.Equal(new[] { @"\\NAS\docs", @"\\NAS\photos", @"\\NAS\IPC$" }, targets.Select(t => t.Remote));
        Assert.True(targets[0].Typed);
        Assert.All(targets, t => Assert.StartsWith(@"\\", t.Remote, System.StringComparison.Ordinal));

        // A typed share that the list holds under a drive letter is named, not ended.
        Assert.Equal(ServerClearError.None, ServerClear.Parse(@"\\nas\media", out var onDrive));
        Assert.DoesNotContain(ServerClear.Targets(onDrive!, listed), t => t.Remote.EndsWith("media", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Z:" }, ServerClear.OnDrive(onDrive!, listed));
    }

    [Theory]
    [InlineData(0, ServerClearVerdict.Ended)]
    [InlineData(2250, ServerClearVerdict.NotHeld)]
    [InlineData(2401, ServerClearVerdict.OpenFiles)]
    [InlineData(2404, ServerClearVerdict.OpenFiles)]
    [InlineData(5, ServerClearVerdict.Failed)]
    public void JudgesWindowsCodes(int code, ServerClearVerdict verdict) => Assert.Equal(verdict, ServerClear.Judge(code));

    [Fact]
    public void ReopenCarriesOnlyTheFlagAndTheConnectionsPage()
    {
        var args = RelaunchArgs.BuildReopen();
        var parsed = RelaunchArgs.Parse(args.ToArray());

        Assert.Equal(new[] { "--reopened", "--page", "Connections" }, args);
        Assert.True(parsed.Reopened);
        Assert.False(parsed.Elevated);
        Assert.Equal(Pages.Connections, parsed.Page);
        Assert.Empty(parsed.Rejected);
    }
}
