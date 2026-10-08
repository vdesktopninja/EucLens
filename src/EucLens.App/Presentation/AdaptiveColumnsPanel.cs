using System.Windows;
using System.Windows.Controls;

namespace EucLens.App.Presentation;

/// <summary>Equal-width technical panes that reflow in DIPs without scaling text.</summary>
public sealed class AdaptiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(AdaptiveColumnsPanel),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(AdaptiveColumnsPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count > 0);
    public double MinimumColumnWidth { get => (double)GetValue(MinimumColumnWidthProperty); set => SetValue(MinimumColumnWidthProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    private int Columns(double width) => double.IsInfinity(width) ? MaximumColumns : Math.Max(1, Math.Min(MaximumColumns, (int)(width / MinimumColumnWidth)));
    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var width = double.IsInfinity(availableSize.Width) ? MinimumColumnWidth : availableSize.Width / columns;
        double height = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == InternalChildren.Count - 1) { height += rowHeight; rowHeight = 0; }
        }
        return new Size(double.IsInfinity(availableSize.Width) ? columns * width : availableSize.Width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = finalSize.Width / columns;
        double y = 0;
        for (var start = 0; start < InternalChildren.Count; start += columns)
        {
            var count = Math.Min(columns, InternalChildren.Count - start);
            var height = Enumerable.Range(start, count).Max(i => InternalChildren[i].DesiredSize.Height);
            for (var i = 0; i < count; i++) InternalChildren[start + i].Arrange(new Rect(i * width, y, width, height));
            y += height;
        }
        return finalSize;
    }
}
