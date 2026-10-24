using System;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class DialogAndTimeTests
{
    [Theory]
    [InlineData(DialogClose.PrimaryButton, true)]
    [InlineData(DialogClose.SecondaryButton, false)]
    [InlineData(DialogClose.CloseButton, false)]
    [InlineData(DialogClose.Escape, false)]
    [InlineData(DialogClose.Window, false)]
    public void OnlyThePrimaryButtonConfirms(DialogClose close, bool confirms) => Assert.Equal(confirms, DialogCloses.Confirms(close));

    [Fact]
    public void CimDateTimeKeepsItsOffset()
    {
        // The owner's restore point: 01:51:04 local time in Iran, UTC+3:30.
        Assert.True(CimDateTime.TryParse("20261024015104.000000+210", out var t));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 1, 51, 4, TimeSpan.FromMinutes(210)), t);
        Assert.Equal(new DateTime(2026, 10, 23, 22, 21, 4), t.UtcDateTime);
        Assert.Equal("2026-10-24T01:51:04+03:30", CimDateTime.Iso(t));

        Assert.True(CimDateTime.TryParse("20260101120000.500000-300", out var west));
        Assert.Equal(TimeSpan.FromHours(-5), west.Offset);
        Assert.Equal(17, west.UtcDateTime.Hour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20261024015104.000000+***")]
    [InlineData("20261024******.000000+210")]
    [InlineData("2026-10-24 01:51:04:000")]
    [InlineData("20261324015104.000000+210")]
    public void CimDateTimeRefusesWhatIsNoPointInTime(string? value) => Assert.False(CimDateTime.TryParse(value, out _));
}
