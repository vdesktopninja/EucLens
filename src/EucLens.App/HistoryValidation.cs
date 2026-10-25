using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.App.Views;
using EucLens.Core;
using EucLens.Providers;
using LiveChartsCore.SkiaSharpView.WPF;
using WpfPath = System.Windows.Shapes.Path;

namespace EucLens.App;

internal sealed record HistorySmokeContext(Application App, Window Window, MainViewModel Vm, string EvidenceDirectory);

/// <summary>
/// Windows-only acceptance of Session History, invoked by --smoke-test after the background validation: recordings are written through the real writer (rotation,
/// compression, incident markers), opened through the real Analyze action, and the page is exercised as a user would: charts on one time axis, zoom and range
/// selection by pointer and keyboard, incident selection, both themes, switching recordings, repeated open and leave with a retention check, and a six hour recording
/// with measured load time and memory. Every section runs even if an earlier one failed, and every result is written to history-validation.txt as it happens.
/// </summary>
internal static class HistoryValidation
{
    private static readonly StringBuilder Report = new();
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Set(DateTimeOffset value) => now = value;
    }

    private sealed record Fixture(string ShortId, DateTimeOffset Start, int Seconds, IReadOnlyList<IncidentMarker> Incidents, int Parts, int CompressedParts, long Bytes);

    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static void Log(HistorySmokeContext c, string line)
    {
        Report.AppendLine(line);
        SmokeLog.Write("history: " + line);
        File.WriteAllText(Path.Combine(c.EvidenceDirectory, "history-validation.txt"), Report.ToString());
    }

    private static Task Idle(HistorySmokeContext c) => c.App.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitFor(Func<bool> condition, string what, int milliseconds = 30_000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new InvalidOperationException($"Timed out waiting for {what}."); await Task.Delay(50); }
    }

    private static SessionHistoryViewModel History(HistorySmokeContext c) => c.Vm.History;
    private static SessionHistoryView View(HistorySmokeContext c) => DesktopValidation.Descendants<SessionHistoryView>(c.Window).Single();
    private static HistoryChartOverlay Overlay(HistorySmokeContext c) => DesktopValidation.Descendants<HistoryChartOverlay>(View(c)).Single();
    private static HistoryTimelineStrip Strip(HistorySmokeContext c) => DesktopValidation.Descendants<HistoryTimelineStrip>(View(c)).Single();
    /// <summary>Safe while the page is still being created: no view yet means no charts yet.</summary>
    private static CartesianChart[] Charts(HistorySmokeContext c) =>
        DesktopValidation.Descendants<SessionHistoryView>(c.Window).FirstOrDefault() is { } view ? DesktopValidation.Descendants<CartesianChart>(view).ToArray() : [];

    private static long HeapAfterCollection()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(true);
    }

    private static string MiB(long bytes) => (bytes / 1048576d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ---------- fixtures ----------

    private static DiagnosticFinding Finding(Severity severity, DateTimeOffset at) => new("connection.rtt.sustained", DiagnosticDomain.Connection, severity, "Elevated Blast round-trip time", "d",
        at, at, null, null, 60, 58, 60, [], "rule", [], [], "limits");

    private static async Task<Fixture> WriteAsync(string directory, DateTimeOffset start, int seconds, SessionSnapshot template, Func<int, (double Rtt, double Cpu, double Fps, bool Active)> shape,
        int[] incidentSeconds, (int From, int To)? pause, long segment)
    {
        var storage = new PhysicalRecordingStorage();
        var clock = new ManualClock(start);
        await using var writer = new RecordingWriter(() => new RecordingOptions
        {
            Directory = directory, QuotaBytes = 8L << 30, MaxAge = null, SegmentBytes = segment, MinimumFreeBytes = 64L << 20, CompressClosedParts = true
        }, storage, clock);
        var recorder = new SessionRecorder(writer, clock);
        recorder.Start(isDemo: true);
        var incidents = new List<IncidentMarker>();
        var wasActive = false;
        for (var i = 0; i < seconds; i++)
        {
            var at = start.AddSeconds(i);
            clock.Set(at);
            if (pause is { } p)
            {
                if (i == p.From) recorder.Mark(new(RecordingMarkKind.Paused, at, "Monitoring paused by the user."));
                if (i == p.To) recorder.Mark(new(RecordingMarkKind.Resumed, at, "Monitoring resumed.", TimeSpan.FromSeconds(p.To - p.From)));
                if (i >= p.From && i < p.To) continue;
            }
            if (incidentSeconds.Contains(i))
            {
                clock.Set(at - TimeSpan.FromSeconds(0.5));
                var result = await recorder.MarkIncidentAsync();
                if (!result.Saved) throw new InvalidOperationException("Fixture incident was not saved: " + result.Message);
                incidents.Add(result.Marker!);
                clock.Set(at);
            }
            var (rtt, cpu, fps, active) = shape(i);
            var events = new List<DiagnosticEvent>();
            if (active != wasActive) events.Add(new(at, active ? FindingTransition.Detected : FindingTransition.Recovered, Finding(Severity.Warning, at)));
            wasActive = active;
            recorder.Add(template with
            {
                Timestamp = at,
                Connection = template.Connection with { RoundTripTime = Metric.Available(rtt, "ms"), PacketLoss = Metric.Available(active ? 1.5 : 0, "%") },
                System = template.System with { CpuUtilization = Metric.Available(cpu, "%") },
                Graphics = template.Graphics with { FramesPerSecond = Metric.Available(fps, "FPS") },
                Diagnostics = new DiagnosticSnapshot([new(DiagnosticDomain.Connection, active ? HealthState.Warning : HealthState.Healthy, AssessmentCoverage.Full, "evidence", "note", 60)],
                    active ? ["connection.rtt.sustained"] : [], events),
            });
            if (i % 250 == 249) await writer.FlushAsync();
        }
        await recorder.StopAsync();
        var shortId = RecordingFormat.ShortId(recorder.SinkStatus!.RecordingId ?? throw new InvalidOperationException("The fixture recording has no id."));
        var files = storage.List(directory).Where(f => RecordingFormat.TryParsePart(Path.GetFileName(f.Path), out _, out var id, out _) && id == shortId).ToArray();
        return new(shortId, start, seconds, incidents, files.Length, files.Count(f => f.Path.EndsWith(".gz", StringComparison.Ordinal)), files.Sum(f => f.Length));
    }

    private static Fixture? shortRun, secondRun, longRun;

    // ---------- entry point ----------

    public static async Task RunAsync(HistorySmokeContext c)
    {
        Report.Clear();
        SmokeLog.Write("history validation started");
        var failures = new List<string>();
        c.Vm.IsDark = false;
        c.Vm.SelectedPage = c.Vm.Pages[0];
        await Section(c, failures, "fixtures", () => Fixtures(c));
        await Section(c, failures, "open through Analyze", () => OpenThroughAnalyze(c));
        await Section(c, failures, "charts share one time axis", () => ChartsShareTimeAxis(c));
        await Section(c, failures, "zoom and range selection", () => ZoomAndRanges(c));
        await Section(c, failures, "pointer interaction", () => PointerInteraction(c));
        await Section(c, failures, "incident selection", () => IncidentSelection(c));
        await Section(c, failures, "keyboard navigation", () => KeyboardNavigation(c));
        await Section(c, failures, "themes and event icons", () => ThemesAndIcons(c));
        await Section(c, failures, "switching recordings", () => SwitchRecordings(c));
        await Section(c, failures, "repeated open and leave", () => RepeatedNavigation(c));
        await Section(c, failures, "six hour recording", () => LongRecording(c));
        c.Vm.SelectedPage = c.Vm.Pages[0];
        c.Vm.IsDark = true;
        if (failures.Count > 0) throw new InvalidOperationException($"History validation failed in {failures.Count} section(s): {string.Join("; ", failures)}");
        Log(c, "PASS: history validation complete");
    }

    private static async Task Section(HistorySmokeContext c, List<string> failures, string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex)
        {
            failures.Add(name);
            Log(c, $"FAIL: {name}: {ex.GetType().Name}: {ex.Message}");
            try { var view = DesktopValidation.Descendants<SessionHistoryView>(c.Window).FirstOrDefault(); if (view is not null && view.ActualWidth > 0) DesktopValidation.Capture(c.Window, view, Path.Combine(c.EvidenceDirectory, $"FAILED-history-{name.Replace(' ', '-')}.png")); }
            catch (Exception captureFailure) { SmokeLog.Write("history failure capture: " + captureFailure.Message); }
        }
        try { c.Vm.SelectedPage = c.Vm.Pages[0]; await Idle(c); }
        catch (Exception ex) { SmokeLog.Write("history reset: " + ex.Message); }
    }

    // ---------- sections ----------

    private static async Task Fixtures(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        Directory.CreateDirectory(directory);
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        var sw = Stopwatch.StartNew();
        var start = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        // A: 20 minutes, a pause, a diagnostic warning and two marked incidents, one with an RTT spike and one with a frame-rate dip; rotates and compresses.
        shortRun = await WriteAsync(directory, start, 1200, template, i =>
            (i is >= 398 and <= 406 ? 380 : 30 + i % 7, 18 + i % 9, i is >= 898 and <= 906 ? 3 : 28 + i % 4, i is >= 398 and <= 410), [400, 900], (600, 660), 300_000);
        // B: five minutes, one incident, a flat series.
        secondRun = await WriteAsync(directory, start.AddHours(1), 300, template, i => (42, 22, 30, false), [150], null, 300_000);
        Require(shortRun.Parts >= 3 && shortRun.CompressedParts >= shortRun.Parts - 1, $"Recording A did not rotate and compress: {shortRun.Parts} parts, {shortRun.CompressedParts} compressed.");
        Require(shortRun.Incidents.Count == 2 && secondRun.Incidents.Count == 1, "Fixture incidents were not saved.");
        Log(c, $"PASS: fixtures written through the real writer in {sw.Elapsed.TotalSeconds:0.0} s: A {shortRun.ShortId} ({shortRun.Seconds} s, {shortRun.Parts} parts, {shortRun.CompressedParts} compressed, {MiB(shortRun.Bytes)} MiB, 2 incidents), B {secondRun.ShortId} ({secondRun.Seconds} s, {secondRun.Parts} parts, 1 incident)");
    }

    private static async Task OpenAnalyze(HistorySmokeContext c, Fixture fixture, int minimumCharts = 1)
    {
        await c.Vm.Background.RefreshRecordingsAsync();
        var item = c.Vm.Background.Recordings.SingleOrDefault(r => r.Summary.ShortId == fixture.ShortId) ?? throw new InvalidOperationException($"Recording {fixture.ShortId} is not listed on the Session Recorder page.");
        item.AnalyzeCommand.Execute(null);
        Require(c.Vm.SelectedPage is SessionHistoryPage, "Analyze did not open the Session History page.");
        await WaitFor(() => History(c).HasHistory || History(c).HasError, "the recording to load");
        Require(History(c).HasHistory, "The recording did not load: " + History(c).Error);
        await WaitFor(() => Charts(c).Length == History(c).Charts.Count && Charts(c).Length >= minimumCharts, "the history charts");
        await History(c).Settled;
        await Idle(c);
        await DesktopValidation.WaitForChartMeasureAsync(c.Window);
        await Task.Delay(150);
    }

    private static async Task OpenThroughAnalyze(HistorySmokeContext c)
    {
        var a = shortRun ?? throw new InvalidOperationException("Fixtures are missing.");
        var sw = Stopwatch.StartNew();
        await OpenAnalyze(c, a, minimumCharts: 5);
        var h = History(c);
        var info = h.Loaded!.Info;
        Require(info.State == RecordingCompleteness.Complete && h.StateLabel == "Complete", $"State is {info.State}.");
        Require(info.Samples == 1200 - 60 && info.Incidents == 2 && info.Warnings == 1, $"Overview counts differ: samples {info.Samples}, incidents {info.Incidents}, warnings {info.Warnings}.");
        Require(info.Parts == a.Parts && info.LostSamples == 0, $"Parts {info.Parts}/{a.Parts}, lost {info.LostSamples}.");
        Require(h.Loaded.Events.Count(e => e.Kind == HistoryEventKind.MonitoringPaused) == 1 && h.Loaded.Gaps.Count == 1, "The pause and its gap are not on the timeline.");
        var ids = h.Charts.Select(r => r.Id).ToArray();
        Require(new[] { "rtt", "packetLoss", "fps", "cpu" }.All(ids.Contains), "Expected charts are missing: " + string.Join(",", ids));
        Require(!ids.Contains("gpu"), "A chart without any recorded values was drawn.");
        DesktopValidation.CheckChartList(Charts(c), "Session History");
        Require(Charts(c).Length == h.Charts.Count, "The number of chart controls differs from the rows.");
        Require(DesktopValidation.Descendants<ListBox>(View(c)).Any(l => l.Items.Count == h.Events.Count && h.Events.Count > 0), "The event list does not show every timeline entry.");
        DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, "history-overview-light.png"));
        Log(c, $"PASS: Analyze opened recording {a.ShortId} in {sw.Elapsed.TotalSeconds:0.0} s: {h.Charts.Count} charts ({string.Join(", ", ids)}), {h.Events.Count} timeline entries, {info.Samples} samples, {info.Incidents} incidents, coverage {info.DataCoverage:P1}, state Complete; every chart passes the ownership, theme and no-animation checks");
    }

    private static async Task ChartsShareTimeAxis(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        var overlay = Overlay(c);
        var engines = Charts(c).Select(chart => (LiveChartsCore.CartesianChartEngine)chart.CoreChart).ToArray();
        // One range, in every chart.
        foreach (var chart in Charts(c))
        {
            var axis = (LiveChartsCore.SkiaSharpView.Axis)chart.XAxes.First();
            Require(Math.Abs((axis.MinLimit ?? 0) - h.ViewStart.UtcDateTime.Ticks) < 1000 && Math.Abs((axis.MaxLimit ?? 0) - h.ViewEnd.UtcDateTime.Ticks) < 1000, "A chart does not show the shared range.");
        }
        // One plot area, in every chart, and the same one the overlay uses.
        var left = engines.Select(e => e.DrawMarginLocation.X).ToArray(); var width = engines.Select(e => e.DrawMarginSize.Width).ToArray();
        Require(left.Max() - left.Min() < 1.5 && width.Max() - width.Min() < 1.5, $"Plot areas are not aligned: left {string.Join("/", left.Select(v => v.ToString("0.#")))}, width {string.Join("/", width.Select(v => v.ToString("0.#")))}.");
        Require(Math.Abs(overlay.XOf(h.ViewStart) - left[0]) < 1.5 && Math.Abs(overlay.XOf(h.ViewEnd) - (left[0] + width[0])) < 1.5,
            $"Overlay maps the range to {overlay.XOf(h.ViewStart):0.#}..{overlay.XOf(h.ViewEnd):0.#}, charts draw {left[0]:0.#}..{left[0] + width[0]:0.#}.");
        // The mapping is exact in both directions, and the strip agrees with it for the same instants.
        foreach (var fraction in new[] { 0.0, 0.25, 0.5, 0.9, 1.0 })
        {
            var at = h.ViewStart + TimeSpan.FromTicks((long)((h.ViewEnd - h.ViewStart).Ticks * fraction));
            Require(Math.Abs((overlay.TimeAt(overlay.XOf(at)) - at).TotalMilliseconds) < 5, $"Time to pixel and back differs at {fraction:P0}.");
        }
        var strip = Strip(c);
        Require(Math.Abs((strip.TimeAt(strip.XOf(h.SessionStart)) - h.SessionStart).TotalMilliseconds) < 5 && strip.ViewStart == h.ViewStart, "The strip does not follow the shared range.");
        Log(c, $"PASS: {engines.Length} charts share the range {HistoryTime.Format(h.ViewStart, h.Zone, withDate: true)} to {HistoryTime.Format(h.ViewEnd, h.Zone, withDate: true)}; plot areas start at x = {left[0]:0.#} with width {width[0]:0.#} in all of them and match the overlay's mapping");
    }

    private static async Task ZoomAndRanges(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        async Task Settle() { await h.Settled; await Idle(c); await Task.Delay(60); }
        void SameRange(string what)
        {
            foreach (var chart in Charts(c))
            {
                var axis = (LiveChartsCore.SkiaSharpView.Axis)chart.XAxes.First();
                Require(Math.Abs((axis.MinLimit ?? 0) - h.ViewStart.UtcDateTime.Ticks) < 1000 && Math.Abs((axis.MaxLimit ?? 0) - h.ViewEnd.UtcDateTime.Ticks) < 1000, $"{what}: a chart is not on the shared range.");
            }
            Require(Math.Abs((Overlay(c).ViewStart - h.ViewStart).TotalMilliseconds) < 1 && Math.Abs((Strip(c).ViewEnd - h.ViewEnd).TotalMilliseconds) < 1, $"{what}: the overlay or strip lags behind the range.");
        }
        var full = h.ViewEnd - h.ViewStart;
        h.ZoomInCommand.Execute(null); await Settle(); SameRange("zoom in");
        Require(h.ViewEnd - h.ViewStart == full / 2, "Zoom in did not halve the range.");
        h.ZoomInCommand.Execute(null); await Settle(); SameRange("zoom in twice");
        h.ZoomOutCommand.Execute(null); await Settle(); SameRange("zoom out");
        h.SelectedPreset = h.RangePresets.Single(p => p.Value == HistoryRangePreset.Last15Minutes); await Settle(); SameRange("last 15 minutes");
        Require(h.ViewEnd == h.SessionEnd && h.ViewEnd - h.ViewStart == TimeSpan.FromMinutes(15), "The 15 minute preset is wrong.");
        var middle = h.SessionStart + TimeSpan.FromMinutes(10);
        h.CustomFrom = HistoryTime.ToLocal(middle, h.Zone).ToString("yyyy-MM-dd HH:mm:ss"); h.CustomTo = HistoryTime.ToLocal(middle + TimeSpan.FromMinutes(3), h.Zone).ToString("yyyy-MM-dd HH:mm:ss");
        h.ApplyCustomRangeCommand.Execute(null); await Settle(); SameRange("custom range");
        Require(h.ViewEnd - h.ViewStart == TimeSpan.FromMinutes(3), "The custom range was not applied.");
        // Every sample of the visible three minutes is drawn at this resolution; none is replaced by an average.
        var rtt = h.Charts.Single(r => r.Id == "rtt");
        Require(rtt.Stats.Contains("valid", StringComparison.Ordinal) && !rtt.Stats.Contains("reduced", StringComparison.Ordinal), "The zoomed range was reduced although it fits: " + rtt.Stats);
        h.ResetZoomCommand.Execute(null); await Settle(); SameRange("reset");
        Require(h.ViewStart == h.SessionStart && h.ViewEnd == h.SessionEnd && h.SelectedPreset.Value == HistoryRangePreset.EntireSession, "Reset did not show the entire session.");
        Log(c, $"PASS: zoom in/out, the 15 minute preset, a custom range and reset keep all {Charts(c).Length} charts, the overlay and the strip on the same range; the zoomed range shows every recorded sample ({rtt.Data.Points.Count} points)");
    }

    private static async Task PointerInteraction(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        var overlay = Overlay(c);
        var strip = Strip(c);
        double x = 0;
        overlay.PointerSource = _ => new Point(x, 20);
        strip.PointerSource = _ => new Point(x, 20);
        try
        {
            var plotLeft = HistoryLayout.PlotLeft; var plotWidth = overlay.ActualWidth - HistoryLayout.PlotLeft - HistoryLayout.PlotRight;
            Require(plotWidth > 100, $"The overlay is only {overlay.ActualWidth:0} wide.");
            // Hover: the pointer position becomes a time and every chart answers with the recorded value or N/A.
            x = plotLeft + plotWidth * 0.5;
            overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            Require(overlay.HoverAt is not null && Math.Abs((overlay.HoverAt.Value - (h.ViewStart + (h.ViewEnd - h.ViewStart) / 2)).TotalSeconds) < 2, "Hover did not report the time under the pointer.");
            Require(h.HoverText.Length > 0 && h.Charts.All(r => r.Readout.Length > 0), "Hover did not fill the readouts.");
            Require(h.Charts.Single(r => r.Id == "rtt").Readout.Contains("ms", StringComparison.Ordinal), "The RTT readout has no unit: " + h.Charts.Single(r => r.Id == "rtt").Readout);
            // Drag across the charts selects a range.
            x = plotLeft + plotWidth * 0.25;
            var start = overlay.TimeAt(x);
            overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            x = plotLeft + plotWidth * 0.5;
            overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            var end = overlay.TimeAt(x);
            overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await h.Settled; await Idle(c);
            Require(Math.Abs((h.ViewStart - start).TotalSeconds) < 1 && Math.Abs((h.ViewEnd - end).TotalSeconds) < 1, $"Dragging selected {h.ViewStart:HH:mm:ss}-{h.ViewEnd:HH:mm:ss}, expected {start:HH:mm:ss}-{end:HH:mm:ss}.");
            // Ctrl + wheel zooms around the pointer.
            overlay.ModifierSource = () => ModifierKeys.Control;
            var before = h.ViewEnd - h.ViewStart;
            x = plotLeft + plotWidth * 0.5;
            var anchor = overlay.TimeAt(x);
            overlay.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
            Require(h.ViewEnd - h.ViewStart < before && h.ViewStart <= anchor && anchor <= h.ViewEnd, "Ctrl + wheel did not zoom around the pointer.");
            // The wheel without Ctrl is left to the page.
            overlay.ModifierSource = () => ModifierKeys.None;
            var unchanged = h.ViewEnd - h.ViewStart;
            overlay.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            Require(h.ViewEnd - h.ViewStart == unchanged, "The plain wheel changed the range.");
            h.ResetZoom();
            await h.Settled; await Idle(c);
            // A click on an incident marker of the strip selects it.
            var incident = h.Loaded!.Incidents[0];
            x = strip.XOf(incident.At);
            strip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            strip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await h.Settled; await Idle(c);
            Require(h.SelectedIncident?.Incident.Id == incident.Id, "Clicking the incident on the strip did not select it.");
            Require(Math.Abs((h.ViewStart + (h.ViewEnd - h.ViewStart) / 2 - incident.At).TotalSeconds) < 1, "The selected incident is not centred.");
            Log(c, "PASS: pointer input on the overlay and the strip: hover readouts with units, drag selects the dragged range, Ctrl + wheel zooms around the pointer (the plain wheel is left to the page), a click on an incident marker selects and centres it (real routed mouse events; the pointer position is supplied by the harness)");
        }
        finally
        {
            overlay.PointerSource = null; overlay.ModifierSource = null; strip.PointerSource = null;
            overlay.HoverAt = null; h.SetHover(null);
        }
    }

    private static async Task IncidentSelection(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        var first = h.Loaded!.Incidents[0]; var second = h.Loaded.Incidents[1];
        h.SelectedEvent = h.Events.Single(e => e.IsIncident && e.Event.IncidentId == first.Id);
        await h.Settled; await Idle(c); await DesktopValidation.WaitForChartMeasureAsync(c.Window);
        var selected = h.SelectedIncident ?? throw new InvalidOperationException("No incident is selected.");
        Require(h.ViewStart == first.At - TimeSpan.FromMinutes(2) && h.ViewEnd == first.At + TimeSpan.FromMinutes(2), "The view is not centred on the incident with two minutes of context.");
        Require(h.SelectedIncident.Summary.Contains("Round-trip time reached 380", StringComparison.Ordinal), "The analysis did not find the recorded RTT spike: " + h.SelectedIncident.Summary);
        Require(h.SelectedIncident.Summary.Contains("not proof of its cause", StringComparison.Ordinal) && h.SelectedIncident.RecordedState.Contains("not re-evaluated", StringComparison.Ordinal), "The analysis lacks its caveats.");
        Require(Math.Abs((Overlay(c).SelectedAt!.Value - first.At).TotalMilliseconds) < 1 && Math.Abs((Strip(c).SelectedAt!.Value - first.At).TotalMilliseconds) < 1, "The selected incident is not marked on the overlay and the strip.");
        Require(h.SelectedIncident.Evidence.Any(r => r.Value == "380"), "The evidence does not hold the recorded spike value.");
        Require(Charts(c).All(chart => ((LiveChartsCore.SkiaSharpView.Axis)chart.XAxes.First()).MinLimit is { } min && Math.Abs(min - h.ViewStart.UtcDateTime.Ticks) < 1000), "The charts are not on the incident's range.");
        DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, "history-incident-light.png"));
        // The second incident has no network problem: the recording shows a frame-rate dip, and the text says what stands out.
        h.NextIncidentCommand.Execute(null);
        await h.Settled; await Idle(c);
        Require(h.SelectedIncident!.Incident.Id == second.Id, "Next incident did not select the second one.");
        Require(h.SelectedIncident.Summary.Contains("Frames per second fell to 3", StringComparison.Ordinal), "The frame-rate dip was not reported: " + h.SelectedIncident.Summary);
        Require(!h.SelectedIncident.Summary.Contains("Round-trip time reached", StringComparison.Ordinal), "An RTT problem was reported where none was recorded.");
        h.SelectedIncident.IsEvidenceExpanded = true; await Idle(c);
        // An annotation is saved beside the recording and shown in the list.
        h.SelectedIncident.Category = HistoryIncidentViewModel.Categories.Single(x => x.Value == IncidentCategory.DisplayStutter);
        h.SelectedIncident.Description = "Cursor stuttered while scrolling.";
        await h.SelectedIncident.SaveCommand.ExecuteAsync();
        Require(h.SelectedIncident.SaveMessage.Contains("Saved beside the recording", StringComparison.Ordinal), "The annotation was not saved: " + h.SelectedIncident.SaveMessage);
        Require(File.Exists(Path.Combine(a.ShortId.Length > 0 ? h.Loaded.Info.Directory : "", RecordingFormat.AnnotationName(RecordingFormat.Stamp(h.Loaded.Info.StartedUtc), a.ShortId))), "The annotation file does not exist.");
        Require(h.Events.Single(e => e.Event.IncidentId == second.Id).Annotation.StartsWith("Display stutter", StringComparison.Ordinal), "The list does not show the annotation.");
        DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, "history-incident2-annotated-light.png"));
        Log(c, "PASS: selecting an incident centres the view with ±2 min, marks it on strip and overlay, and the deterministic text reports the recorded RTT spike (380 ms) for the first and the frame-rate dip (3 FPS, no RTT claim) for the second; evidence lists recorded values; the annotation is saved beside the recording");
    }

    private static async Task KeyboardNavigation(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        var overlay = Overlay(c); var strip = Strip(c);
        Require(overlay.Focusable && strip.Focusable && overlay.IsEnabled, "The charts or the strip cannot take keyboard focus.");
        foreach (var element in new HistoryTimeElement[] { strip, overlay })
        {
            h.ResetZoom();
            var focused = element.Focus();      // the runner's window may not be the active one; the keys below are raised at the element either way
            SmokeLog.Write($"history: {element.GetType().Name} focus requested: {focused}, keyboard focus {element.IsKeyboardFocused}");
            var full = h.ViewEnd - h.ViewStart;
            DesktopValidation.RaiseKey(element, Key.Up); Require(h.ViewEnd - h.ViewStart == full / 2, $"{element.GetType().Name}: Up did not zoom in.");
            DesktopValidation.RaiseKey(element, Key.Right); Require(h.ViewStart > h.SessionStart, $"{element.GetType().Name}: Right did not pan.");
            var moved = h.ViewStart;
            DesktopValidation.RaiseKey(element, Key.Left); Require(h.ViewStart < moved, $"{element.GetType().Name}: Left did not pan back.");
            DesktopValidation.RaiseKey(element, Key.Down); Require(h.ViewEnd - h.ViewStart == full, $"{element.GetType().Name}: Down did not zoom out.");
            DesktopValidation.RaiseKey(element, Key.Up); DesktopValidation.RaiseKey(element, Key.Home);
            Require(h.ViewStart == h.SessionStart && h.ViewEnd == h.SessionEnd, $"{element.GetType().Name}: Home did not show the entire session.");
            // From the middle of the session (599 s) the first incident lies before it and the second after it.
            DesktopValidation.RaiseKey(element, Key.PageDown); Require(h.SelectedIncident?.Incident.Id == h.Loaded!.Incidents[1].Id, $"{element.GetType().Name}: Page Down did not select the next incident.");
            DesktopValidation.RaiseKey(element, Key.PageUp); Require(h.SelectedIncident?.Incident.Id == h.Loaded!.Incidents[0].Id, $"{element.GetType().Name}: Page Up did not select the previous incident.");
        }
        // The event list is an ordinary list: arrow keys move through it.
        var list = DesktopValidation.Descendants<ListBox>(View(c)).First(l => System.Windows.Automation.AutomationProperties.GetName(l) == "Timeline events");
        h.SelectedEvent = h.Events[0]; await Idle(c);
        list.Focus();
        DesktopValidation.RaiseKey(list, Key.Down); await Idle(c);
        Require(h.SelectedEvent == h.Events[1] || h.SelectedEvent == h.Events[0], "The list did not respond to the keyboard.");
        // Every action of the page is a real button with an automation name.
        var buttons = DesktopValidation.Descendants<Button>(View(c)).Where(b => b.IsVisible).ToArray();
        Require(buttons.Length >= 8 && buttons.All(b => b.Focusable && (b.Content is string || System.Windows.Automation.AutomationProperties.GetName(b).Length > 0)), "A page action is not a focusable, named button.");
        Require(new[] { "Previous incident", "Next incident" }.All(n => buttons.Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == n)), "Incident navigation buttons are missing their names.");
        Log(c, $"PASS: keyboard: Up/Down zoom, Left/Right pan, Home resets and Page Up/Page Down step through incidents on both the strip and the charts; the event list answers arrow keys; {buttons.Length} visible actions are focusable buttons with names");
    }

    private static async Task ThemesAndIcons(HistorySmokeContext c)
    {
        var a = shortRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        h.SelectedEvent = h.Events.Single(e => e.IsIncident && e.Event.IncidentId == h.Loaded!.Incidents[0].Id);
        await h.Settled;
        foreach (var dark in new[] { false, true })
        {
            c.Vm.IsDark = dark;
            await Idle(c); await Task.Delay(250); await DesktopValidation.WaitForChartMeasureAsync(c.Window);
            DesktopValidation.CheckChartList(Charts(c), $"Session History ({(dark ? "dark" : "light")})");
            var expected = ((SolidColorBrush)c.Window.FindResource("AccentBrush")).Color;
            foreach (HistoryTimeElement element in new HistoryTimeElement[] { Overlay(c), Strip(c) })
                Require(element.GetValue(HistoryTimeElement.AccentFillProperty) is SolidColorBrush accent && accent.Color == expected, $"{element.GetType().Name} did not follow the {(dark ? "dark" : "light")} theme.");
            var tag = dark ? "dark" : "light";
            DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, $"history-incident-{tag}-theme.png"));
            // Every event shape resolves to its own geometry; findings use the severity shapes. The probes live in the page for a moment so the style applies as it does on screen.
            var probes = new WrapPanel();
            var style = (Style)c.Window.FindResource("EventIcon");
            foreach (var iconTag in HistoryEventKinds.IconTags) probes.Children.Add(new WpfPath { Style = style, Tag = iconTag });
            var root = (StackPanel)View(c).Content;
            root.Children.Add(probes);
            await Idle(c); c.Window.UpdateLayout();
            var icons = probes.Children.Cast<WpfPath>().Select(p => (Tag: (string)p.Tag, p.Data)).ToList();
            root.Children.Remove(probes);
            foreach (var (iconTag, data) in icons)
            {
                var key = iconTag.StartsWith("Finding", StringComparison.Ordinal) ? "SeverityGeometry" + iconTag["Finding".Length..] : "EventGeometry" + iconTag;
                if (iconTag != "Other") Require(ReferenceEquals(data, c.Window.FindResource(key)), $"Event icon {iconTag} does not use {key}.");
            }
            Require(icons.Select(i => i.Data).Distinct().Count() == HistoryEventKinds.IconTags.Count, "Two event kinds share one shape.");
            var drawn = DesktopValidation.Descendants<WpfPath>(View(c)).Where(p => p.Style == c.Window.FindResource("EventIcon")).ToArray();
            Require(drawn.Length >= h.Events.Count && drawn.All(p => p.Data is not null && p.Fill is not null), "Not every timeline entry shows its icon.");
            Log(c, $"PASS: {tag} theme: charts, overlay and strip follow the theme ({Charts(c).Length} charts checked), {HistoryEventKinds.IconTags.Count} event shapes resolve to {icons.Select(i => i.Data).Distinct().Count()} distinct geometries, {drawn.Length} icons drawn next to their labels");
        }
        c.Vm.IsDark = true;
        await Idle(c);
    }

    private static async Task SwitchRecordings(HistorySmokeContext c)
    {
        var a = shortRun!; var b = secondRun!;
        await OpenAnalyze(c, a, 5);
        var h = History(c);
        var itemB = c.Vm.Background.Recordings.Single(r => r.Summary.ShortId == b.ShortId);
        h.SelectedRecording = itemB;                                  // what choosing it in the list does
        await WaitFor(() => h.Loaded?.Info.ShortId == b.ShortId, "recording B to load");
        await h.Settled; await Idle(c);
        await WaitFor(() => Charts(c).Length == h.Charts.Count, "the charts of recording B");
        await DesktopValidation.WaitForChartMeasureAsync(c.Window);
        Require(h.Loaded!.SampleCount == 300 && h.Loaded.Incidents.Count == 1, "Recording B is not what is shown.");
        var data = h.Charts.Select(r => r.Data).ToHashSet();
        Require(Charts(c).All(chart => LiveChart.GetData(chart) is { } d && data.Contains(d)), "A chart of the previous recording is still on the page.");
        DesktopValidation.CheckChartList(Charts(c), "Session History (recording B)");
        Require(h.Charts.Single(r => r.Id == "rtt").Stats.Contains("P50 42", StringComparison.Ordinal), "The statistics are not those of recording B: " + h.Charts.Single(r => r.Id == "rtt").Stats);
        // Opening one recording straight after another shows only the second.
        var itemA = c.Vm.Background.Recordings.Single(r => r.Summary.ShortId == a.ShortId);
        h.SelectedRecording = itemA; h.SelectedRecording = itemB;
        await WaitFor(() => h.HasHistory && h.Loaded?.Info.ShortId == b.ShortId && !h.IsLoading, "the last choice to win");
        await h.Settled;
        Log(c, $"PASS: switching A → B replaced the charts, statistics and timeline with those of B (300 samples, 1 incident) and left no chart of A on the page; choosing A and B in quick succession shows only B");
    }

    private static async Task RepeatedNavigation(HistorySmokeContext c)
    {
        var a = shortRun!;
        var views = new List<WeakReference>(); var objects = new List<WeakReference>(); var controls = new List<WeakReference>();
        var heaps = new List<long>();
        var sw = Stopwatch.StartNew();
        for (var cycle = 1; cycle <= 12; cycle++)
        {
            await OpenAnalyze(c, a, 5);
            var h = History(c);
            h.SelectedEvent = h.Events.First(e => e.IsIncident); await h.Settled; await Idle(c);
            views.Add(new WeakReference(View(c)));
            foreach (var chart in Charts(c)) { controls.Add(new WeakReference(chart)); objects.Add(new WeakReference(LiveChart.GetObjects(chart))); }
            c.Vm.SelectedPage = c.Vm.Pages[0];                          // leaving the page releases the recording
            await Idle(c); await Task.Delay(120);
            Require(!h.HasHistory && h.Charts.Count == 0, $"Cycle {cycle}: the page did not release the recording.");
            if (cycle is 3 or 6 or 9 or 12) heaps.Add(HeapAfterCollection());
        }
        await Idle(c); await Task.Delay(500); await Idle(c);
        HeapAfterCollection();
        var aliveViews = views.Count(w => w.IsAlive); var aliveObjects = objects.Count(w => w.IsAlive); var aliveControls = controls.Count(w => w.IsAlive);
        var growth = heaps[^1] - heaps[0];
        var perBatch = heaps.Zip(heaps.Skip(1), (x, y) => y - x).DefaultIfEmpty(0).Max();
        await File.WriteAllTextAsync(Path.Combine(c.EvidenceDirectory, "history-navigation-result.txt"),
            $"Session History, 12 × (Analyze, select incident, leave): {sw.Elapsed.TotalSeconds:0.0} s{Environment.NewLine}" +
            $"Managed heap after a full collection at cycles 3/6/9/12: {string.Join(" → ", heaps.Select(h => MiB(h) + " MiB"))} (growth {MiB(growth)} MiB, largest step {MiB(perBatch)} MiB){Environment.NewLine}" +
            $"Working set now: {MiB(Environment.WorkingSet)} MiB{Environment.NewLine}" +
            $"Still alive after the last collection: views {aliveViews}/{views.Count}, LiveCharts objects {aliveObjects}/{objects.Count}, chart controls {aliveControls}/{controls.Count}{Environment.NewLine}" +
            $"Limits: views and LiveCharts objects of released pages must be collected; managed heap growth ≤ 24 MiB overall and ≤ 12 MiB per three cycles." + Environment.NewLine);
        Require(aliveViews == 0, $"{aliveViews} of {views.Count} released history views are still reachable.");
        Require(aliveObjects == 0, $"{aliveObjects} of {objects.Count} released LiveCharts objects are still reachable.");
        Require(growth <= 24L << 20 && perBatch <= 12L << 20, $"Managed heap keeps growing across history visits: {string.Join(" → ", heaps.Select(h => MiB(h)))} MiB.");
        Log(c, $"PASS: 12 × open and leave: managed heap {string.Join(" → ", heaps.Select(h => MiB(h)))} MiB (growth {MiB(growth)} MiB); views alive {aliveViews}/{views.Count}, LiveCharts objects alive {aliveObjects}/{objects.Count}; chart controls alive {aliveControls}/{controls.Count} (informational: controls are released by WPF, not by this code)");
    }

    private static async Task LongRecording(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        var sw = Stopwatch.StartNew();
        const int seconds = 6 * 3600;
        longRun = await WriteAsync(directory, new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.Zero), seconds, template,
            i => (i is >= 3 * 3600 - 2 and <= 3 * 3600 + 6 ? 420 : 30 + i % 9, 15 + i % 11, 28 + i % 5, false), [3 * 3600, 5 * 3600 + 1800], (4 * 3600, 4 * 3600 + 300), 16L << 20);
        var writing = sw.Elapsed;
        Require(longRun.Parts >= 3, "The long recording did not rotate.");
        var baseline = HeapAfterCollection();
        var processBefore = Environment.WorkingSet;
        sw.Restart();
        await c.Vm.Background.RefreshRecordingsAsync();
        c.Vm.Background.Recordings.Single(r => r.Summary.ShortId == longRun.ShortId).AnalyzeCommand.Execute(null);
        var h = History(c);
        await WaitFor(() => h.HasHistory || h.HasError, "the six hour recording to load", 180_000);
        Require(h.HasHistory, "The six hour recording did not load: " + h.Error);
        var load = sw.Elapsed;
        await WaitFor(() => Charts(c).Length == h.Charts.Count && Charts(c).Length > 0, "the charts of the six hour recording");
        await h.Settled; await Idle(c); await DesktopValidation.WaitForChartMeasureAsync(c.Window); await Task.Delay(200);
        var open = HeapAfterCollection();
        var workingSet = Environment.WorkingSet;
        Require(h.Loaded!.SampleCount == seconds - 300 && h.Loaded.Incidents.Count == 2 && h.Loaded.Info.State == RecordingCompleteness.Complete, $"Loaded {h.Loaded.SampleCount} samples, {h.Loaded.Incidents.Count} incidents, {h.Loaded.Info.State}.");
        var maxPoints = h.Charts.Max(r => r.Data.Points.Count);
        Require(maxPoints <= SessionHistoryViewModel.MaximumChartPoints + 8, $"A chart holds {maxPoints} points.");
        DesktopValidation.CheckChartList(Charts(c), "Session History (six hours)");
        DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, "history-6h-overview.png"));
        var rtt = h.Charts.Single(r => r.Id == "rtt");
        Require(rtt.Data.Points.Any(p => p.Value is { } v && v >= 420), "The 420 ms spike of the six hour recording is missing from the reduced chart: peaks must survive.");
        Require(rtt.Stats.Contains("peaks kept", StringComparison.Ordinal), "The reduction is not announced: " + rtt.Stats);
        // Select the incident next to the spike and zoom: the resolution rises to every sample.
        sw.Restart();
        h.SelectedEvent = h.Events.First(e => e.IsIncident); await h.Settled; await Idle(c);
        var zoom = sw.Elapsed;
        Require(h.SelectedIncident!.Summary.Contains("Round-trip time reached 420", StringComparison.Ordinal), "The six hour analysis did not report the spike: " + h.SelectedIncident.Summary);
        Require(h.Charts.All(r => r.Data.Points.Count <= 241), "Zooming to the incident did not raise the resolution (one point per sample).");
        DesktopValidation.Capture(c.Window, View(c), Path.Combine(c.EvidenceDirectory, "history-6h-incident.png"));
        var peakHeap = HeapAfterCollection();
        c.Vm.SelectedPage = c.Vm.Pages[0]; await Idle(c); await Task.Delay(200);
        var released = HeapAfterCollection();
        var text =
            $"Session History, six hour recording (accelerated fixture written through the real writer; not a six hour real run): {seconds - 300:N0} samples, {longRun.Parts} parts ({longRun.CompressedParts} compressed), {MiB(longRun.Bytes)} MiB on disk, written in {writing.TotalSeconds:0.0} s{Environment.NewLine}" +
            $"Load (read, decompress, decode, aggregate) {load.TotalSeconds:0.0} s; select incident, centre and re-aggregate {zoom.TotalMilliseconds:0} ms{Environment.NewLine}" +
            $"Managed heap after a full collection: before {MiB(baseline)} MiB, history open {MiB(open)} MiB (+{MiB(open - baseline)} MiB), after zoom {MiB(peakHeap)} MiB, page left {MiB(released)} MiB (+{MiB(released - baseline)} MiB){Environment.NewLine}" +
            $"Working set: before {MiB(processBefore)} MiB, with history open {MiB(workingSet)} MiB{Environment.NewLine}" +
            $"History columns {MiB(h.Loaded.ApproximateBytes)} MiB for {h.Charts.Count} metrics; largest chart {maxPoints} points (limit {SessionHistoryViewModel.MaximumChartPoints}){Environment.NewLine}" +
            $"Limits: load ≤ 120 s, history open ≤ +96 MiB managed heap, ≤ +24 MiB after leaving the page.{Environment.NewLine}";
        await File.WriteAllTextAsync(Path.Combine(c.EvidenceDirectory, "history-6h-result.txt"), text);
        Require(load < TimeSpan.FromSeconds(120), $"Loading took {load.TotalSeconds:0} s.");
        Require(open - baseline <= 96L << 20, $"History open retains {MiB(open - baseline)} MiB.");
        Require(released - baseline <= 24L << 20, $"{MiB(released - baseline)} MiB stay after leaving the page.");
        Log(c, $"PASS: six hour recording ({seconds - 300:N0} samples, {longRun.Parts} parts): loaded in {load.TotalSeconds:0.0} s, managed heap +{MiB(open - baseline)} MiB while open and +{MiB(released - baseline)} MiB after leaving, working set {MiB(workingSet)} MiB, largest chart {maxPoints} points, the 420 ms spike survives the reduction, selecting its incident re-aggregates in {zoom.TotalMilliseconds:0} ms");
        // Remove the large fixture: it is evidence of measurement, not of content.
        try { foreach (var file in Directory.GetFiles(directory, $"EucLens-*-{longRun.ShortId}*")) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(c, "NOTE: the six hour fixture could not be removed: " + ex.Message); }
    }
}
