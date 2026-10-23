using System;
using System.Collections.Generic;

namespace Cpfs.Core.Layout;

public static class ButtonRow
{
    // Dialog buttons share one row in equal columns only when every label fits its column at its natural width;
    // otherwise they stack, one per row, so no label is ever cut. An unbounded width always fits.
    public static bool FitsInRow(IReadOnlyList<double> naturalWidths, double available, double gap)
    {
        if (naturalWidths.Count <= 1 || double.IsInfinity(available))
        {
            return true;
        }

        var column = ColumnLayout.ItemWidth(available, naturalWidths.Count, gap);
        foreach (var w in naturalWidths)
        {
            if (w > column)
            {
                return false;
            }
        }

        return true;
    }
}
