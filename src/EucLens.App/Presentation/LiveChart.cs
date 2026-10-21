using System.Windows;
using EucLens.App.ViewModels;
using LiveChartsCore.SkiaSharpView.WPF;

namespace EucLens.App.Presentation;

/// <summary>
/// Gives a chart view its own LiveCharts objects for a view model's <see cref="ChartData"/>. Values are set from code,
/// so they also take precedence over the chart's constructor defaults inside templates.
/// </summary>
public static class LiveChart
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached("Data", typeof(ChartData), typeof(LiveChart),
        new PropertyMetadata(null, DataChanged));
    private static readonly DependencyProperty ObjectsProperty = DependencyProperty.RegisterAttached("Objects", typeof(ChartObjects), typeof(LiveChart));
    public static ChartData? GetData(DependencyObject target) => (ChartData?)target.GetValue(DataProperty);
    public static void SetData(DependencyObject target, ChartData? value) => target.SetValue(DataProperty, value);
    /// <summary>The chart's own objects, for validation.</summary>
    public static ChartObjects? GetObjects(DependencyObject target) => (ChartObjects?)target.GetValue(ObjectsProperty);

    private static void DataChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not CartesianChart chart) return;
        chart.Loaded -= Loaded;
        if (args.NewValue is not ChartData data)
        {
            chart.ClearValue(ObjectsProperty);
            chart.Series = []; chart.XAxes = []; chart.YAxes = []; chart.Sections = [];
            return;
        }
        var objects = new ChartObjects(data);
        chart.SetValue(ObjectsProperty, objects);
        chart.Series = objects.Series; chart.XAxes = objects.XAxes; chart.YAxes = objects.YAxes; chart.Sections = objects.Sections;
        // The handler references only the chart that owns these objects.
        objects.SectionsReplaced += () => { chart.Sections = objects.Sections; ChartTheme.Refresh(chart); };
        chart.Loaded += Loaded;
        ChartTheme.Refresh(chart);
    }

    /// <summary>Unloaded is not necessarily final: a chart shown again resyncs with the data it missed.</summary>
    private static void Loaded(object sender, RoutedEventArgs args) => GetObjects((CartesianChart)sender)?.Resync();
}
