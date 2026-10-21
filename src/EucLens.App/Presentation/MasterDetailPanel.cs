using System.Windows;
using System.Windows.Controls;

namespace EucLens.App.Presentation;

/// <summary>
/// List beside its detail when there is room, detail below the list otherwise. Widths are DIPs, so the switch point
/// follows Windows scaling; text is never scaled to fit.
/// </summary>
public sealed class MasterDetailPanel : Panel
{
    public static readonly DependencyProperty SplitWidthProperty = DependencyProperty.Register(
        nameof(SplitWidth), typeof(double), typeof(MasterDetailPanel),
        new FrameworkPropertyMetadata(900d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);
    public static readonly DependencyProperty MasterFractionProperty = DependencyProperty.Register(
        nameof(MasterFraction), typeof(double), typeof(MasterDetailPanel),
        new FrameworkPropertyMetadata(0.6d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double fraction && fraction is > 0.2 and < 0.8);
    public double SplitWidth { get => (double)GetValue(SplitWidthProperty); set => SetValue(SplitWidthProperty, value); }
    public double MasterFraction { get => (double)GetValue(MasterFractionProperty); set => SetValue(MasterFractionProperty, value); }
    /// <summary>True when the last layout placed the detail beside the list.</summary>
    public bool IsSideBySide { get; private set; }

    private bool SideBySide(double width) => InternalChildren.Count == 2 && !double.IsInfinity(width) && width >= SplitWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        IsSideBySide = SideBySide(availableSize.Width);
        if (IsSideBySide)
        {
            var master = availableSize.Width * MasterFraction;
            InternalChildren[0].Measure(new Size(master, double.PositiveInfinity));
            InternalChildren[1].Measure(new Size(availableSize.Width - master, double.PositiveInfinity));
            return new Size(availableSize.Width, Math.Max(InternalChildren[0].DesiredSize.Height, InternalChildren[1].DesiredSize.Height));
        }
        double height = 0, width = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
            width = Math.Max(width, child.DesiredSize.Width);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (SideBySide(finalSize.Width))
        {
            // Each pane keeps its own height: a short findings list does not stretch into empty space.
            var master = finalSize.Width * MasterFraction;
            InternalChildren[0].Arrange(new Rect(0, 0, master, InternalChildren[0].DesiredSize.Height));
            InternalChildren[1].Arrange(new Rect(master, 0, finalSize.Width - master, InternalChildren[1].DesiredSize.Height));
            return finalSize;
        }
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
