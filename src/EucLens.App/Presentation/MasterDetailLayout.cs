namespace EucLens.App.Presentation;

/// <summary>Sizing rules of a master list with a details pane. All values are DIPs.</summary>
public readonly record struct MasterDetailOptions(double SideBySideWidth, double MasterFraction, double MinMaster, double MaxMaster, double MinDetail, double Gap, double StackedGap)
{
    /// <summary>The defaults of the findings workspace: two columns from 900 DIPs, the list about 46 % wide but never narrower than 400 or wider than 680, the details at least 400.</summary>
    public static MasterDetailOptions Default { get; } = new(900, 0.46, 400, 680, 400, 16, 12);
}

/// <summary>Where the list and the details go: side by side (list left) or stacked (details under the list).</summary>
public readonly record struct MasterDetailResult(bool SideBySide, double MasterWidth, double DetailWidth, double DetailX, double Gap);

/// <summary>
/// Pure layout rules (no WPF types) for <see cref="MasterDetailPanel"/>. Side by side when both columns fit at their minimum width and the page is at least
/// <see cref="MasterDetailOptions.SideBySideWidth"/> wide; stacked otherwise. Without a details pane the list takes the whole width.
/// </summary>
public static class MasterDetailLayout
{
    public static MasterDetailResult Compute(double width, bool hasDetail, MasterDetailOptions o)
    {
        if (!hasDetail || double.IsInfinity(width) || double.IsNaN(width)) return new(false, width, 0, 0, 0);
        var needed = Math.Max(o.SideBySideWidth, o.MinMaster + o.MinDetail + o.Gap);
        if (width < needed) return new(false, width, width, 0, o.StackedGap);
        var total = width - o.Gap;
        var master = Math.Clamp(total * o.MasterFraction, o.MinMaster, Math.Max(o.MinMaster, Math.Min(o.MaxMaster, total - o.MinDetail)));
        return new(true, master, total - master, master + o.Gap, o.Gap);
    }
}
