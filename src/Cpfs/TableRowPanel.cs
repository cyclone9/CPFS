using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Cpfs.Core.Layout;

namespace Cpfs;

// One row of a table (TableLayout): every child but the last is a column as wide as the widest cell of that column in
// every row under the nearest element with IsScope set, the header row included; the last child takes the rest and
// wraps, or goes below the row when too little is left. Unlike Grid's SharedSizeGroup the shared columns shrink, and
// their cells trim, when the window is too narrow for them. Right-to-left needs nothing here: WPF mirrors the layout.
public sealed class TableRowPanel : Panel
{
    public static readonly DependencyProperty IsScopeProperty = DependencyProperty.RegisterAttached(
        "IsScope", typeof(bool), typeof(TableRowPanel), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(TableRowPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinLastEmsProperty = DependencyProperty.Register(
        nameof(MinLastEms), typeof(double), typeof(TableRowPanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(TableRowPanel), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyProperty ScopeStateProperty = DependencyProperty.RegisterAttached(
        "ScopeState", typeof(Scope), typeof(TableRowPanel), new FrameworkPropertyMetadata(null));

    private const double BelowGap = 2;

    private Scope? _scope;
    private TableColumns _columns = new(Array.Empty<double>(), false);
    private double _rowHeight;

    public TableRowPanel()
    {
        Unloaded += (_, _) => Leave();

        // Back on screen (a cached page shown again): join the table again before the next layout.
        Loaded += (_, _) =>
        {
            if (_scope is null)
            {
                InvalidateMeasure();
            }
        };
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    // The least width the last column keeps beside the others, in ems of the font size.
    public double MinLastEms
    {
        get => (double)GetValue(MinLastEmsProperty);
        set => SetValue(MinLastEmsProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public static bool GetIsScope(DependencyObject element) => (bool)element.GetValue(IsScopeProperty);

    public static void SetIsScope(DependencyObject element, bool value) => element.SetValue(IsScopeProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var natural = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            natural[i] = children[i].DesiredSize.Width;
        }

        _scope ??= FindScope();
        var shared = _scope?.Report(this, natural) ?? natural;
        _columns = TableLayout.Arrange(shared, availableSize.Width, Gap, MinLastEms * FontSize);

        _rowHeight = 0;
        double lastHeight = 0;
        double width = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var w = _columns.Widths[i];
            children[i].Measure(new Size(w, double.PositiveInfinity));
            var h = children[i].DesiredSize.Height;
            if (_columns.LastBelow && i == children.Count - 1)
            {
                lastHeight = h;
            }
            else
            {
                _rowHeight = Math.Max(_rowHeight, h);
                width += w + (i > 0 ? Gap : 0);
            }
        }

        var height = _columns.LastBelow ? _rowHeight + BelowGap + lastHeight : _rowHeight;
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        double x = 0;
        for (var i = 0; i < children.Count && i < _columns.Widths.Count; i++)
        {
            var w = _columns.Widths[i];
            if (_columns.LastBelow && i == children.Count - 1)
            {
                children[i].Arrange(new Rect(0, _rowHeight + BelowGap, finalSize.Width, children[i].DesiredSize.Height));
                break;
            }

            children[i].Arrange(new Rect(x, 0, w, _rowHeight));
            x += w + Gap;
        }

        return finalSize;
    }

    private Scope? FindScope()
    {
        for (var e = VisualTreeHelper.GetParent(this); e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (GetIsScope(e))
            {
                if (e.GetValue(ScopeStateProperty) is not Scope scope)
                {
                    scope = new Scope();
                    e.SetValue(ScopeStateProperty, scope);
                }

                return scope;
            }
        }

        return null;
    }

    private void Leave()
    {
        _scope?.Remove(this);
        _scope = null;
    }

    // The natural widths every row of one table reported, and the widest per column; a row whose widths change the
    // widest makes the other rows measure again.
    private sealed class Scope
    {
        private readonly Dictionary<TableRowPanel, double[]> _rows = new();
        private double[] _shared = Array.Empty<double>();

        public double[] Report(TableRowPanel row, double[] natural)
        {
            _rows[row] = natural;
            Recompute(row);
            return _shared;
        }

        public void Remove(TableRowPanel row)
        {
            if (_rows.Remove(row))
            {
                Recompute(null);
            }
        }

        private void Recompute(TableRowPanel? caller)
        {
            var count = 0;
            foreach (var widths in _rows.Values)
            {
                count = Math.Max(count, widths.Length);
            }

            var shared = new double[count];
            foreach (var widths in _rows.Values)
            {
                for (var i = 0; i < widths.Length; i++)
                {
                    shared[i] = Math.Max(shared[i], widths[i]);
                }
            }

            if (Same(shared, _shared))
            {
                return;
            }

            _shared = shared;
            foreach (var other in _rows.Keys)
            {
                if (!ReferenceEquals(other, caller))
                {
                    other.InvalidateMeasure();
                }
            }
        }

        private static bool Same(double[] a, double[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i] - b[i]) > 0.5)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
