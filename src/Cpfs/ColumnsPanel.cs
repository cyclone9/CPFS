using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Cpfs.Core.Layout;

namespace Cpfs;

// Lays its children out in rows of up to MaxColumns equal columns, as many as fit at the minimum item width; the
// items of one row get the height of the tallest. The minimum is in ems of the inherited font size, so it grows with
// a larger font or another font choice. Right-to-left needs nothing here: WPF mirrors the arranged layout.
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty MinItemEmsProperty = DependencyProperty.Register(
        nameof(MinItemEms), typeof(double), typeof(ColumnsPanel), new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemChromeProperty = DependencyProperty.Register(
        nameof(ItemChrome), typeof(double), typeof(ColumnsPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ColumnsPanel), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ColumnsPanel), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(ColumnsPanel), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly List<double> _rowHeights = new();
    private int _columns = 1;
    private int _loggedColumns;

    // Width of the readable content of one item, in ems of the font size.
    public double MinItemEms
    {
        get => (double)GetValue(MinItemEmsProperty);
        set => SetValue(MinItemEmsProperty, value);
    }

    // Padding and borders of one item, in device-independent pixels, added to the content minimum.
    public double ItemChrome
    {
        get => (double)GetValue(ItemChromeProperty);
        set => SetValue(ItemChromeProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public double MinItemWidth => (MinItemEms * FontSize) + ItemChrome;

    // When set, each change of the column count goes to the log under this name.
    public string? LogName { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var bounded = !double.IsInfinity(availableSize.Width);
        _columns = bounded ? ColumnLayout.Columns(availableSize.Width, MinItemWidth, Gap, MaxColumns) : 1;
        var itemWidth = bounded ? ColumnLayout.ItemWidth(availableSize.Width, _columns, Gap) : double.PositiveInfinity;
        if (LogName is not null && _columns != _loggedColumns)
        {
            _loggedColumns = _columns;
            AppLog.Write("layout", LogName + ": " + _columns.ToString(CultureInfo.InvariantCulture) + " column(s) at width "
                + (bounded ? availableSize.Width.ToString("F0", CultureInfo.InvariantCulture) : "unbounded") + ", minimum item width "
                + MinItemWidth.ToString("F0", CultureInfo.InvariantCulture) + " (font size " + FontSize.ToString("F1", CultureInfo.InvariantCulture) + ")");
        }

        _rowHeights.Clear();
        var widest = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            var row = i / _columns;
            if (row == _rowHeights.Count)
            {
                _rowHeights.Add(0);
            }

            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
        }

        var height = 0.0;
        foreach (var h in _rowHeights)
        {
            height += h;
        }

        height += Gap * Math.Max(0, _rowHeights.Count - 1);
        return new Size(bounded ? availableSize.Width : widest, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var itemWidth = ColumnLayout.ItemWidth(finalSize.Width, _columns, Gap);
        var y = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var row = i / _columns;
            var column = i % _columns;
            if (column == 0 && row > 0)
            {
                y += _rowHeights[row - 1] + Gap;
            }

            var height = row < _rowHeights.Count ? _rowHeights[row] : children[i].DesiredSize.Height;
            children[i].Arrange(new Rect(column * (itemWidth + Gap), y, itemWidth, height));
        }

        return finalSize;
    }
}
