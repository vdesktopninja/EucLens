namespace EucLens.App.Presentation;

/// <summary>Sizing rules of the findings / evidence split. All values are DIPs.</summary>
public readonly record struct SplitOptions(double SplitWidth = 900, double WideFraction = 0.55, double StackedFraction = 0.45,
    double MinMasterWidth = 520, double MinDetailWidth = 360, double MinMasterHeight = 160, double MinDetailHeight = 320, double Thickness = 6)
{
    /// <summary>The default options. Use this, not <c>new SplitOptions()</c>: for a record struct that expression zero-initializes and ignores the defaults above.</summary>
    public static SplitOptions Default { get; } = new(900);
}

/// <summary>Where the master pane, splitter and detail pane go for a given size.</summary>
public readonly record struct SplitResult(bool SideBySide, double MasterWidth, double MasterHeight, double DetailX, double DetailY,
    double DetailWidth, double DetailHeight, double SplitterX, double SplitterY, double SplitterWidth, double SplitterHeight, double MasterExtent, double Total);

/// <summary>
/// Pure layout rules (no WPF types) for <see cref="ResponsiveSplitPanel"/>: side by side when there is room for both
/// panes at their minimum size, stacked otherwise, and a user-adjustable split that can never shrink either pane below
/// its minimum. The detail pane therefore always keeps a readable size.
/// </summary>
public static class SplitLayout
{
    /// <summary>Height of a region meant to fill the visible page: the viewport minus a reserve, never below a minimum.</summary>
    public static double RegionHeight(double viewportHeight, double reserve, double minimum) =>
        Math.Max(minimum, (double.IsFinite(viewportHeight) ? viewportHeight : 0) - reserve);

    public static bool IsSideBySide(double width, SplitOptions o) => !double.IsInfinity(width) && width >= Math.Max(o.SplitWidth, o.MinMasterWidth + o.MinDetailWidth + o.Thickness);

    public static SplitResult Compute(double width, double height, SplitOptions o)
    {
        var t = o.Thickness;
        if (IsSideBySide(width, o))
        {
            var total = width - t;
            var master = Clamp(total * o.WideFraction, o.MinMasterWidth, total - o.MinDetailWidth);
            return new(true, master, height, master + t, 0, total - master, height, master, 0, t, height, master, total);
        }
        var stackedTotal = height - t;
        // Both minimums apply whenever they fit. Only a region too short for both shrinks them proportionally,
        // so neither pane vanishes.
        double minMaster = o.MinMasterHeight, minDetail = o.MinDetailHeight;
        if (stackedTotal < minMaster + minDetail)
        {
            var scale = Math.Max(stackedTotal, 0) / (minMaster + minDetail);
            minMaster *= scale; minDetail *= scale;
        }
        var masterHeight = Clamp(stackedTotal * o.StackedFraction, minMaster, stackedTotal - minDetail);
        return new(false, width, masterHeight, 0, masterHeight + t, width, stackedTotal - masterHeight, 0, masterHeight, width, t, masterHeight, stackedTotal);
    }

    /// <summary>The fraction that results from moving the splitter by <paramref name="delta"/> DIPs (positive = master grows).</summary>
    public static double FractionAfterMove(double width, double height, SplitOptions o, double delta)
    {
        var r = Compute(width, height, o);
        if (r.Total <= 0) return r.SideBySide ? o.WideFraction : o.StackedFraction;
        var target = r.MasterExtent + delta;
        var next = target / r.Total;
        // Re-clamp through Compute so the stored fraction always maps to a legal layout.
        var probe = r.SideBySide ? o with { WideFraction = next } : o with { StackedFraction = next };
        var legal = Compute(width, height, probe);
        return legal.MasterExtent / legal.Total;
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Min(Math.Max(value, min), max);
}
