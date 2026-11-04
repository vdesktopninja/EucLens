using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Providers;

namespace EucLens.App;

/// <summary>
/// Windows-only screenshots and layout checks of the Session History workspace, run inside --smoke-test after the other History sections. Four recordings are written through
/// the real writer (a short one with several findings, one with the same rule recurring, a long one, one with gaps and unavailable counters), opened through the real Analyze
/// action, and the page is drawn in both themes at 1366×768 and 1920×1080 with the available space of 100 %, 125 % and 150 %. The scale cases only change the space that
/// WPF has to lay the page out in: they do not change the Windows DPI of the machine (real desktop DPI needs a manual acceptance on a scaled display).
/// </summary>
internal static class HistoryUxValidation
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Set(DateTimeOffset value) => now = value;
    }

    /// <summary>One sampled instant of a scenario; NaN (or a null dirty-frame rate) is written as an unavailable counter, never as zero.</summary>
    private readonly record struct Reading(double Rtt, double Loss, double Fps, double? Dirty, double Dropped, double Cpu, double Ram);

    private sealed record Scenario(string Key, string Title, int Samples, int StepSeconds, Func<double, Reading> Shape, int[] IncidentSeconds, (int From, int To)[] Pauses);
    private sealed record Written(Scenario Scenario, string ShortId);

    private static bool In(double t, double from, double to) => t >= from && t < to;
    private static Reading Healthy(double t) => new(30 + t % 7, 0, 28 + t % 4, 20 + t % 5, 0.5, 22 + t % 9, 45);

    private static readonly Scenario[] Scenarios =
    [
        // A short session with findings in all three areas, one marked incident in the middle.
        new("short", "Short recording, several findings", 1200, 1, t => Healthy(t) with
        {
            Rtt = In(t, 300, 420) ? 420 + t % 11 : 30 + t % 7, Loss = In(t, 310, 400) ? 4 : 0, Cpu = In(t, 700, 800) ? 96 + t % 3 : 22 + t % 9,
            Fps = In(t, 900, 1000) ? 7 : 28 + t % 4, Dirty = In(t, 900, 1000) ? 30 : 20 + t % 5, Dropped = In(t, 900, 1000) ? 9 : 0.5
        }, [420, 950], []),
        // The same rules recurring: guest CPU three times, round-trip time twice, low frame rate once.
        new("recurring", "One rule, several occurrences", 1800, 1, t => Healthy(t) with
        {
            Cpu = In(t, 200, 300) || In(t, 700, 800) || In(t, 1300, 1420) ? 96 + t % 3 : 22 + t % 9,
            Rtt = In(t, 250, 330) || In(t, 1000, 1090) ? 210 + t % 13 : 30 + t % 7,
            Fps = In(t, 1500, 1600) ? 8 : 28 + t % 4, Dirty = In(t, 1500, 1600) ? 28 : 20 + t % 5
        }, [720], []),
        // Eight hours at one sample per 10 s, with a 20-minute pause and episodes spread over the session, some overlapping in time.
        new("long", "Long recording (8 h)", 2880, 10, t => Healthy(t) with
        {
            Rtt = t % 1800 is >= 600 and < 720 ? 200 + t % 17 : 30 + t % 7, Loss = t % 1800 is >= 640 and < 700 ? 3 : 0,
            Cpu = t % 2400 is >= 1200 and < 1330 ? 95 + t % 4 : 22 + t % 9,
            Fps = t % 5400 is >= 3000 and < 3160 ? 8 : 28 + t % 4, Dirty = t % 5400 is >= 3000 and < 3160 ? 30 : 20 + t % 5, Dropped = t % 5400 is >= 3000 and < 3160 ? 8 : 0.5
        }, [9000], [(1440, 1560)]),
        // Two pauses (gaps), a round-trip counter that returned nothing for a while, no dirty-frame counter at all, frame rate missing for a stretch.
        new("gaps", "Gaps and unavailable measurements", 900, 1, t => Healthy(t) with
        {
            Rtt = In(t, 350, 450) ? double.NaN : In(t, 280, 350) ? 230 : 30 + t % 7, Fps = In(t, 500, 600) ? double.NaN : 28 + t % 4, Dirty = null,
            Cpu = In(t, 100, 200) || In(t, 710, 830) ? 97 : 22 + t % 9
        }, [], [(200, 260), (600, 700)]),
    ];

    private static readonly List<string> Problems = [];
    private static void Check(bool condition, string problem) { if (!condition) { Problems.Add(problem); } }
    private static FrameworkElement Named(FrameworkElement view, string name) =>
        DesktopValidation.Descendants<FrameworkElement>(view).FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetName(e) == name) ?? throw new InvalidOperationException($"Nothing is named '{name}'.");

    /// <summary>A key as the keyboard delivers it: tunnelling first, then bubbling unless the preview handled it.</summary>
    private static void PressKey(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (target is HistoryTimeElement element) element.ModifierSource = () => modifiers;
        try
        {
            var source = PresentationSource.FromVisual(target)!;
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        finally { if (target is HistoryTimeElement e) e.ModifierSource = null; }
    }

    private static Metric M(double value, string unit, string reason) => double.IsNaN(value) ? Metric.Unavailable(unit, reason) : Metric.Available(value, unit);

    private static async Task<Written> WriteAsync(string directory, Scenario scenario, DateTimeOffset start, SessionSnapshot template)
    {
        var storage = new PhysicalRecordingStorage();
        var clock = new ManualClock(start);
        await using var writer = new RecordingWriter(() => new RecordingOptions
        {
            Directory = directory, QuotaBytes = 8L << 30, MaxAge = null, SegmentBytes = 4L << 20, MinimumFreeBytes = 64L << 20, CompressClosedParts = true
        }, storage, clock);
        var recorder = new SessionRecorder(writer, clock);
        recorder.Start(isDemo: false);
        for (var i = 0; i < scenario.Samples; i++)
        {
            var seconds = i * scenario.StepSeconds;
            var at = start.AddSeconds(seconds);
            clock.Set(at);
            foreach (var (from, to) in scenario.Pauses)
            {
                if (seconds == from) recorder.Mark(new(RecordingMarkKind.Paused, at, "Monitoring paused by the user."));
                if (seconds == to) recorder.Mark(new(RecordingMarkKind.Resumed, at, "Monitoring resumed.", TimeSpan.FromSeconds(to - from)));
            }
            if (scenario.Pauses.Any(p => seconds >= p.From && seconds < p.To)) continue;
            if (scenario.IncidentSeconds.Contains(seconds))
            {
                clock.Set(at - TimeSpan.FromSeconds(0.5));
                var result = await recorder.MarkIncidentAsync();
                if (!result.Saved) throw new InvalidOperationException("The scenario incident was not saved: " + result.Message);
                clock.Set(at);
            }
            var p = scenario.Shape(seconds);
            var blast = new Dictionary<string, Metric>
            {
                ["dirtyFps"] = p.Dirty is { } dirty ? Metric.Available(dirty, "FPS") : Metric.Unavailable("FPS", "The dirty-frame counter is not published by this agent")
            };
            recorder.Add(template with
            {
                Timestamp = at, Workload = null, DemoScenario = null, Diagnostics = null, HostMetrics = null, BlastMetrics = blast,
                Session = template.Session with { IsDemo = false, Protocol = RemoteProtocol.OmnissaBlast },
                Connection = template.Connection with { RoundTripTime = M(p.Rtt, "ms", "Counter could not be read in this sample"), PacketLoss = Metric.Available(p.Loss, "%") },
                System = template.System with { CpuUtilization = Metric.Available(p.Cpu, "%"), RamUtilization = Metric.Available(p.Ram, "%") },
                Graphics = template.Graphics with { FramesPerSecond = M(p.Fps, "FPS", "Counter could not be read in this sample"), DroppedFramesPercent = Metric.Available(p.Dropped, "%") }
            });
            if (i % 500 == 499) await writer.FlushAsync();
        }
        await recorder.StopAsync();
        return new(scenario, RecordingFormat.ShortId(recorder.SinkStatus!.RecordingId ?? throw new InvalidOperationException("The scenario recording has no id.")));
    }

    private static async Task OpenAsync(HistorySmokeContext c, Written written)
    {
        await c.Vm.Background.RefreshRecordingsAsync();
        var item = c.Vm.Background.Recordings.SingleOrDefault(r => r.Summary.ShortId == written.ShortId) ?? throw new InvalidOperationException($"Recording {written.ShortId} is not listed.");
        c.Vm.SelectedPage = c.Vm.Pages[0];
        await HistoryValidation.Idle(c);
        item.AnalyzeCommand.Execute(null);
        HistoryValidation.Require(c.Vm.SelectedPage is SessionHistoryPage, "Analyze did not open the Session History page.");
        await HistoryValidation.WaitFor(() => HistoryValidation.History(c).HasHistory || HistoryValidation.History(c).HasError, "the recording to load");
        HistoryValidation.Require(HistoryValidation.History(c).HasHistory, "The recording did not load: " + HistoryValidation.History(c).Error);
        await HistoryValidation.WaitFor(() => HistoryValidation.Charts(c).Length == HistoryValidation.History(c).Charts.Count && HistoryValidation.Charts(c).Length >= 1, "the history charts");
        await HistoryValidation.History(c).Settled;
        await HistoryValidation.Idle(c);
        await DesktopValidation.WaitForChartMeasureAsync(c.Window);
        await Task.Delay(150);
    }

    public static async Task RunAsync(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        Directory.CreateDirectory(directory);
        var shots = Path.Combine(c.EvidenceDirectory, "workspace");
        Directory.CreateDirectory(shots);
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        var written = new List<Written>();
        Problems.Clear();
        var start = new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero);
        foreach (var scenario in Scenarios) written.Add(await WriteAsync(directory, scenario, start.AddDays(written.Count), template));
        HistoryValidation.Log(c, $"PASS: workspace scenarios written through the real writer: {string.Join(", ", written.Select(w => $"{w.Scenario.Key} {w.ShortId}"))}");

        var window = c.Window;
        var client = (FrameworkElement)window.Content;
        var chromeWidth = window.ActualWidth - client.ActualWidth; var chromeHeight = window.ActualHeight - client.ActualHeight;
        var (originalWidth, originalHeight, originalClientWidth, originalClientHeight) = (window.Width, window.Height, client.Width, client.Height);
        var host = new Canvas { DataContext = c.Vm };
        window.Content = null; host.Children.Add(client); window.Content = host;
        try
        {
            foreach (var item in written)
            {
                await OpenAsync(c, item);
                var findings = HistoryValidation.History(c).Findings;
                HistoryValidation.Log(c, $"NOTE: workspace {item.Scenario.Key}: {findings.Observations.Count} observation(s) ({string.Join(", ", findings.Observations.GroupBy(o => o.Observation.RuleId).Select(g => $"{g.Key} ×{g.Count()}"))}), {findings.PerformanceCorrelations.Count} performance correlation(s)");
                foreach (var dark in new[] { false, true })
                {
                    c.Vm.IsDark = dark;
                    foreach (var (width, height) in new[] { (1366, 768), (1920, 1080) })
                    foreach (var scale in new[] { 1d, 1.25d, 1.5d })
                    {
                        window.Width = width / scale; window.Height = height / scale;
                        client.Width = width / scale - chromeWidth; client.Height = height / scale - chromeHeight;
                        await HistoryValidation.Idle(c);
                        await Task.Delay(120);
                        await DesktopValidation.WaitForChartMeasureAsync(window);
                        await Layout(c, item.Scenario.Key, dark, width, height, scale, client);
                        var tag = $"ux-{item.Scenario.Key}-{(dark ? "dark" : "light")}-{width}x{height}-{scale * 100:0}pct";
                        await HistoryValidation.Shot(c, HistoryValidation.View(c), Path.Combine(shots, tag + ".png"));
                        if (scale == 1d && item.Scenario.Key is "short" or "recurring" && findings.VisibleObservations.Count > 0)
                        {
                            findings.SelectedObservation = findings.VisibleObservations.First();
                            await HistoryValidation.Idle(c); await Task.Delay(250); await DesktopValidation.WaitForChartMeasureAsync(window);
                            await HistoryValidation.Shot(c, HistoryValidation.View(c), Path.Combine(shots, tag.Replace("ux-", "ux-selected-") + ".png"));
                            findings.ShowFull();
                            await HistoryValidation.Idle(c); await Task.Delay(150);
                        }
                    }
                }
            }
            await Interactions(c, written.Single(w => w.Scenario.Key == "recurring"), client, chromeWidth, chromeHeight);
            if (Problems.Count > 0) throw new InvalidOperationException($"{Problems.Count} layout problem(s): {string.Join(" || ", Problems.Take(6))}");
            HistoryValidation.Log(c, $"PASS: workspace screenshots written: {Directory.GetFiles(shots, "*.png").Length} (both themes, 1366×768 and 1920×1080, available space of 100 %, 125 % and 150 %; the scale cases do not change the Windows DPI)");
        }
        finally
        {
            host.Children.Remove(client);
            client.Width = originalClientWidth; client.Height = originalClientHeight;
            window.Content = client; window.Width = originalWidth; window.Height = originalHeight;
            c.Vm.IsDark = true;
            c.Vm.SelectedPage = c.Vm.Pages[0];
        }
    }

    /// <summary>
    /// What must hold in every state: the overview tiles, the timeline, the findings panel and their buttons fit the page; the timeline's bars sit inside its tracks, keep at least
    /// their minimum width and never overlap inside a lane; nothing runs out of a panel; on a 100 % display the overview and the whole timeline are on the first screen. Problems are
    /// collected and reported together so one run shows every state that fails.
    /// </summary>
    private static Task Layout(HistorySmokeContext c, string scenario, bool dark, int width, int height, double scale, FrameworkElement client)
    {
        var view = HistoryValidation.View(c);
        var label = $"{scenario} {(dark ? "dark" : "light")} {width}×{height} at {scale:P0}";
        var viewport = HistoryValidation.PageViewport(view);
        ((ScrollViewer)viewport).ScrollToTop();
        view.UpdateLayout();
        var findings = HistoryValidation.History(c).Findings;
        var timeline = DesktopValidation.Descendants<FindingsTimeline>(view).Single();
        var bars = timeline.DrawnBars(); var tracks = timeline.DrawnTracks();
        Check(timeline.ActualWidth >= 320 && timeline.ActualHeight > 40 && timeline.ActualHeight <= 300, $"{label}: the performance timeline is {timeline.ActualWidth:0}×{timeline.ActualHeight:0} DIPs.");
        Check(bars.Count == findings.TimelineBars.Count, $"{label}: the timeline draws {bars.Count} bars for {findings.TimelineBars.Count} findings.");
        foreach (var (id, domain, lane, rect) in bars)
        {
            var track = tracks.Single(t => t.Domain == domain);
            Check(rect.Left >= FindingsTimeline.LabelColumn - 1 && rect.Right <= timeline.ActualWidth + 1 && rect.Width >= FindingsTimeline.MinimumBar - 0.01, $"{label}: bar {id} is outside the axis or narrower than {FindingsTimeline.MinimumBar} DIPs ({rect.Left:0.0}–{rect.Right:0.0}).");
            Check(rect.Top >= track.Top - 0.5 && rect.Bottom <= track.Top + track.Height + 0.5, $"{label}: bar {id} lies outside its track.");
        }
        foreach (var group in bars.GroupBy(b => (b.Domain, b.Lane)))
            foreach (var pair in group.OrderBy(b => b.Rect.Left).Zip(group.OrderBy(b => b.Rect.Left).Skip(1)))
                Check(pair.First.Rect.Right <= pair.Second.Rect.Left + 0.01, $"{label}: bars {pair.First.Id} and {pair.Second.Id} overlap in one lane.");
        foreach (var name in new[] { "Recording overview", "Performance timeline", "Performance findings" })
        {
            var panel = Named(view, name);
            var outside = DesktopValidation.Descendants<TextBlock>(panel).Where(t => t.IsVisible && t.ActualWidth > 0 && t.Text.Length > 0 && t.TranslatePoint(new Point(t.ActualWidth, 0), panel).X > panel.ActualWidth + 1).Select(t => t.Text).ToArray();
            Check(outside.Length == 0, $"{label}: text runs out of '{name}': {string.Join(" | ", outside.Take(3))}");
            foreach (var button in DesktopValidation.Descendants<Button>(panel).Where(b => b.IsVisible))
                Check(button.TranslatePoint(new Point(button.ActualWidth, 0), panel).X <= panel.ActualWidth + 1, $"{label}: button '{button.Content}' is clipped by '{name}'.");
        }
        var tiles = DesktopValidation.Descendants<ItemsControl>(Named(view, "Recording overview")).First().Items.Count;
        Check(tiles == 8, $"{label}: the overview shows {tiles} tiles, not 8.");
        var list = view.FindingsListControl;
        Check(list.ActualHeight <= HistoryLayout.FindingsListMaxHeight + 1, $"{label}: the findings list is {list.ActualHeight:0} DIPs tall.");
        foreach (var name in new[] { "Export findings as CSV", "Export findings as JSON", "Export incidents as CSV", "Export timeline as CSV", "Show full recording" })
        {
            var button = DesktopValidation.Descendants<Button>(view).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
            Check(button is { IsVisible: true, IsTabStop: true, ActualWidth: >= 40 }, $"{label}: button '{name}' is missing or unusable.");
        }
        if (scale == 1d)
        {
            var bottom = timeline.TranslatePoint(new Point(0, timeline.ActualHeight), viewport).Y;
            Check(bottom <= viewport.ActualHeight, $"{label}: the overview and the performance timeline do not fit the first screen (the timeline ends at {bottom:0} of {viewport.ActualHeight:0} DIPs).");
            var export = DesktopValidation.Descendants<Button>(view).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Export findings as CSV");
            var top = export.TranslatePoint(new Point(0, 0), viewport).Y;
            Check(top < viewport.ActualHeight, $"{label}: the export area is below the first screen ({top:0} of {viewport.ActualHeight:0}).");
        }
        HistoryValidation.Log(c, string.Create(CultureInfo.InvariantCulture, $"NOTE: layout {label}: client {client.ActualWidth:0}×{client.ActualHeight:0} DIP, page {view.ActualWidth:0}×{view.ActualHeight:0}, timeline {timeline.ActualWidth:0}×{timeline.ActualHeight:0} with {bars.Count} bars in {tracks.Count} tracks, {list.Items.Count} list lines"));
        return Task.CompletedTask;
    }

    /// <summary>Clicks, keys and the group gestures on the real page: timeline to list to charts, and the keyboard.</summary>
    private static async Task Interactions(HistorySmokeContext c, Written item, FrameworkElement client, double chromeWidth, double chromeHeight)
    {
        await OpenAsync(c, item);
        var window = c.Window;
        window.Width = 1920; window.Height = 1080; client.Width = 1920 - chromeWidth; client.Height = 1080 - chromeHeight;
        c.Vm.IsDark = false;
        await HistoryValidation.Idle(c); await Task.Delay(150); await DesktopValidation.WaitForChartMeasureAsync(window);
        var h = HistoryValidation.History(c); var f = h.Findings; var view = HistoryValidation.View(c);
        var timeline = DesktopValidation.Descendants<FindingsTimeline>(view).Single();
        var list = view.FindingsListControl;
        ((ScrollViewer)HistoryValidation.PageViewport(view)).ScrollToTop(); await HistoryValidation.Idle(c);

        // A real click on the last guest-CPU bar: the finding is selected, its group opens, the list shows its line, the charts move to its interval.
        var target = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).Last();
        var bar = timeline.DrawnBars().Single(b => b.Id == target.Id);
        var at = new Point(bar.Rect.Left + bar.Rect.Width / 2, bar.Rect.Top + bar.Rect.Height / 2);
        timeline.PointerSource = _ => at;
        timeline.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        timeline.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        timeline.PointerSource = null;
        await h.Settled; await HistoryValidation.Idle(c); await Task.Delay(250);
        HistoryValidation.Require(ReferenceEquals(f.SelectedObservation, target), "A click on the timeline bar did not select the finding.");
        HistoryValidation.Require(f.FindingRows.Any(r => r.IsGroup && r.RuleId == "guest.cpu.saturation" && r.IsExpanded), "The group of the clicked finding did not open.");
        HistoryValidation.Require(ReferenceEquals(list.SelectedItem, f.SelectedRow) && f.SelectedRow?.Item == target, "The list does not show the finding selected on the timeline.");
        HistoryValidation.Require(timeline.SelectedId == target.Id && h.ViewStart <= target.Observation.Start && h.ViewEnd >= target.Observation.End, "The timeline selection or the charts do not follow the click.");
        var overlay = DesktopValidation.Descendants<HistoryChartOverlay>(view).Single();
        HistoryValidation.Require(overlay.HighlightStart == target.Observation.Start && overlay.HighlightEnd == target.Observation.End, "The charts do not highlight the clicked finding's own interval.");

        // Ctrl+Left steps to the previous finding in time order.
        var ordered = f.VisibleObservations.OrderBy(o => o.Observation.Start).ThenBy(o => o.Observation.End).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var index = Array.IndexOf(ordered, target);
        timeline.Focus(); PressKey(timeline, Key.Left, ModifierKeys.Control); await HistoryValidation.Idle(c);
        HistoryValidation.Require(index > 0 && ReferenceEquals(f.SelectedObservation, ordered[index - 1]), "Ctrl+Left on the timeline did not select the previous finding.");

        // The group header from the keyboard: choosing it selects the group and opens nothing, Right opens it, Left closes it, Enter toggles.
        f.ShowFull(); await HistoryValidation.Idle(c);
        f.ToggleGroup("guest.cpu.saturation"); await HistoryValidation.Idle(c);           // closed again for the test
        var header = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        var occurrencesBefore = f.FindingRows.Count(r => r.IsOccurrence);
        list.Focus(); list.SelectedItem = header; await HistoryValidation.Idle(c);
        HistoryValidation.Require(f.HasSelectedGroup && f.SelectedObservation is null && !header.IsExpanded && f.FindingRows.Count(r => r.IsOccurrence) == occurrencesBefore, "Choosing a header opened it or selected an observation.");
        PressKey(list, Key.Right); await HistoryValidation.Idle(c);
        HistoryValidation.Require(f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation").IsExpanded && f.FindingRows.Count(r => r.IsOccurrence) == occurrencesBefore + 3, "Right arrow did not open the group.");
        HistoryValidation.Require(f.HasSelectedGroup && ReferenceEquals(list.SelectedItem, f.SelectedRow), "Opening the group lost the selection on its header.");
        PressKey(list, Key.Left); await HistoryValidation.Idle(c);
        HistoryValidation.Require(!f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation").IsExpanded && f.FindingRows.Count(r => r.IsOccurrence) == occurrencesBefore, "Left arrow did not close the group.");
        PressKey(list, Key.Enter); await HistoryValidation.Idle(c);
        HistoryValidation.Require(f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation").IsExpanded, "Enter did not open the group.");
        // A click on the header line toggles it as well.
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(f.SelectedRow)!;
        container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = container });
        await HistoryValidation.Idle(c);
        HistoryValidation.Require(!f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation").IsExpanded, "A click on the header line did not close the group.");

        // Occurrences stay separately selectable.
        f.ToggleGroup("guest.cpu.saturation"); await HistoryValidation.Idle(c);
        var occurrence = f.FindingRows.Where(r => r.IsOccurrence).ElementAt(1);
        list.SelectedItem = occurrence; await h.Settled; await HistoryValidation.Idle(c);
        HistoryValidation.Require(ReferenceEquals(f.SelectedObservation, occurrence.Item) && timeline.SelectedId == occurrence.Item!.Id, "An occurrence is not selectable on its own.");
        f.ShowFull(); await HistoryValidation.Idle(c);
        HistoryValidation.Log(c, "PASS: workspace interactions: a click on a timeline bar selects the finding, opens its group, scrolls the list and moves the charts to its own interval; Ctrl+Left steps back; a header chosen from the keyboard selects the group without opening it, Right, Left and Enter open and close it, a click toggles it; every occurrence is selectable on its own");
    }
}
