using System;
using System.Collections.Generic;

namespace Cpfs.Core.Layout;

public sealed record TableColumns(IReadOnlyList<double> Widths, bool LastBelow);

public static class TableLayout
{
    // A table whose rows lay out one by one but must line up. Every column but the last is as wide as its widest cell
    // in the table (natural holds those shared widths); the last takes what is left and wraps. When that would leave
    // it narrower than minLast, it goes on a line of its own under the others at the full width, and when the other
    // columns alone don't fit they shrink in proportion (their cells trim). The answer depends only on the shared
    // widths and the available width, so every row of one table decides alike. An unbounded width keeps the natural
    // widths.
    public static TableColumns Arrange(IReadOnlyList<double> natural, double available, double gap, double minLast)
    {
        if (natural is null)
        {
            throw new ArgumentNullException(nameof(natural));
        }

        var n = natural.Count;
        var widths = new double[n];
        if (n == 0)
        {
            return new TableColumns(widths, false);
        }

        if (double.IsNaN(available) || double.IsInfinity(available))
        {
            for (var i = 0; i < n; i++)
            {
                widths[i] = natural[i];
            }

            return new TableColumns(widths, false);
        }

        available = Math.Max(0, available);
        if (n == 1)
        {
            widths[0] = available;
            return new TableColumns(widths, false);
        }

        var fixedTotal = 0.0;
        for (var i = 0; i < n - 1; i++)
        {
            fixedTotal += Math.Max(0, natural[i]);
        }

        var left = available - fixedTotal - gap * (n - 1);
        if (left >= minLast)
        {
            for (var i = 0; i < n - 1; i++)
            {
                widths[i] = Math.Max(0, natural[i]);
            }

            widths[n - 1] = left;
            return new TableColumns(widths, false);
        }

        var fixedGaps = gap * (n - 2);
        var factor = fixedTotal + fixedGaps <= available || fixedTotal <= 0 ? 1.0 : Math.Max(0, available - fixedGaps) / fixedTotal;
        for (var i = 0; i < n - 1; i++)
        {
            widths[i] = Math.Max(0, natural[i]) * factor;
        }

        widths[n - 1] = available;
        return new TableColumns(widths, true);
    }
}
