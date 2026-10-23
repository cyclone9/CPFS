using Cpfs.Core.Layout;
using Xunit;

namespace Cpfs.Tests;

public class ButtonRowTests
{
    [Theory]
    [InlineData(450, true)]
    [InlineData(300, false)]
    [InlineData(double.PositiveInfinity, true)]
    public void ButtonsStackWhenALabelWouldNotFitItsColumn(double available, bool row)
    {
        // Three labels at their natural widths; columns are (available - 2 gaps of 8) / 3.
        Assert.Equal(row, ButtonRow.FitsInRow(new[] { 140.0, 120.0, 60.0 }, available, 8));
    }
}
