using Cpfs.Core.Layout;
using Xunit;

namespace Cpfs.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(700, 308, 4, 2, 2)]
    [InlineData(620, 308, 4, 2, 2)]
    [InlineData(619, 308, 4, 2, 1)]
    [InlineData(300, 308, 4, 2, 1)]
    [InlineData(2000, 308, 4, 2, 2)]
    [InlineData(double.PositiveInfinity, 308, 4, 2, 1)]
    [InlineData(0, 308, 4, 2, 1)]
    public void ColumnsFitTheMinimumWidth(double available, double min, double gap, int max, int expected) =>
        Assert.Equal(expected, ColumnLayout.Columns(available, min, gap, max));

    [Theory]
    [InlineData(700, 364, 168, 16, true)]
    [InlineData(548, 364, 168, 16, true)]
    [InlineData(547, 364, 168, 16, false)]
    [InlineData(240, 364, 168, 16, false)]
    [InlineData(double.PositiveInfinity, 364, 168, 16, true)]
    public void AFieldGoesBelowItsTitleWhenTheCardIsTooNarrow(double available, double field, double header, double gap, bool expected) =>
        Assert.Equal(expected, FieldLayout.SideBySide(available, field, header, gap));

    [Fact]
    public void ItemWidthSharesWhatIsLeftAfterTheGaps() =>
        Assert.Equal(348, ColumnLayout.ItemWidth(700, 2, 4));

    [Fact]
    public void ExpansionDefaultsToAttentionUntilTheUserChooses()
    {
        var store = new SectionExpansion();
        Assert.False(store.IsExpanded("User", needsAttention: false));
        Assert.True(store.IsExpanded("Tasks", needsAttention: true));

        store.Set("Tasks", false);
        Assert.False(store.IsExpanded("Tasks", needsAttention: true));

        store.SetAll(new[] { "User", "Tasks" }, true);
        Assert.True(store.IsExpanded("User", needsAttention: false));
        Assert.True(store.IsExpanded("Tasks", needsAttention: false));
    }

    [Fact]
    public void TableColumnsShareTheirWidthsAndTheLastTakesTheRest()
    {
        var wide = TableLayout.Arrange(new double[] { 130, 60, 120, 100 }, 600, 16, 140);
        Assert.False(wide.LastBelow);
        Assert.Equal(new double[] { 130, 60, 120, 600 - 310 - 48 }, wide.Widths);

        // Too little left for the last column: it goes under the row at full width, the others keep their widths.
        var narrow = TableLayout.Arrange(new double[] { 130, 60, 120, 100 }, 400, 16, 140);
        Assert.True(narrow.LastBelow);
        Assert.Equal(new double[] { 130, 60, 120, 400 }, narrow.Widths);

        // Even the first three don't fit: they shrink in proportion and fill the width with their gaps.
        var tiny = TableLayout.Arrange(new double[] { 130, 60, 120, 100 }, 250, 16, 140);
        Assert.True(tiny.LastBelow);
        Assert.Equal(250, tiny.Widths[0] + tiny.Widths[1] + tiny.Widths[2] + 32, 6);
        Assert.Equal(130 / 60.0, tiny.Widths[0] / tiny.Widths[1], 6);

        Assert.Equal(new double[] { 130, 60, 120, 100 }, TableLayout.Arrange(new double[] { 130, 60, 120, 100 }, double.PositiveInfinity, 16, 140).Widths);
    }
}
