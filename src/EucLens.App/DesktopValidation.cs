using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;

namespace EucLens.App;

/// <summary>Windows-only acceptance evidence for the real WPF views, invoked by --smoke-test.</summary>
internal static class DesktopValidation
{
    public static async Task RunAsync(Window window, MainViewModel vm)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "visual-evidence");
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        var dpi = VisualTreeHelper.GetDpi(window);
        report.AppendLine($"Host DPI: {dpi.PixelsPerInchX:0} × {dpi.PixelsPerInchY:0}. Other scale cases below simulate available DIP space; they do not change Windows DPI.");
        var navigation = Descendants<ListBox>(window).Single(list => list.Items.Count == 6);
        var client = (FrameworkElement)window.Content;
        var chromeWidth = window.ActualWidth - client.ActualWidth;
        var chromeHeight = window.ActualHeight - client.ActualHeight;
        var originalWidth = client.Width;
        var originalHeight = client.Height;
        // Canvas measures children at their requested size, independent of the host's
        // native window cap. Keep the real client attached so charts remain loaded.
        var viewportHost = new Canvas { DataContext = vm };
        window.Content = null;
        viewportHost.Children.Add(client);
        window.Content = viewportHost;
        var cases = new[] { (1366, 768), (1536, 864), (1920, 1080), (2560, 1440) };
        foreach (var dark in new[] { false, true })
        {
            vm.IsDark = dark;
            var expectedBackground = (SolidColorBrush)window.FindResource("WindowBrush");
            if (window.Background is not SolidColorBrush actualBackground || actualBackground.Color != expectedBackground.Color || !window.UseLayoutRounding)
                throw new InvalidOperationException("Window foundation style or active theme background was not applied.");
            foreach (var (width, height) in cases)
            foreach (var scale in new[] { 1d, 1.25d, 1.5d })
            {
                window.Width = width / scale;
                window.Height = height / scale;
                var viewport = new Size(width / scale - chromeWidth, height / scale - chromeHeight);
                client.Width = viewport.Width;
                client.Height = viewport.Height;
                foreach (var page in vm.Pages)
                {
                    // Exercise navigation through the actual selector binding.
                    navigation.SelectedItem = page;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(75);
                    if (vm.SelectedPage != page) throw new InvalidOperationException("Navigation binding did not select the page.");
                    if (!Descendants<UserControl>(window).Any(view => view.IsLoaded))
                        throw new InvalidOperationException($"No loaded view for {page.Name}.");
                    if (Math.Abs(client.ActualWidth - viewport.Width) > 1 || Math.Abs(client.ActualHeight - viewport.Height) > 1)
                        throw new InvalidOperationException($"Client viewport {client.ActualWidth}×{client.ActualHeight} did not match requested {viewport.Width}×{viewport.Height}.");
                    await WaitForChartMeasureAsync(window);
                    CheckCharts(window, page);
                    CheckPanes(window);
                    if (page is DiagnosticsPage)
                    {
                        CheckText(window);
                        CheckScrolling(window);
                        // Whole page, not just the viewport, for visual review. Scale cases simulate DIP space only.
                        if (scale == 1 || width == 1366)
                            Capture(window, Descendants<Views.DiagnosticsView>(window).Single(),
                                Path.Combine(directory, $"diagnostics-fullpage-{width}x{height}-{scale * 100:0}pct-{(dark ? "dark" : "light")}.png"));
                    }
                    report.AppendLine($"PASS: {(dark ? "dark" : "light")} | {width}×{height} | {scale:P0} available-space simulation | {page.Name} | client viewport {client.ActualWidth:0}×{client.ActualHeight:0} DIP; host window {window.ActualWidth:0}×{window.ActualHeight:0} DIP");
                    // Diagnostics is also captured at 125 % and 150 %, where the evidence panel moves below the list.
                    if ((scale == 1 && (width == 1366 || width == 1920)) || (page is DiagnosticsPage && width == 1366))
                    {
                        Capture(window, client, Path.Combine(directory, $"{width}x{height}-{scale * 100:0}pct-{(dark ? "dark" : "light")}-{page.GetType().Name}.png"));
                    }
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "layout-results.txt"), report.ToString());
        viewportHost.Children.Remove(client);
        client.Width = originalWidth;
        client.Height = originalHeight;
        window.Content = client;
    }
    /// <summary>LiveCharts measures asynchronously after a chart loads; wait (bounded) until every engine has laid out its axes.</summary>
    internal static async Task WaitForChartMeasureAsync(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && Descendants<CartesianChart>(window).Any(c =>
            ((LiveChartsCore.CartesianChartEngine)c.CoreChart) is var e && (e.XAxes.Length == 0 || e.ControlSize.Width <= 0)))
            await window.Dispatcher.InvokeAsync(() => Task.Delay(50), DispatcherPriority.Background).Task.Unwrap();
    }
    internal static void CheckCharts(Window window, PageViewModel page)
    {
        var charts = Descendants<CartesianChart>(window).ToArray();
        var expected = page is DashboardPage ? 2 : page is DiagnosticsPage ? 4 : page is RecorderPage or SystemPage ? 1 : 0;
        if (charts.Length != expected) throw new InvalidOperationException($"{page.Name}: expected {expected} charts, found {charts.Length}.");
        foreach (var chart in charts)
        {
            // Every chart owns its LiveCharts objects (built from the view model's ChartData), and the engine must draw
            // exactly those, mirroring the current data and range, not LiveCharts' defaults.
            var data = LiveChart.GetData(chart) ?? throw new InvalidOperationException($"{page.Name}: a chart is not bound through LiveChart.Data.");
            var objects = LiveChart.GetObjects(chart)!;
            var engine = (LiveChartsCore.CartesianChartEngine)chart.CoreChart;
            if (!ReferenceEquals(chart.Series?.FirstOrDefault(), objects.Line) || engine.XAxes.Length == 0 || !ReferenceEquals(engine.XAxes[0], objects.XAxes[0])
                || !engine.Series.Contains(objects.Line) || objects.Values.Count != data.Points.Count
                || objects.XAxes[0].MinLimit != data.XMin || objects.XAxes[0].MaxLimit != data.XMax || objects.Sections.Length != data.References.Count)
                throw new InvalidOperationException($"{data.Name} chart does not draw its data: series bound={ReferenceEquals(chart.Series?.FirstOrDefault(), objects.Line)}, " +
                    $"engine axes={engine.XAxes.Length}, engine axis bound={engine.XAxes.Length > 0 && ReferenceEquals(engine.XAxes[0], objects.XAxes[0])}, engine series={engine.Series.Count()}, " +
                    $"points {objects.Values.Count}/{data.Points.Count}, sections {objects.Sections.Length}/{data.References.Count}, engine loaded={engine.IsLoaded}, control size={engine.ControlSize.Width:0}×{engine.ControlSize.Height:0}.");
            if (!chart.IsLoaded || chart.ActualWidth <= 0 || chart.ActualHeight <= 0 || chart.AnimationsSpeed != TimeSpan.Zero)
                throw new InvalidOperationException("Chart was not laid out with animation disabled.");
            AssertPaint(chart.TooltipTextPaint as SolidColorPaint, ChartTheme.GetText(chart), "tooltip text");
            AssertPaint(chart.TooltipBackgroundPaint as SolidColorPaint, ChartTheme.GetSurface(chart), "tooltip surface");
            foreach (var axis in chart.XAxes.Concat(chart.YAxes).OfType<Axis>())
                AssertPaint(axis.LabelsPaint as SolidColorPaint, ChartTheme.GetText(chart), "axis labels");
            foreach (var axis in chart.YAxes.OfType<Axis>())
                AssertPaint(axis.SeparatorsPaint as SolidColorPaint, ChartTheme.GetGrid(chart), "gridlines");
            foreach (var section in chart.Sections?.OfType<RectangularSection>() ?? [])
            {
                AssertPaint(section.Stroke as SolidColorPaint, Equals(section.Tag, "Critical") ? ChartTheme.GetCritical(chart) : ChartTheme.GetWarning(chart), "threshold line");
                if (section.Fill is not null) throw new InvalidOperationException("Threshold lines must not fill an area.");
            }
            foreach (var series in chart.Series.OfType<LineSeries<DateTimePoint>>())
            {
                AssertPaint(series.Stroke as SolidColorPaint, ChartTheme.GetLine(chart), "series stroke");
                if (series.Fill is not null || series.GeometrySize != 0)
                    throw new InvalidOperationException("Unexpected chart fill or point markers.");
            }
        }
    }
    private static void AssertPaint(SolidColorPaint? paint, Brush? brush, string role)
    {
        if (paint is null || brush is not SolidColorBrush solid || paint.Color.Red != solid.Color.R ||
            paint.Color.Green != solid.Color.G || paint.Color.Blue != solid.Color.B)
            throw new InvalidOperationException($"Chart {role} does not match active theme.");
    }
    private static void CheckPanes(Window window)
    {
        foreach (var panel in Descendants<Panel>(window).Where(p => p is AdaptiveColumnsPanel or MasterDetailPanel))
        {
            var bounds = panel.Children.Cast<UIElement>().Where(child => child.Visibility == Visibility.Visible)
                .Select(child => child.TransformToAncestor(panel).TransformBounds(new Rect(child.RenderSize))).ToArray();
            for (var i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].Right > panel.ActualWidth + 1) throw new InvalidOperationException("A technical pane exceeds the available width.");
                for (var j = i + 1; j < bounds.Length; j++)
                {
                    var overlap = Rect.Intersect(bounds[i], bounds[j]);
                    if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
                        throw new InvalidOperationException("Technical panes overlap.");
                }
            }
        }
    }
    /// <summary>The page scrolls as one surface: no nested scroll viewer inside it, and the end of the page is reachable.</summary>
    private static void CheckScrolling(Window window)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        if (Descendants<ScrollViewer>(view).Any(v => v.IsVisible)) throw new InvalidOperationException("Diagnostics contains a nested scroll viewer that would capture the mouse wheel.");
        var page = Descendants<ScrollViewer>(window).First(v => v.IsAncestorOf(view));
        if (page.ScrollableHeight > 0)
        {
            page.ScrollToEnd();
            page.UpdateLayout();
            if (Math.Abs(page.VerticalOffset - page.ScrollableHeight) > 1) throw new InvalidOperationException("Diagnostics page cannot scroll to its end.");
            page.ScrollToHome();
            page.UpdateLayout();
        }
    }
    /// <summary>Single-line text without trimming must fit its box; wrapping text reflows and is not clipped.</summary>
    private static void CheckText(Window window)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        var dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        foreach (var text in Descendants<TextBlock>(view))
        {
            if (!text.IsVisible || text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None || text.ActualWidth <= 0 || string.IsNullOrEmpty(text.Text)) continue;
            var natural = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
            if (natural > text.ActualWidth + 1)
                throw new InvalidOperationException($"Diagnostics text is clipped: '{text.Text}' needs {natural:0} DIP, has {text.ActualWidth:0} DIP.");
        }
    }
    private static void Capture(Window window, FrameworkElement visual, string path)
    {
        // Capture the actual WPF client visual. Native window chrome is excluded.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Window.Background belongs to the native presentation root, outside the
        // captured client visual. Composite it explicitly instead of emitting transparent gaps.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
