using System.Runtime.CompilerServices;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Chart ownership: view models hold data only, every chart view owns its LiveCharts objects. Rendering uses the real
/// LiveCharts engine (Skia, no window), the same engine the WPF control drives.
/// </summary>
public sealed class ChartLifetimeTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 9, 0, 0);

    private static ChartData Data(int points = 30)
    {
        var data = new ChartData("RTT", "ms", 120, "RTT · ms");
        for (var i = 0; i < points; i++) data.Add(T0.AddSeconds(i), i % 10 == 0 ? null : 200 + i);
        data.SetXRange(T0, T0.AddSeconds(points));
        data.SetReferences([new("Warning", 150, "Warning ≥ 150 ms", "Warning 150"), new("Critical", 300, "Critical ≥ 300 ms", "Critical 300", Visible: false)]);
        return data;
    }

    [Fact]
    public void EachChartViewOwnsItsObjectsAndMirrorsTheData()
    {
        var data = Data();
        var a = new ChartObjects(data);
        var b = new ChartObjects(data);
        Assert.NotSame(a.Line, b.Line);
        Assert.NotSame(a.Values, b.Values);
        Assert.NotSame(a.XAxes[0], b.XAxes[0]);
        Assert.NotSame(a.Sections[0], b.Sections[0]);
        Assert.Equal(data.Points.Select(p => p.Value), a.Values.Select(p => p.Value));
        Assert.Equal(data.Points.Select(p => p.At), a.Values.Select(p => p.DateTime));
        Assert.Null(a.Values[0].Value); // gaps stay gaps

        data.Add(T0.AddSeconds(30), 250);
        Assert.Equal(31, a.Values.Count);
        Assert.Equal(250, a.Values[^1].Value);
        for (var i = 0; i < 100; i++) data.Add(T0.AddSeconds(31 + i), 1); // capacity 120: oldest points leave
        Assert.Equal(120, data.Points.Count);
        Assert.Equal(data.Points.Select(p => p.At), a.Values.Select(p => p.DateTime));
        Assert.Equal(data.Points.Select(p => p.At), b.Values.Select(p => p.DateTime));

        data.SetXRange(T0.AddSeconds(60), T0.AddSeconds(120));
        Assert.Equal(T0.AddSeconds(60).Ticks, a.XAxes[0].MinLimit);
        Assert.Equal(T0.AddSeconds(120).Ticks, a.XAxes[0].MaxLimit);
        data.SetYMax(345);
        Assert.Equal(345, a.YAxes[0].MaxLimit);

        var section = (RectangularSection)a.Sections[1];
        Assert.False(section.IsVisible);
        data.SetReferences([data.References[0], data.References[1] with { Visible = true }]);
        Assert.Same(section, a.Sections[1]);
        Assert.True(section.IsVisible);
        var replaced = false;
        a.SectionsReplaced += () => replaced = true;
        data.SetReferences([data.References[0]]);
        Assert.True(replaced);
        Assert.Single(a.Sections);

        data.Clear();
        Assert.Empty(a.Values);
        Assert.Empty(b.Values);
    }

    [Fact]
    public void ResyncAfterReloadRestoresCurrentData()
    {
        var data = Data();
        var view = new ChartObjects(data);
        data.SetXRange(T0.AddSeconds(5), T0.AddSeconds(25));
        view.Resync();
        Assert.Equal(data.Points.Count, view.Values.Count);
        Assert.Equal(T0.AddSeconds(5).Ticks, view.XAxes[0].MinLimit);
        Assert.Equal(data.References.Count, view.Sections.Length);
    }

    private static readonly object RenderGate = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Visit(ChartData data)
    {
        // A page visit: a new chart view with its own objects renders a few live updates, then the page is closed.
        var objects = new ChartObjects(data);
        var chart = new SKCartesianChart { Width = 280, Height = 116, Series = objects.Series, XAxes = objects.XAxes, YAxes = objects.YAxes, Sections = objects.Sections };
        lock (RenderGate)
        {
            for (var f = 0; f < 3; f++)
            {
                var last = data.Points[^1].At;
                data.Add(last.AddSeconds(1), 210);
                data.SetXRange(last.AddSeconds(-59), last.AddSeconds(1));
                chart.GetImage().Dispose();
            }
        }
        chart.CoreChart.Unload();
        return new WeakReference(chart);
    }

    [Fact]
    public void ClosedChartViewsAreCollectedWhileTheDataKeepsUpdating()
    {
        var data = Data(60);
        var visits = Enumerable.Range(0, 24).Select(_ => Visit(data)).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(visits, v => Assert.False(v.IsAlive, "A closed chart view is still reachable from the view model's data."));

        // The data outlives every view and a new view still renders it.
        data.Add(data.Points[^1].At.AddSeconds(1), 220);
        var fresh = new ChartObjects(data);
        Assert.Equal(data.Points.Count, fresh.Values.Count);
        var chart = new SKCartesianChart { Width = 280, Height = 116, Series = fresh.Series, XAxes = fresh.XAxes, YAxes = fresh.YAxes, Sections = fresh.Sections };
        lock (RenderGate) using (var image = chart.GetImage()) Assert.Equal(280, image.Width);
        Assert.Contains(fresh.Line, ((LiveChartsCore.CartesianChartEngine)chart.CoreChart).Series);
    }

    private sealed class Subscriber { public int Calls; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Subscribe(ChartData data, out Func<int> calls)
    {
        var s = new Subscriber();
        WeakSubscription.Collection(data.Points, s, static (x, _) => x.Calls++);
        var weak = new WeakReference(s);
        calls = () => weak.Target is Subscriber alive ? alive.Calls : -1;
        return weak;
    }

    [Fact]
    public void WeakSubscriptionDeliversWhileAliveAndNeverKeepsTheSubscriberAlive()
    {
        var data = Data(1);
        var weak = Subscribe(data, out var calls);
        data.Add(T0.AddSeconds(1), 1);
        Assert.Equal(1, calls());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        data.Add(T0.AddSeconds(2), 2); // the dead subscription is removed here without error
        Assert.Equal(-1, calls());
    }
}
