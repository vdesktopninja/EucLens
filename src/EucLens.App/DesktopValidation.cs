using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                        await CheckDiagnosticsLayoutAsync(window, vm);
                        // Whole page, not just the viewport, for visual review. Scale cases simulate DIP space only.
                        if (scale == 1 || width == 1366)
                        {
                            var tag = $"{width}x{height}-{scale * 100:0}pct-{(dark ? "dark" : "light")}";
                            Capture(window, Descendants<Views.DiagnosticsView>(window).Single(), Path.Combine(directory, $"diagnostics-fullpage-{tag}.png"));
                            // The findings and evidence region as the administrator sees it after selecting a finding.
                            await ScrollToFindingsAsync(window);
                            Capture(window, client, Path.Combine(directory, $"diagnostics-findings-{tag}.png"));
                            await ScrollPageAsync(window, home: true);
                        }
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
        foreach (var panel in Descendants<Panel>(window).Where(p => p is AdaptiveColumnsPanel or ResponsiveSplitPanel))
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
    private static ScrollViewer PageScroll(Window window) =>
        Descendants<ScrollViewer>(window).First(v => v.IsAncestorOf(Descendants<Views.DiagnosticsView>(window).Single()));

    private static async Task ScrollPageAsync(Window window, bool home)
    {
        var page = PageScroll(window);
        if (home) page.ScrollToHome(); else page.ScrollToEnd();
        await window.Dispatcher.InvokeAsync(page.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static async Task ScrollToFindingsAsync(Window window)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        ((FrameworkElement)view.FindName("FindingsRegion")).BringIntoView();
        await window.Dispatcher.InvokeAsync(() => PageScroll(window).UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static void RaiseKey(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    /// <summary>Raises what the input system raises for a real wheel turn: the preview event, then (unless handled) the bubbling event.</summary>
    private static void RaiseWheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = target });
    }

    /// <summary>
    /// Findings and evidence layout: responsive arrangement, usable minimum sizes, reachability below the fold, independent
    /// scrolling with wheel chaining, keyboard access, selection that survives resizing, and no horizontal overflow of the page.
    /// </summary>
    private static async Task CheckDiagnosticsLayoutAsync(Window window, MainViewModel vm)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        var region = (ResponsiveSplitPanel)view.FindName("FindingsRegion");
        var findings = (ScrollViewer)view.FindName("FindingsScroll");
        var evidence = (ScrollViewer)view.FindName("EvidenceScroll");
        var splitter = (Thumb)view.FindName("FindingsSplitter");
        var list = (ListBox)view.FindName("FindingsList");
        var page = PageScroll(window);
        var where = $"viewport {page.ViewportWidth:0}×{page.ViewportHeight:0}, region {region.ActualWidth:0}×{region.ActualHeight:0}";
        Task Idle() => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

        // No horizontal overflow: the page never needs a horizontal scroll bar.
        if (view.DesiredSize.Width > page.ViewportWidth + 1) throw new InvalidOperationException($"Diagnostics is wider than the page ({view.DesiredSize.Width:0} > {page.ViewportWidth:0} DIP).");

        // The region fills the visible page height, with a floor so both panes stay usable.
        var expectedHeight = SplitLayout.RegionHeight(page.ViewportHeight, (double)view.FindResource("FindingsRegionReserve"), (double)view.FindResource("FindingsRegionMinHeight"));
        if (Math.Abs(region.ActualHeight - expectedHeight) > 1.5) throw new InvalidOperationException($"Findings region height {region.ActualHeight:0} does not follow the visible page height ({expectedHeight:0}; {where}).");

        // Responsive arrangement follows the available width and keeps both panes usable.
        var expectedSide = SplitLayout.IsSideBySide(region.ActualWidth, SplitOptions.Default with { SplitWidth = region.SplitWidth, MinMasterWidth = region.MinMasterWidth, MinDetailWidth = region.MinDetailWidth, Thickness = region.SplitterThickness });
        if (region.IsSideBySide != expectedSide) throw new InvalidOperationException($"Findings region arranged {(region.IsSideBySide ? "side by side" : "stacked")} but width {region.ActualWidth:0} DIP requires the opposite.");
        if (evidence.ActualWidth < region.MinDetailWidth - 4 || evidence.ActualHeight < 140 || findings.ActualHeight < 60 || findings.ActualWidth < 200)
            throw new InvalidOperationException($"A findings pane is too small to use: findings {findings.ActualWidth:0}×{findings.ActualHeight:0}, evidence {evidence.ActualWidth:0}×{evidence.ActualHeight:0} ({where}).");

        // Selection survives resizing and relayout, and the evidence belongs to it.
        if (vm.Diagnostics.SelectedFinding is { } selected)
        {
            if (!ReferenceEquals(list.SelectedItem, selected) || list.ItemContainerGenerator.ContainerFromItem(selected) is not ListBoxItem { IsSelected: true })
                throw new InvalidOperationException("The selected finding is no longer selected in the list after a layout change.");
            if (((ContentControl)((StackPanel)evidence.Content).Children[1]).Content != selected) throw new InvalidOperationException("The evidence panel shows a different finding than the selection.");
        }

        // The evidence panel is reachable: scrolling the page brings its top into the viewport.
        evidence.BringIntoView();
        await Idle();
        var top = evidence.TransformToAncestor(page).Transform(new Point(0, 0)).Y;
        if (top < -1 || top >= page.ViewportHeight - 20) throw new InvalidOperationException($"The evidence panel cannot be brought into view (top {top:0} of {page.ViewportHeight:0}).");

        // Independent scrolling: moving through the evidence does not move the page; at its end the wheel continues to the page.
        page.ScrollToHome(); evidence.ScrollToHome(); await Idle();
        if (evidence.ScrollableHeight > 1)
        {
            var pageBefore = page.VerticalOffset;
            RaiseWheel(evidence, -120);
            await Idle();
            if (evidence.VerticalOffset <= 0 || Math.Abs(page.VerticalOffset - pageBefore) > 0.5) throw new InvalidOperationException("The wheel did not scroll the evidence pane on its own.");
            evidence.ScrollToEnd(); await Idle();
        }
        if (page.ScrollableHeight > 1)
        {
            var pageBefore = page.VerticalOffset;
            RaiseWheel(evidence, -120);
            await Idle();
            if (page.VerticalOffset <= pageBefore) throw new InvalidOperationException($"The wheel did not continue to the page after the evidence pane reached its end ({where}).");
        }
        evidence.ScrollToHome(); await ScrollPageAsync(window, home: true);

        // Keyboard access: evidence and splitter are focusable tab stops, and the splitter moves with the arrow keys within its limits.
        if (!evidence.Focusable || !evidence.IsTabStop || !splitter.Focusable || !splitter.IsTabStop || !list.IsTabStop)
            throw new InvalidOperationException("Findings, splitter or evidence cannot be reached with the keyboard.");
        // The effective position of the splitter is the master pane's extent; at a limit one direction cannot move, the other can.
        double Extent() => region.IsSideBySide ? findings.ActualWidth : findings.ActualHeight;
        var (wide, stacked) = (region.WideFraction, region.StackedFraction);
        var (grow, shrink) = region.IsSideBySide ? (Key.Right, Key.Left) : (Key.Down, Key.Up);
        var positions = new List<double> { Extent() };
        foreach (var key in new[] { grow, shrink, shrink })
        {
            RaiseKey(splitter, key);
            await Idle();
            positions.Add(Extent());
        }
        if (positions.Zip(positions.Skip(1), (a, b) => Math.Abs(b - a)).Max() < 0.5) throw new InvalidOperationException($"The splitter did not respond to the keyboard ({where}).");
        if (evidence.ActualWidth < region.MinDetailWidth - 4 || evidence.ActualHeight < 140 || findings.ActualHeight < 60)
            throw new InvalidOperationException("Moving the splitter shrank a pane below its minimum.");
        // The view is rebuilt on every visit; restore the starting layout so captures show the default split.
        region.WideFraction = wide; region.StackedFraction = stacked;
        await Idle();
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
