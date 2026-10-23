using System;

namespace Cpfs.Core.Layout;

public static class ColumnLayout
{
    // As many columns as fit at the minimum item width, between 1 and maxColumns. An unmeasured or unbounded width
    // gives one column, so content is never squeezed below its minimum.
    public static int Columns(double available, double minItemWidth, double gap, int maxColumns)
    {
        if (maxColumns <= 1 || double.IsNaN(available) || double.IsInfinity(available) || available <= 0 || minItemWidth <= 0)
        {
            return 1;
        }

        var fit = (int)Math.Floor((available + gap) / (minItemWidth + gap));
        return Math.Max(1, Math.Min(maxColumns, fit));
    }

    public static double ItemWidth(double available, int columns, double gap) =>
        columns <= 1 ? Math.Max(0, available) : Math.Max(0, (available - gap * (columns - 1)) / columns);
}
