using System.Windows;
using System.Windows.Controls;

namespace EucLens.App.Presentation;

/// <summary>
/// A list and its details pane (two children, in that order) that flow with the page: side by side on a wide page, the details under the list on a narrow one, the list alone while the
/// details pane is collapsed. Unlike a splitter panel it takes no fixed height: the page scrolls, and the height is that of the taller column (or both, stacked). The sizes come
/// from <see cref="MasterDetailLayout"/>.
/// </summary>
public sealed class MasterDetailPanel : Panel
{
    public static readonly DependencyProperty SideBySideWidthProperty = DependencyProperty.Register(nameof(SideBySideWidth), typeof(double), typeof(MasterDetailPanel),
        new FrameworkPropertyMetadata(MasterDetailOptions.Default.SideBySideWidth, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d > 0);
    public double SideBySideWidth { get => (double)GetValue(SideBySideWidthProperty); set => SetValue(SideBySideWidthProperty, value); }

    /// <summary>True when the last layout put the details beside the list.</summary>
    public bool IsSideBySide { get; private set; }

    private MasterDetailOptions Options => MasterDetailOptions.Default with { SideBySideWidth = SideBySideWidth };
    private bool HasDetail => InternalChildren.Count > 1 && InternalChildren[1].Visibility != Visibility.Collapsed;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) throw new InvalidOperationException("MasterDetailPanel needs a master and a detail element.");
        var width = double.IsInfinity(availableSize.Width) ? Options.MinMaster + Options.MinDetail + Options.Gap : availableSize.Width;
        var r = MasterDetailLayout.Compute(width, HasDetail, Options);
        IsSideBySide = r.SideBySide;
        var master = InternalChildren[0]; var detail = InternalChildren[1];
        master.Measure(new Size(r.MasterWidth, double.PositiveInfinity));
        if (!HasDetail) { detail.Measure(new Size(0, 0)); return new Size(width, master.DesiredSize.Height); }
        detail.Measure(new Size(r.DetailWidth, double.PositiveInfinity));
        var height = r.SideBySide ? Math.Max(master.DesiredSize.Height, detail.DesiredSize.Height) : master.DesiredSize.Height + r.Gap + detail.DesiredSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var r = MasterDetailLayout.Compute(finalSize.Width, HasDetail, Options);
        IsSideBySide = r.SideBySide;
        var master = InternalChildren[0]; var detail = InternalChildren[1];
        master.Arrange(new Rect(0, 0, r.MasterWidth, master.DesiredSize.Height));
        if (!HasDetail) return finalSize;
        if (r.SideBySide) detail.Arrange(new Rect(r.DetailX, 0, r.DetailWidth, detail.DesiredSize.Height));
        else detail.Arrange(new Rect(0, master.DesiredSize.Height + r.Gap, r.DetailWidth, detail.DesiredSize.Height));
        return finalSize;
    }
}
