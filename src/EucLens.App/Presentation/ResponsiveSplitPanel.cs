using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace EucLens.App.Presentation;

/// <summary>
/// Master pane, splitter thumb and detail pane (in that order). Side by side when wide, stacked when narrow, with a
/// draggable and keyboard-adjustable split. Sizes come from <see cref="SplitLayout"/>; it takes a finite height from its
/// parent so each pane can scroll on its own.
/// </summary>
public sealed class ResponsiveSplitPanel : Panel
{
    public static readonly DependencyProperty SplitWidthProperty = Register(nameof(SplitWidth), 900d, v => v > 0);
    public static readonly DependencyProperty WideFractionProperty = Register(nameof(WideFraction), 0.55d, v => v is > 0.1 and < 0.9);
    public static readonly DependencyProperty StackedFractionProperty = Register(nameof(StackedFraction), 0.45d, v => v is > 0.1 and < 0.9);
    public static readonly DependencyProperty MinMasterWidthProperty = Register(nameof(MinMasterWidth), 520d, v => v > 0);
    public static readonly DependencyProperty MinDetailWidthProperty = Register(nameof(MinDetailWidth), 360d, v => v > 0);
    public static readonly DependencyProperty MinMasterHeightProperty = Register(nameof(MinMasterHeight), 160d, v => v > 0);
    public static readonly DependencyProperty MinDetailHeightProperty = Register(nameof(MinDetailHeight), 320d, v => v > 0);
    public static readonly DependencyProperty SplitterThicknessProperty = Register(nameof(SplitterThickness), 6d, v => v > 0);
    private static DependencyProperty Register(string name, double fallback, Func<double, bool> valid) => DependencyProperty.Register(name, typeof(double), typeof(ResponsiveSplitPanel),
        new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && valid(d));
    public double SplitWidth { get => (double)GetValue(SplitWidthProperty); set => SetValue(SplitWidthProperty, value); }
    public double WideFraction { get => (double)GetValue(WideFractionProperty); set => SetValue(WideFractionProperty, value); }
    public double StackedFraction { get => (double)GetValue(StackedFractionProperty); set => SetValue(StackedFractionProperty, value); }
    public double MinMasterWidth { get => (double)GetValue(MinMasterWidthProperty); set => SetValue(MinMasterWidthProperty, value); }
    public double MinDetailWidth { get => (double)GetValue(MinDetailWidthProperty); set => SetValue(MinDetailWidthProperty, value); }
    public double MinMasterHeight { get => (double)GetValue(MinMasterHeightProperty); set => SetValue(MinMasterHeightProperty, value); }
    public double MinDetailHeight { get => (double)GetValue(MinDetailHeightProperty); set => SetValue(MinDetailHeightProperty, value); }
    public double SplitterThickness { get => (double)GetValue(SplitterThicknessProperty); set => SetValue(SplitterThicknessProperty, value); }
    /// <summary>True when the last layout placed the detail pane beside the master pane.</summary>
    public bool IsSideBySide { get; private set; }
    private const double KeyStep = 24;
    private Size lastSize;

    private SplitOptions Options => new(SplitWidth, WideFraction, StackedFraction, MinMasterWidth, MinDetailWidth, MinMasterHeight, MinDetailHeight, SplitterThickness);

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is Thumb removed) { removed.DragDelta -= Dragged; removed.KeyDown -= KeyPressed; }
        if (visualAdded is Thumb added) { added.DragDelta += Dragged; added.KeyDown += KeyPressed; }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 3) throw new InvalidOperationException("ResponsiveSplitPanel needs a master, a splitter thumb and a detail element.");
        // Without a finite height the panes could not scroll independently; fall back to a usable height.
        var height = double.IsInfinity(availableSize.Height) ? Math.Max(MinHeight, MinMasterHeight + MinDetailHeight + SplitterThickness) : availableSize.Height;
        var width = double.IsInfinity(availableSize.Width) ? MinMasterWidth + MinDetailWidth + SplitterThickness : availableSize.Width;
        lastSize = new Size(width, height);
        var r = SplitLayout.Compute(width, height, Options);
        IsSideBySide = r.SideBySide;
        InternalChildren[0].Measure(new Size(r.MasterWidth, r.MasterHeight));
        InternalChildren[1].Measure(new Size(r.SplitterWidth, r.SplitterHeight));
        InternalChildren[2].Measure(new Size(r.DetailWidth, r.DetailHeight));
        if (InternalChildren[1] is FrameworkElement thumb)
        {
            thumb.Cursor = r.SideBySide ? Cursors.SizeWE : Cursors.SizeNS;
            thumb.ToolTip = r.SideBySide ? "Drag or use the arrow keys to resize the findings and evidence panes" : "Drag or use the up and down arrow keys to resize the findings and evidence panes";
        }
        return lastSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var r = SplitLayout.Compute(finalSize.Width, finalSize.Height, Options);
        IsSideBySide = r.SideBySide;
        InternalChildren[0].Arrange(new Rect(0, 0, r.MasterWidth, r.MasterHeight));
        InternalChildren[1].Arrange(new Rect(r.SplitterX, r.SplitterY, r.SplitterWidth, r.SplitterHeight));
        InternalChildren[2].Arrange(new Rect(r.DetailX, r.DetailY, r.DetailWidth, r.DetailHeight));
        return finalSize;
    }

    private void Move(double delta)
    {
        var next = SplitLayout.FractionAfterMove(lastSize.Width, lastSize.Height, Options, delta);
        if (IsSideBySide) WideFraction = Math.Clamp(next, 0.11, 0.89); else StackedFraction = Math.Clamp(next, 0.11, 0.89);
    }

    private void Dragged(object sender, DragDeltaEventArgs e) => Move(IsSideBySide ? e.HorizontalChange : e.VerticalChange);

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        var grow = IsSideBySide ? Key.Right : Key.Down;
        var shrink = IsSideBySide ? Key.Left : Key.Up;
        if (e.Key == grow) Move(KeyStep);
        else if (e.Key == shrink) Move(-KeyStep);
        else return;
        e.Handled = true;
    }
}
