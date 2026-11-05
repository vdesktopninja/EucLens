using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
        report.Append(TypographyValidation.CheckResources(window));
        TypographyValidation.Reset();
        var navigation = Descendants<ListBox>(window).Single(list => ReferenceEquals(list.ItemsSource, vm.Pages));
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
            try { report.AppendLine(CheckSeveritySpecimens(window, directory, dark)); }
            catch (Exception ex)
            {
                SmokeLog.Write($"severity icon specimens FAILED ({(dark ? "dark" : "light")}): {ex.Message}");
                report.AppendLine($"FAIL: severity icon specimens ({(dark ? "dark" : "light")}) | {ex.GetType().Name}: {ex.Message}");
                await File.WriteAllTextAsync(Path.Combine(directory, "layout-results.txt"), report.ToString());
                throw;
            }
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
                    var visit = $"{(dark ? "dark" : "light")} | {width}×{height} | {scale:P0} | {page.Name}";
                    try
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
                    CheckNoSourceSelection(window, page);
                    TypographyValidation.CheckPage(window, visit);
                    if (page is DiagnosticsPage)
                    {
                        CheckText(window, visit);
                        report.AppendLine($"    severity icons: {CheckSeverityIcons(window, vm)}");
                        var keyboard = await CheckDiagnosticsLayoutAsync(window, vm);
                        var emptyState = await CheckEmptyFindingsAsync(window, vm, Descendants<Views.DiagnosticsView>(window).Single());
                        report.AppendLine($"    keyboard: {keyboard}");
                        report.AppendLine($"    empty findings: {emptyState}");
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
                    // Informational: with eight pages the navigation list needs more height than a 1366×768 window at 150 % leaves; it then scrolls inside its own column (nothing is clipped without a way to reach it).
                    if (page == vm.Pages[0] && Descendants<ScrollViewer>(navigation).FirstOrDefault() is { ScrollableHeight: > 1 } navScroll)
                        report.AppendLine($"    INFO: {(dark ? "dark" : "light")} | {width}×{height} | {scale:P0}: the navigation list ({vm.Pages.Count} pages) scrolls by {navScroll.ScrollableHeight:0} DIPs inside its column (viewport {navScroll.ViewportHeight:0}, content {navScroll.ExtentHeight:0}).");
                    report.AppendLine($"PASS: {(dark ? "dark" : "light")} | {width}×{height} | {scale:P0} available-space simulation | {page.Name} | client viewport {client.ActualWidth:0}×{client.ActualHeight:0} DIP; host window {window.ActualWidth:0}×{window.ActualHeight:0} DIP");
                    // Diagnostics is also captured at 125 % and 150 %, where the evidence panel moves below the list.
                    if ((scale == 1 && (width == 1366 || width == 1920)) || (page is DiagnosticsPage && width == 1366))
                    {
                        Capture(window, client, Path.Combine(directory, $"{width}x{height}-{scale * 100:0}pct-{(dark ? "dark" : "light")}-{page.GetType().Name}.png"));
                    }
                    }
                    catch (Exception ex)
                    {
                        // Keep the evidence of the failing state: where it happened, what had passed, the runtime state and a screenshot.
                        SmokeLog.Write($"layout FAILED at {visit}: {ex.Message}");
                        report.AppendLine($"FAIL: {visit} | {ex.GetType().Name}: {ex.Message}");
                        try
                        {
                            var pageScroll = PageHost(window);
                            report.AppendLine($"    state: client {client.ActualWidth:0}×{client.ActualHeight:0}, page viewport {pageScroll?.ViewportWidth:0}×{pageScroll?.ViewportHeight:0}, offset {pageScroll?.VerticalOffset:0}/{pageScroll?.ScrollableHeight:0}, window active {window.IsActive}, keyboard focus on {Describe(Keyboard.FocusedElement)}, selected finding {vm.Diagnostics.SelectedFinding?.RuleId ?? "none"}, findings {vm.Diagnostics.Findings.Count}, filter {vm.Diagnostics.SelectedFilter.Label}");
                            Capture(window, client, Path.Combine(directory, $"FAILED-{(dark ? "dark" : "light")}-{width}x{height}-{scale * 100:0}pct-{page.GetType().Name}.png"));
                        }
                        catch (Exception captureFailure) { report.AppendLine($"    (state capture failed: {captureFailure.Message})"); }
                        await File.WriteAllTextAsync(Path.Combine(directory, "layout-results.txt"), report.ToString());
                        throw;
                    }
                }
            }
        }
        report.AppendLine(TypographyValidation.Problems.Count == 0 ? "PASS: typography: no text below 13 DIPs, clipped, truncated without a full value, cut, overlapping or outside the window, and no page scrolls sideways, on every page, size, scale and theme of the matrix" : $"FAIL: typography: {TypographyValidation.Problems.Count} problem(s)\n  " + string.Join("\n  ", TypographyValidation.Problems));
        await File.WriteAllTextAsync(Path.Combine(directory, "layout-results.txt"), report.ToString());
        viewportHost.Children.Remove(client);
        client.Width = originalWidth;
        client.Height = originalHeight;
        window.Content = client;
        if (TypographyValidation.Problems.Count > 0) throw new InvalidOperationException($"{TypographyValidation.Problems.Count} typography problem(s): {string.Join(" || ", TypographyValidation.Problems.Take(40))}");
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
        CheckChartList(charts, page.Name);
    }

    /// <summary>The ownership and theme rules every chart follows (own LiveCharts objects built from its ChartData, drawn by the engine, themed, no animation).</summary>
    internal static void CheckChartList(IEnumerable<CartesianChart> charts, string page)
    {
        foreach (var chart in charts)
        {
            // Every chart owns its LiveCharts objects (built from the view model's ChartData), and the engine must draw
            // exactly those, mirroring the current data and range, not LiveCharts' defaults.
            var data = LiveChart.GetData(chart) ?? throw new InvalidOperationException($"{page}: a chart is not bound through LiveChart.Data.");
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
    /// <summary>
    /// Production pages offer no data-source, demo protocol, demo scenario or workload selection. Dashboard and Settings show the detected
    /// protocol and the data availability as text instead.
    /// </summary>
    private static void CheckNoSourceSelection(Window window, PageViewModel page)
    {
        string[] names = ["Monitoring mode", "Simulated protocol", "Demo scenario", "Workload context", "Diagnostic workload context", "Demo selection"];
        string[] labels = ["Demo protocol", "Demo scenario", "Workload", "Workload (user-selected)", "Monitoring defaults"];
        var offending = Descendants<FrameworkElement>(window).FirstOrDefault(e => e.IsVisible
            && (names.Contains(System.Windows.Automation.AutomationProperties.GetName(e)) || e is TextBlock t && labels.Contains(t.Text)));
        Require(offending is null, $"{page.Name}: the page offers a selection that must not exist ({offending?.GetType().Name} '{(offending is null ? "" : System.Windows.Automation.AutomationProperties.GetName(offending))}').");
        if (page is not (DashboardPage or SettingsPage)) return;
        foreach (var name in new[] { "Detected protocol", "Data availability" })
            Require(Descendants<TextBlock>(window).Any(t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == name && t.Text.Length > 0),
                $"{page.Name}: the {name.ToLowerInvariant()} indicator is missing.");
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

    internal static void RaiseKey(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    /// <summary>Raises what the input system raises for a real wheel turn: the preview event, then (unless handled) the bubbling event.</summary>
    private static void RaiseWheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = target });
    }

    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }

    private static string Describe(IInputElement? element) => element switch
    {
        null => "nothing",
        FrameworkElement { Name: { Length: > 0 } name } fe => $"{fe.GetType().Name} '{name}'",
        FrameworkElement fe => fe.GetType().Name + (fe.DataContext is FindingItem item ? $" (finding {item.RuleId})" : ""),
        _ => element.GetType().Name,
    };

    /// <summary>
    /// Keyboard access to the findings list, the splitter and the evidence panel.
    /// WPF semantics: a ListBox is not a tab stop itself (its rows are), and the findings list is one Tab stop with arrow keys
    /// moving the selection inside it. So the list is checked through its rows and navigation modes, never through
    /// <c>ListBox.IsTabStop</c>. Real focus movement is verified when this window can actually take keyboard focus; otherwise
    /// that is stated in the evidence rather than assumed.
    /// </summary>
    private static async Task<string> CheckKeyboardAccessAsync(Window window, MainViewModel vm, Views.DiagnosticsView view)
    {
        var evidence = (ScrollViewer)view.FindName("EvidenceScroll");
        var splitter = (Thumb)view.FindName("FindingsSplitter");
        var list = (ListBox)view.FindName("FindingsList");
        var d = vm.Diagnostics;
        Task Idle() => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

        // Evidence panel: a focusable tab stop, so PageUp/PageDown and the arrow keys scroll it.
        Require(evidence.Focusable, "Evidence panel: ScrollViewer.Focusable is false, so the keyboard cannot focus it.");
        Require(evidence.IsTabStop, "Evidence panel: IsTabStop is false, so Tab skips it.");
        Require(evidence.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled, "Evidence panel: vertical scrolling is disabled.");

        // Splitter: a focusable tab stop that responds to the arrow keys (exercised by the caller).
        Require(splitter.Focusable, "Splitter: Thumb.Focusable is false, so the keyboard cannot focus it.");
        Require(splitter.IsTabStop, "Splitter: IsTabStop is false, so Tab skips it.");

        // Findings list: one Tab stop for the list, arrow keys inside it, selection by arrow key. The list control itself is not a tab stop.
        Require(KeyboardNavigation.GetTabNavigation(list) == KeyboardNavigationMode.Once, $"Findings list: TabNavigation is {KeyboardNavigation.GetTabNavigation(list)}, expected Once (one Tab stop for the whole list).");
        Require(KeyboardNavigation.GetDirectionalNavigation(list) == KeyboardNavigationMode.Contained, $"Findings list: DirectionalNavigation is {KeyboardNavigation.GetDirectionalNavigation(list)}, expected Contained (arrow keys stay inside the list).");
        Require(list.SelectionMode == SelectionMode.Single, $"Findings list: SelectionMode is {list.SelectionMode}, expected Single.");
        var notes = new List<string>();
        var populated = d.Findings.Count > 0;
        if (populated)
        {
            Require(list.Items.Count == d.Findings.Count, $"Findings list shows {list.Items.Count} rows for {d.Findings.Count} findings.");
            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Require(row is not null, "Findings list: no row container was generated for the first finding.");
            Require(row!.Focusable, "Findings list: a row (ListBoxItem) is not focusable, so Tab and the arrow keys cannot reach it.");
            Require(row.IsTabStop, "Findings list: a row (ListBoxItem) is not a tab stop.");
            notes.Add($"populated ({list.Items.Count} rows)");
        }
        else
        {
            // No findings is a real state, shown with an explanation; nothing is invented for the test.
            Require(list.Items.Count == 0 && !Descendants<ListBoxItem>(list).Any(), "Findings list: rows exist although there are no findings.");
            Require(d.IsListEmpty && d.ListEmptyTitle.Length > 0, "Findings list: empty, but no explanation is shown.");
            notes.Add("empty list with an explanation");
        }

        // Real keyboard focus, where this runner's window can take it.
        window.Activate();
        for (var i = 0; i < 20 && !window.IsActive; i++) await Task.Delay(50);
        var focusable = window.IsActive && ReferenceEquals(Keyboard.Focus(evidence), evidence) && ReferenceEquals(Keyboard.FocusedElement, evidence);
        if (!focusable)
        {
            notes.Add($"real focus movement NOT verified (window active: {window.IsActive}); structural checks only");
            return string.Join("; ", notes);
        }
        notes.Add("real focus movement verified");

        // Tab reaches every part: walk forward from the first button of the page and collect what receives focus.
        var start = Descendants<Button>(view).FirstOrDefault(b => b.IsVisible && b.Focusable);
        Require(start is not null && ReferenceEquals(Keyboard.Focus(start), start), "Keyboard: could not place focus on the first button of the Diagnostics page.");
        bool sawList = false, sawSplitter = false, sawEvidence = false;
        var visited = new List<string>();
        for (var step = 0; step < 250 && !(sawSplitter && sawEvidence && (sawList || !populated)); step++)
        {
            if (Keyboard.FocusedElement is not UIElement current) break;
            current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            var focused = Keyboard.FocusedElement;
            visited.Add(Describe(focused));
            sawList |= focused is ListBoxItem row && list.IsAncestorOf(row);
            sawSplitter |= ReferenceEquals(focused, splitter);
            sawEvidence |= ReferenceEquals(focused, evidence);
        }
        var trail = string.Join(" → ", visited.TakeLast(12));
        Require(sawSplitter, $"Keyboard: Tab never reached the splitter. Last focus targets: {trail}");
        Require(sawEvidence, $"Keyboard: Tab never reached the evidence panel. Last focus targets: {trail}");
        Require(sawList || !populated, $"Keyboard: Tab never reached a findings row. Last focus targets: {trail}");

        // Arrow keys move the selection inside the list; then restore it.
        if (populated && d.Findings.Count >= 2 && d.SelectedFinding is { } selected && list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
        {
            Require(ReferenceEquals(Keyboard.Focus(container), container), "Keyboard: could not focus the selected findings row.");
            var index = d.Findings.IndexOf(selected);
            var (first, back) = index < d.Findings.Count - 1 ? (Key.Down, Key.Up) : (Key.Up, Key.Down);
            var expected = d.Findings[index + (first == Key.Down ? 1 : -1)];
            RaiseKey(container, first);
            await Idle();
            Require(ReferenceEquals(d.SelectedFinding, expected), $"Keyboard: {first} did not move the selection to the neighbouring finding (selection is {d.SelectedFinding?.RuleId ?? "none"}, expected {expected.RuleId}; focus is on {Describe(Keyboard.FocusedElement)}).");
            Require(Keyboard.FocusedElement is ListBoxItem, $"Keyboard: after {first}, focus left the findings rows (on {Describe(Keyboard.FocusedElement)}).");
            RaiseKey((UIElement)Keyboard.FocusedElement, back);
            await Idle();
            Require(ReferenceEquals(d.SelectedFinding, selected), $"Keyboard: {back} did not return the selection (selection is {d.SelectedFinding?.RuleId ?? "none"}).");
            notes.Add("arrow keys select neighbouring findings");
        }
        Keyboard.ClearFocus();
        await ScrollPageAsync(window, home: true);
        return string.Join("; ", notes);
    }

    /// <summary>The empty-list state, reached through the real filters (no finding is invented): explained, and the other panes stay reachable.</summary>
    private static async Task<string> CheckEmptyFindingsAsync(Window window, MainViewModel vm, Views.DiagnosticsView view)
    {
        var d = vm.Diagnostics;
        var emptyFilter = d.Filters.FirstOrDefault(f => f.Kind != FindingFilterKind.All && f.Count == 0);
        if (emptyFilter is null) return "no filter with zero findings at this moment; empty state not exercised";
        var previous = d.SelectedFilter;
        var list = (ListBox)view.FindName("FindingsList");
        d.SelectedFilter = emptyFilter;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            Require(list.Items.Count == 0 && !Descendants<ListBoxItem>(list).Any(), $"Empty filter '{emptyFilter.Label}': the list still shows rows.");
            Require(d.IsListEmpty && d.ListEmptyTitle.Length > 0, $"Empty filter '{emptyFilter.Label}': no empty-state explanation.");
            Require(d.SelectedFinding is null && d.HasNoSelection, $"Empty filter '{emptyFilter.Label}': a finding is still selected.");
            var evidence = (ScrollViewer)view.FindName("EvidenceScroll");
            var hint = ((StackPanel)evidence.Content).Children[0] as TextBlock;
            Require(hint is { IsVisible: true, Text.Length: > 0 }, $"Empty filter '{emptyFilter.Label}': the evidence panel shows no hint.");
            Require(evidence.Focusable && evidence.IsTabStop && ((Thumb)view.FindName("FindingsSplitter")).Focusable, "Empty list: the evidence panel or splitter is no longer keyboard reachable.");
            return $"empty state ('{emptyFilter.Label}') explained, evidence hint shown, panes reachable";
        }
        finally
        {
            d.SelectedFilter = previous;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// Findings and evidence layout: responsive arrangement, usable minimum sizes, reachability below the fold, independent
    /// scrolling with wheel chaining, keyboard access, selection that survives resizing, and no horizontal overflow of the page.
    /// </summary>
    private static async Task<string> CheckDiagnosticsLayoutAsync(Window window, MainViewModel vm)
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

        // Keyboard access, one named check per element (see CheckKeyboardAccessAsync), then the splitter's arrow keys within its limits.
        var keyboard = await CheckKeyboardAccessAsync(window, vm, view);
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
        return keyboard;
    }
    // ---------- severity icons ----------
    private static readonly (SeverityKind Kind, string Label, string Brush)[] SeveritySpecimens =
    [
        (SeverityKind.Info, "Information", "AccentBrush"), (SeverityKind.Warning, "Warning", "WarningBrush"), (SeverityKind.Critical, "Critical", "CriticalBrush"),
        (SeverityKind.Healthy, "Healthy", "SuccessBrush"), (SeverityKind.Recovered, "Recovered", "MutedBrush"), (SeverityKind.NoData, "Insufficient data", "MutedBrush")
    ];

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { var s = v / 255d; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>
    /// Every severity icon on the Diagnostics page must be a real, visible shape: populated geometry of the right kind, the style's size,
    /// the theme colour (3:1 against the surface), a text label next to it, and vertical alignment with that label.
    /// </summary>
    private static string CheckSeverityIcons(Window window, MainViewModel vm)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        var style = (Style)window.FindResource("SeverityIcon");
        var size = (double)window.FindResource("SeverityIconSize");
        var surface = ((SolidColorBrush)window.FindResource("SurfaceBrush")).Color;
        var counts = new SortedDictionary<string, int>();
        foreach (var icon in Descendants<System.Windows.Shapes.Path>(view).Where(p => ReferenceEquals(p.Style, style) && p.IsVisible))
        {
            if (!Enum.TryParse<SeverityKind>(icon.Tag as string, out var kind) || !Enum.IsDefined(kind)) throw new InvalidOperationException($"A severity icon has an unknown kind '{icon.Tag}'.");
            var expected = (Geometry)window.FindResource(SeverityKinds.GeometryKey(kind));
            var brushName = SeveritySpecimens.Single(x => x.Kind == kind).Brush;
            if (icon.Data is null || icon.Data.IsEmpty() || icon.Data.Bounds.Width < 8 || icon.Data.Bounds.Height < 8)
                throw new InvalidOperationException($"The {kind} icon has no geometry (bounds {icon.Data?.Bounds}).");
            if (!ReferenceEquals(icon.Data, expected)) throw new InvalidOperationException($"The {kind} icon shows the geometry of another kind.");
            if (Math.Abs(icon.ActualWidth - size) > 0.5 || Math.Abs(icon.ActualHeight - size) > 0.5) throw new InvalidOperationException($"The {kind} icon is {icon.ActualWidth:0.#}×{icon.ActualHeight:0.#} DIP, expected {size:0}.");
            if (icon.Fill is not SolidColorBrush fill || fill.Color != ((SolidColorBrush)window.FindResource(brushName)).Color || fill.Opacity < 1 || icon.Opacity < 1)
                throw new InvalidOperationException($"The {kind} icon is not drawn with {brushName}.");
            if (Contrast(fill.Color, surface) < 3) throw new InvalidOperationException($"The {kind} icon has {Contrast(fill.Color, surface):0.0}:1 contrast against the surface, below 3:1.");
            if (VisualTreeHelper.GetParent(icon) is not Panel panel || panel.Children.OfType<TextBlock>().FirstOrDefault() is not { } label || string.IsNullOrWhiteSpace(label.Text))
                throw new InvalidOperationException($"The {kind} icon has no text label next to it.");
            var iconMid = icon.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
            var labelMid = label.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
            if (Math.Abs((iconMid.Top + iconMid.Bottom) / 2 - (labelMid.Top + labelMid.Bottom) / 2) > 2.5)
                throw new InvalidOperationException($"The {kind} icon is not vertically aligned with its label '{label.Text}' (icon {iconMid.Top:0.#}–{iconMid.Bottom:0.#}, text {labelMid.Top:0.#}–{labelMid.Bottom:0.#}).");
            counts[kind.ToString()] = counts.GetValueOrDefault(kind.ToString()) + 1;
        }
        var total = counts.Values.Sum();
        var tiles = Math.Min(3, vm.Diagnostics.Domains.Count); // the page shows the first three domains as tiles
        if (total < 1 + tiles) throw new InvalidOperationException($"Only {total} severity icons are visible on Diagnostics; the status line and {tiles} health tiles each need one.");
        return $"{total} visible, {string.Join(", ", counts.Select(c => $"{c.Key}×{c.Value}"))}";
    }

    /// <summary>
    /// Controlled data: each of the six kinds is rendered with the real style and the active theme at 100, 125 and 150 % pixel density
    /// (render-time density of the bitmap, not a Windows DPI change). The pixels must show a shape of sensible size, in the theme colour,
    /// with the symbol visible as a cut-out or fill, and every kind must differ from every other. A labelled sheet is saved for review.
    /// </summary>
    private static string CheckSeveritySpecimens(Window window, string directory, bool dark)
    {
        var theme = dark ? "dark" : "light";
        var style = (Style)window.FindResource("SeverityIcon");
        var size = (double)window.FindResource("SeverityIconSize");
        var surface = (SolidColorBrush)window.FindResource("SurfaceBrush");
        var summary = new List<string>();
        foreach (var scale in new[] { 1d, 1.25, 1.5, 4d })
        {
            var dpi = 96 * scale;
            var px = (int)Math.Ceiling(size * scale);
            var masks = new Dictionary<SeverityKind, byte[]>();
            var sheet = new StackPanel { Background = surface, Margin = new Thickness(0), MinWidth = 200 };
            foreach (var (kind, text, brushName) in SeveritySpecimens)
            {
                if (scale != 4)
                {
                    var cell = new Border { Width = size, Height = size, Child = new System.Windows.Shapes.Path { Style = style, Tag = kind.ToString() } };
                    cell.Measure(new Size(size, size)); cell.Arrange(new Rect(0, 0, size, size)); cell.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(cell);
                    var pixels = new byte[px * px * 4];
                    bitmap.CopyPixels(pixels, px * 4, 0);
                    var alpha = new byte[px * px];
                    for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
                    masks[kind] = alpha;
                    var coverage = alpha.Count(a => a > 128) / (double)alpha.Length;
                    if (coverage < 0.18 || coverage > 0.85) throw new InvalidOperationException($"{theme} {kind} icon at {scale:P0}: covers {coverage:P0} of its box, expected 18–85 %.");
                    var brush = ((SolidColorBrush)window.FindResource(brushName)).Color;
                    var opaque = Enumerable.Range(0, alpha.Length).Where(i => alpha[i] == 255).ToArray();
                    if (opaque.Length < 20) throw new InvalidOperationException($"{theme} {kind} icon at {scale:P0}: only {opaque.Length} fully opaque pixels.");
                    var off = opaque.Count(i => Math.Abs(pixels[i * 4 + 2] - brush.R) > 3 || Math.Abs(pixels[i * 4 + 1] - brush.G) > 3 || Math.Abs(pixels[i * 4] - brush.B) > 3);
                    if (off > 0) throw new InvalidOperationException($"{theme} {kind} icon at {scale:P0}: {off} opaque pixels are not the {brushName} colour {brush}.");
                    // The symbol: a cut-out of the disc / triangle, or a fill inside the ring.
                    int lo = (int)Math.Round(px * 5 / 16d), hi = (int)Math.Round(px * 11 / 16d), centre = 0, hole = 0;
                    for (var y = lo; y < hi; y++) for (var x = lo; x < hi; x++) { centre++; if (alpha[y * px + x] < 128) hole++; }
                    var holes = hole / (double)centre;
                    var ring = kind is SeverityKind.Recovered or SeverityKind.NoData;
                    if (ring ? holes > 0.92 || holes < 0.30 : holes < 0.08) throw new InvalidOperationException($"{theme} {kind} icon at {scale:P0}: the symbol is not visible ({holes:P0} of the centre is transparent).");
                }
                var label = new TextBlock { Text = text, Style = (Style)window.FindResource("StatusText"), FontSize = (double)window.FindResource("ReadingFontSize"), Tag = kind is SeverityKind.Warning or SeverityKind.Critical or SeverityKind.Healthy ? kind.ToString() : "Neutral" };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 12, 5) };
                row.Children.Add(new System.Windows.Shapes.Path { Style = style, Tag = kind.ToString(), Margin = new Thickness(0, 0, 8, 0) });
                row.Children.Add(label);
                sheet.Children.Add(row);
            }
            if (scale != 4)
            {
                var kinds = masks.Keys.ToArray();
                foreach (var a in kinds) foreach (var b in kinds.Where(k => k > a))
                {
                    var differing = Enumerable.Range(0, masks[a].Length).Count(i => Math.Abs(masks[a][i] - masks[b][i]) > 64) / (double)masks[a].Length;
                    if (differing < 0.04) throw new InvalidOperationException($"{theme} {a} and {b} icons at {scale:P0} look alike ({differing:P1} of the pixels differ).");
                }
            }
            sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sheet.Arrange(new Rect(sheet.DesiredSize));
            sheet.UpdateLayout();
            var sheetBitmap = new RenderTargetBitmap((int)Math.Ceiling(sheet.DesiredSize.Width * scale), (int)Math.Ceiling(sheet.DesiredSize.Height * scale), dpi, dpi, PixelFormats.Pbgra32);
            sheetBitmap.Render(sheet);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheetBitmap));
            using (var stream = File.Create(Path.Combine(directory, scale == 4 ? $"severity-icons-{theme}-zoom.png" : $"severity-icons-{theme}-{scale * 100:0}pct.png"))) encoder.Save(stream);
            if (scale != 4) summary.Add($"{scale:P0}");
        }
        return $"PASS: severity icons ({theme}): 6 kinds drawn with their theme colours, symbol visible and pairwise distinct at {string.Join(", ", summary)} pixel density (render-time density, not Windows DPI)";
    }

    /// <summary>
    /// Single-line text without trimming must fit its box; wrapping text reflows and is not clipped. The natural width comes from
    /// a clone of the TextBlock with the same font, typography (e.g. tabular numerals), formatting and DPI, measured without a
    /// width limit, so it is exactly what WPF itself would draw. A text that is genuinely narrower than that is reported.
    /// </summary>
    private static void CheckText(Window window, string visit)
    {
        var view = Descendants<Views.DiagnosticsView>(window).Single();
        foreach (var text in Descendants<TextBlock>(view))
        {
            if (!text.IsVisible || text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None || text.ActualWidth <= 0 || string.IsNullOrEmpty(text.Text)) continue;
            var natural = NaturalWidth(text);
            if (natural > text.ActualWidth + 1)
                TypographyValidation.Report(visit, $"Diagnostics text is clipped: '{text.Text}' needs {natural:0.0} DIP, has {text.ActualWidth:0.0} DIP " +
                    $"({text.FontFamily.Source} {text.FontSize:0.#} {text.FontWeight}, numerals {Typography.GetNumeralAlignment(text)}; {TypographyValidation.Where(text)})");
        }
    }

    private static readonly System.Reflection.FieldInfo[] TypographyProperties = typeof(Typography).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(DependencyProperty)).ToArray();

    /// <summary>Width of the text as a TextBlock with identical text properties would measure it without any width limit.</summary>
    internal static double NaturalWidth(TextBlock text)
    {
        var clone = Clone(text, TextWrapping.NoWrap);
        clone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return clone.DesiredSize.Width;
    }

    /// <summary>The height a text needs at the width it actually has: a copy with the same text properties, measured at <paramref name="width"/>.</summary>
    internal static double NeededHeight(TextBlock text, double width)
    {
        var clone = Clone(text, text.TextWrapping);
        clone.Measure(new Size(width, double.PositiveInfinity));
        return clone.DesiredSize.Height;
    }

    private static TextBlock Clone(TextBlock text, TextWrapping wrapping)
    {
        var clone = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontStyle = text.FontStyle, FontWeight = text.FontWeight,
            FontStretch = text.FontStretch, FlowDirection = text.FlowDirection, TextWrapping = wrapping, TextTrimming = wrapping == TextWrapping.NoWrap ? TextTrimming.None : text.TextTrimming,
            UseLayoutRounding = text.UseLayoutRounding, SnapsToDevicePixels = text.SnapsToDevicePixels,
            LineHeight = text.LineHeight, LineStackingStrategy = text.LineStackingStrategy, Padding = text.Padding,
        };
        // Every OpenType feature the original sets (tabular numerals and so on), and how its text is formatted and rendered.
        foreach (var field in TypographyProperties)
        {
            var property = (DependencyProperty)field.GetValue(null)!;
            clone.SetValue(property, text.GetValue(property));
        }
        TextOptions.SetTextFormattingMode(clone, TextOptions.GetTextFormattingMode(text));
        TextOptions.SetTextRenderingMode(clone, TextOptions.GetTextRenderingMode(text));
        // Not attached to a window, so give it the DPI of the original.
        VisualTreeHelper.SetRootDpi(clone, VisualTreeHelper.GetDpi(text));
        return clone;
    }

    /// <summary>The scroll viewer that hosts the current page (the one whose content is the page view model's view).</summary>
    internal static ScrollViewer? PageHost(Window window) => Descendants<ScrollViewer>(window).FirstOrDefault(v => v.Content is ContentControl { Content: PageViewModel });

    internal static void Capture(Window window, FrameworkElement visual, string path)
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
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
