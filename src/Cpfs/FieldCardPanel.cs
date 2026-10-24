using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Cpfs.Core.Layout;

namespace Cpfs;

// A settings card's title (first child) and its field (second child). The field has one width on every card, set in
// ems so it grows with the font, and sits at the card's far edge beside the title; when the card is too narrow for
// both it goes below the title at full width. So the fields of a page line up in one column at any window size.
// With FieldAutoWidth the field takes its own width instead (a column of buttons), and below the title it keeps that
// width at the start edge. Right-to-left needs nothing here: WPF mirrors the arranged layout.
public sealed class FieldCardPanel : Panel
{
    public static readonly DependencyProperty FieldAutoWidthProperty = DependencyProperty.Register(
        nameof(FieldAutoWidth), typeof(bool), typeof(FieldCardPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FieldEmsProperty = DependencyProperty.Register(
        nameof(FieldEms), typeof(double), typeof(FieldCardPanel), new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinHeaderEmsProperty = DependencyProperty.Register(
        nameof(MinHeaderEms), typeof(double), typeof(FieldCardPanel), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(FieldCardPanel), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double SideGap = 16;
    private const double StackGap = 8;

    private bool _side = true;
    private double _fieldWidth;

    public bool FieldAutoWidth
    {
        get => (bool)GetValue(FieldAutoWidthProperty);
        set => SetValue(FieldAutoWidthProperty, value);
    }

    public double FieldEms
    {
        get => (double)GetValue(FieldEmsProperty);
        set => SetValue(FieldEmsProperty, value);
    }

    public double MinHeaderEms
    {
        get => (double)GetValue(MinHeaderEmsProperty);
        set => SetValue(MinHeaderEmsProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (header, field) = Parts();
        var bounded = !double.IsInfinity(availableSize.Width);
        if (FieldAutoWidth)
        {
            field?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _fieldWidth = WidthOf(field);
            if (_fieldWidth <= 0)
            {
                // A collapsed field: the title takes the whole width, with no gap kept for it.
                _side = false;
                header?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                return new Size(bounded ? availableSize.Width : WidthOf(header), HeightOf(header));
            }
        }
        else
        {
            _fieldWidth = FieldEms * FontSize;
        }

        _side = FieldLayout.SideBySide(availableSize.Width, _fieldWidth, MinHeaderEms * FontSize, SideGap);
        if (_side)
        {
            var headerWidth = bounded ? Math.Max(0, availableSize.Width - _fieldWidth - SideGap) : double.PositiveInfinity;
            header?.Measure(new Size(headerWidth, double.PositiveInfinity));
            field?.Measure(new Size(_fieldWidth, double.PositiveInfinity));
            var height = Math.Max(HeightOf(header), HeightOf(field));
            return new Size(bounded ? availableSize.Width : WidthOf(header) + SideGap + _fieldWidth, height);
        }

        header?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        field?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(availableSize.Width, HeightOf(header) + StackGap + HeightOf(field));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (header, field) = Parts();
        if (_side)
        {
            var fieldWidth = Math.Min(_fieldWidth, finalSize.Width);
            var headerWidth = Math.Max(0, finalSize.Width - fieldWidth - SideGap);
            header?.Arrange(new Rect(0, (finalSize.Height - HeightOf(header)) / 2, headerWidth, HeightOf(header)));
            field?.Arrange(new Rect(finalSize.Width - fieldWidth, (finalSize.Height - HeightOf(field)) / 2, fieldWidth, HeightOf(field)));
        }
        else
        {
            header?.Arrange(new Rect(0, 0, finalSize.Width, HeightOf(header)));
            var fieldWidth = FieldAutoWidth ? Math.Min(_fieldWidth, finalSize.Width) : finalSize.Width;
            var top = FieldAutoWidth && _fieldWidth <= 0 ? HeightOf(header) : HeightOf(header) + StackGap;
            field?.Arrange(new Rect(0, top, fieldWidth, HeightOf(field)));
        }

        return finalSize;
    }

    private (UIElement? Header, UIElement? Field) Parts()
    {
        var children = InternalChildren;
        return (children.Count > 0 ? children[0] : null, children.Count > 1 ? children[1] : null);
    }

    private static double HeightOf(UIElement? e) => e?.DesiredSize.Height ?? 0;

    private static double WidthOf(UIElement? e) => e?.DesiredSize.Width ?? 0;
}
