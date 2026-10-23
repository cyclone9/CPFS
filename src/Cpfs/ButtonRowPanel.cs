using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Layout;

namespace Cpfs;

// Dialog buttons in one row of equal columns when every label fits at its natural width, otherwise stacked at full
// width (labels wrap if even that is too narrow). Right-to-left needs nothing here: WPF mirrors the arranged layout.
public sealed class ButtonRowPanel : Panel
{
    private const double Gap = 8;

    private bool _row = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var widths = new List<double>(children.Count);
        foreach (UIElement child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths.Add(child.DesiredSize.Width);
        }

        _row = ButtonRow.FitsInRow(widths, availableSize.Width, Gap);
        if (_row)
        {
            var widest = 0.0;
            var tallest = 0.0;
            foreach (UIElement child in children)
            {
                widest = Math.Max(widest, child.DesiredSize.Width);
                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }

            var width = double.IsInfinity(availableSize.Width) ? (widest * children.Count) + (Gap * Math.Max(0, children.Count - 1)) : availableSize.Width;
            return new Size(width, tallest);
        }

        var height = 0.0;
        foreach (UIElement child in children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
        }

        height += Gap * Math.Max(0, children.Count - 1);
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (_row)
        {
            var width = ColumnLayout.ItemWidth(finalSize.Width, children.Count, Gap);
            for (var i = 0; i < children.Count; i++)
            {
                children[i].Arrange(new Rect(i * (width + Gap), 0, width, finalSize.Height));
            }

            return finalSize;
        }

        var y = 0.0;
        foreach (UIElement child in children)
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + Gap;
        }

        return finalSize;
    }
}
