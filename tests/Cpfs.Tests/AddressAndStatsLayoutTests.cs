using Cpfs.Core.Connect;
using Cpfs.Core.Layout;
using Xunit;

namespace Cpfs.Tests;

public class AddressAndStatsLayoutTests
{
    [Fact]
    public void TheAddressSplitsOnlyAfterBackslashes()
    {
        Assert.Equal(new[] { @"\\", @"CyClone-Laptop.local\", "Shared-Server" }, UncPieces.Split(@"\\CyClone-Laptop.local\Shared-Server"));
        Assert.Equal(new[] { @"\\", @"host\", @"a\", "b" }, UncPieces.Split(@"\\host\a\b"));
        Assert.Empty(UncPieces.Split(""));
        Assert.Equal(new[] { "plain" }, UncPieces.Split("plain"));
    }

    // MonitorPage.xaml: MinItemEms 8 at a 14 px font plus 34 px of padding and border, gap 8, four cards, even rows.
    [Theory]
    [InlineData(860, 4)]
    [InlineData(640, 4)]
    [InlineData(600, 2)]
    [InlineData(340, 2)]
    [InlineData(290, 1)]
    public void TheStatsGoFourAcrossFromAbout150PxACard(double width, int expected)
    {
        const double min = (8 * 14) + 34;
        Assert.Equal(expected, ColumnLayout.EvenColumns(ColumnLayout.Columns(width, min, 8, 4), 4));
    }
}
