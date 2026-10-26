using LiveChartsCore.Measure;

namespace EucLens.App.Presentation;

/// <summary>
/// The plot margins shared by every history chart and by the overlay drawn above them. A fixed margin makes every chart's plot area start and end at the same
/// pixel, so one time maps to one x position in all of them (the overlay uses the same left and right values).
/// </summary>
public static class HistoryLayout
{
    public const double PlotLeft = 64, PlotRight = 16, PlotTop = 6, PlotBottom = 22;
    /// <summary>Height of the event list's own viewport (DIPs): about nine rows. The list is virtualized, so its length no longer decides the height of the page.</summary>
    public const double EventListMaxHeight = 520;
    public static readonly Margin PlotMargin = new((float)PlotLeft, (float)PlotTop, (float)PlotRight, (float)PlotBottom);
}
