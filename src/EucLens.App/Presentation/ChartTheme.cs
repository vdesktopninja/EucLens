using System.Windows;
using System.Windows.Media;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace EucLens.App.Presentation;

/// <summary>Adapts semantic WPF resources to Skia paints without coupling the metrics model to WPF.</summary>
public static class ChartTheme
{
    public static readonly DependencyProperty TextProperty = Register("Text");
    public static readonly DependencyProperty SurfaceProperty = Register("Surface");
    public static readonly DependencyProperty GridProperty = Register("Grid");
    public static readonly DependencyProperty LineProperty = Register("Line");
    /// <summary>Colours of threshold reference lines; a section's Tag ("Warning" or "Critical") selects the brush.</summary>
    public static readonly DependencyProperty WarningProperty = Register("Warning");
    public static readonly DependencyProperty CriticalProperty = Register("Critical");
    private static DependencyProperty Register(string name) => DependencyProperty.RegisterAttached(name,
        typeof(Brush), typeof(ChartTheme), new PropertyMetadata(null, Changed));
    public static Brush? GetText(DependencyObject target) => (Brush?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, Brush value) => target.SetValue(TextProperty, value);
    public static Brush? GetSurface(DependencyObject target) => (Brush?)target.GetValue(SurfaceProperty);
    public static void SetSurface(DependencyObject target, Brush value) => target.SetValue(SurfaceProperty, value);
    public static Brush? GetGrid(DependencyObject target) => (Brush?)target.GetValue(GridProperty);
    public static void SetGrid(DependencyObject target, Brush value) => target.SetValue(GridProperty, value);
    public static Brush? GetLine(DependencyObject target) => (Brush?)target.GetValue(LineProperty);
    public static void SetLine(DependencyObject target, Brush value) => target.SetValue(LineProperty, value);
    public static Brush? GetWarning(DependencyObject target) => (Brush?)target.GetValue(WarningProperty);
    public static void SetWarning(DependencyObject target, Brush value) => target.SetValue(WarningProperty, value);
    public static Brush? GetCritical(DependencyObject target) => (Brush?)target.GetValue(CriticalProperty);
    public static void SetCritical(DependencyObject target, Brush value) => target.SetValue(CriticalProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not CartesianChart chart) return;
        chart.Loaded -= Loaded;
        chart.Loaded += Loaded;
        if (chart.IsLoaded) Apply(chart);
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Apply((CartesianChart)sender);
    /// <summary>Re-applies theme paints after a chart's series, axes or sections were replaced.</summary>
    public static void Refresh(CartesianChart chart) { if (chart.IsLoaded) Apply(chart); }
    private static SolidColorPaint Paint(Brush brush, float thickness = 1)
    {
        var color = ((SolidColorBrush)brush).Color;
        return new SolidColorPaint(new SKColor(color.R, color.G, color.B, color.A), thickness);
    }
    private static void Apply(CartesianChart chart)
    {
        if (GetText(chart) is not SolidColorBrush text || GetSurface(chart) is not SolidColorBrush surface ||
            GetGrid(chart) is not SolidColorBrush grid || GetLine(chart) is not SolidColorBrush line) return;
        chart.TooltipTextPaint = Paint(text);
        chart.TooltipBackgroundPaint = Paint(surface);
        chart.TooltipTextSize = 12;
        foreach (var axis in chart.XAxes.OfType<Axis>())
        {
            axis.LabelsPaint = Paint(text); axis.NamePaint = Paint(text); axis.SeparatorsPaint = null;
        }
        foreach (var axis in chart.YAxes.OfType<Axis>())
        {
            axis.LabelsPaint = Paint(text); axis.NamePaint = Paint(text); axis.SeparatorsPaint = Paint(grid);
        }
        foreach (var series in chart.Series.OfType<LineSeries<DateTimePoint>>())
        {
            series.Stroke = Paint(line, 2); series.Fill = null;
        }
        foreach (var section in chart.Sections?.OfType<RectangularSection>() ?? [])
        {
            if ((Equals(section.Tag, "Critical") ? GetCritical(chart) : GetWarning(chart)) is not SolidColorBrush level) continue;
            var stroke = Paint(level);
            stroke.PathEffect = new DashEffect([4f, 3f], 0);
            section.Stroke = stroke; section.Fill = null; section.LabelPaint = Paint(level);
        }
    }
}
