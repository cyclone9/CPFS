namespace Cpfs.Core.Layout;

public static class FieldLayout
{
    // A settings card puts its field beside the title only while the title keeps its minimum width; otherwise the
    // field goes below it. The answer depends on the card's width alone, so cards of one width switch together and
    // their fields keep one column. An unbounded width (measuring for content size) counts as wide.
    public static bool SideBySide(double available, double fieldWidth, double minHeaderWidth, double gap) =>
        double.IsNaN(available) || double.IsInfinity(available) || available >= fieldWidth + gap + minHeaderWidth;
}
