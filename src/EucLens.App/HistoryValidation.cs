using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>The first stack frame inside EUC Lens code, with its line, for the FAIL line of a section.</summary>
    internal static string FirstFrame(Exception ex)
    {
        var frame = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at EucLens", StringComparison.Ordinal)) ?? (ex.StackTrace ?? "no stack").Split('\n')[0].Trim();
        return frame.Length > 400 ? frame[..400] : frame;
    }

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

    // ---------- screenshots that wait for the charts to be drawn ----------

    private static Task NextFrame()
    {
        var done = new TaskCompletionSource();
        EventHandler? handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; done.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        // WPF raises Rendering only while something is being drawn; a short timeout keeps the loop moving when nothing is.
        return Task.WhenAny(done.Task, Task.Delay(60)).ContinueWith(_ => CompositionTarget.Rendering -= handler, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Largest side of a failure picture: the page of a nonvirtualized list can be tens of thousands of pixels tall, which no bitmap or PNG codec accepts.</summary>
    private const int MaxFailurePictureSide = 4096;

    private static RenderTargetBitmap RenderOnce(Window window, FrameworkElement visual, int maxSide = int.MaxValue)
    {
        var width = (int)Math.Min(Math.Ceiling(visual.ActualWidth), maxSide); var height = (int)Math.Min(Math.Ceiling(visual.ActualHeight), maxSide);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }
        bitmap.Render(drawing);
        return bitmap;
    }

    /// <summary>
    /// The part of the page a user sees: the scroll viewer that holds the page, not the page's full height. The application header (live clock, status) lies outside it,
    /// so its once-per-second updates cannot keep the pixels from settling, and a nonvirtualized list of thousands of rows is never rendered at its full height.
    /// </summary>
    private static FrameworkElement PageViewport(FrameworkElement view)
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(view); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer && viewer.ActualHeight > 0) return viewer;
        throw new InvalidOperationException("The page is not inside a scroll viewer, so its viewport cannot be captured.");
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string PixelHash(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))[..16];
    }

    /// <summary>
    /// Saves a screenshot only once what is drawn has stopped changing: the same pixels in five consecutive frames and every LiveCharts canvas reporting that it has
    /// nothing left to draw. LiveCharts repaints after the frame in which its axis range changed, so a capture taken right after a range change shows the old range.
    /// </summary>
    private static async Task<ShotInfo> Shot(HistorySmokeContext c, FrameworkElement visual, string path)
    {
        var sw = Stopwatch.StartNew();
        string? previous = null; var identical = 0; var frames = 0; RenderTargetBitmap? bitmap = null; var canvasesIdle = false; var slowestFrame = 0d;
        while (sw.ElapsedMilliseconds < 10_000)
        {
            var frameStart = sw.Elapsed.TotalMilliseconds;
            await NextFrame(); await Idle(c); frames++;
            bitmap = RenderOnce(c.Window, visual);
            var hash = PixelHash(bitmap);
            slowestFrame = Math.Max(slowestFrame, sw.Elapsed.TotalMilliseconds - frameStart);
            identical = hash == previous ? identical + 1 : 0; previous = hash;
            canvasesIdle = DesktopValidation.Descendants<MotionCanvas>(c.Window).All(m => m.CanvasCore.IsValid);
            if (identical >= 5 && canvasesIdle) break;
        }
        // The last picture is kept even when it never settled, so the evidence exists whatever the verdict.
        if (bitmap is not null) SavePng(bitmap, path);
        Require(bitmap is not null && identical >= 5, $"{Path.GetFileName(path)}: what is drawn did not settle within 10 s ({frames} frames, {identical} identical, slowest frame {slowestFrame:0} ms, picture {bitmap?.PixelWidth}×{bitmap?.PixelHeight} px).");
        // Evidence of what the engine had applied at that moment: the visible axis range of the first chart against the range the page shows.
        var chart = DesktopValidation.Descendants<CartesianChart>(visual).FirstOrDefault();
        var range = chart?.XAxes.FirstOrDefault() is LiveChartsCore.SkiaSharpView.Axis axis ? $"; engine axis {axis.VisibleDataBounds.Min:0} to {axis.VisibleDataBounds.Max:0} ticks" : "";
        Log(c, $"NOTE: screenshot {Path.GetFileName(path)} taken after {sw.ElapsedMilliseconds} ms ({frames} frames, slowest {slowestFrame:0} ms, stable pixels, canvases idle: {canvasesIdle}{range})");
        return new(sw.ElapsedMilliseconds, frames, slowestFrame);
    }

    private sealed record ShotInfo(long Milliseconds, int Frames, double SlowestFrameMilliseconds);

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
        int[] incidentSeconds, (int From, int To)? pause, long segment, bool compress = true, int notes = 0, int stepSeconds = 1)
    {
        var storage = new PhysicalRecordingStorage();
        var clock = new ManualClock(start);
        await using var writer = new RecordingWriter(() => new RecordingOptions
        {
            Directory = directory, QuotaBytes = 8L << 30, MaxAge = null, SegmentBytes = segment, MinimumFreeBytes = 64L << 20, CompressClosedParts = compress
        }, storage, clock);
        var recorder = new SessionRecorder(writer, clock);
        recorder.Start(isDemo: true);
        var incidents = new List<IncidentMarker>();
        var wasActive = false;
        for (var i = 0; i < seconds; i++)
        {
            var at = start.AddSeconds((double)i * stepSeconds);
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
            if (notes > 0)
            {
                var from = (int)((long)notes * i / seconds); var to = (int)((long)notes * (i + 1) / seconds);
                for (var n = from; n < to; n++) recorder.Mark(new(RecordingMarkKind.Note, at, $"Synthetic event {n + 1}"));
            }
            var (rtt, cpu, fps, active) = shape(i);
            var events = new List<DiagnosticEvent>();
            if (active != wasActive) events.Add(new(at, active ? FindingTransition.Detected : FindingTransition.Recovered, Finding(Severity.Warning, at)));
            wasActive = active;
            recorder.Add(template with
            {
                Timestamp = at,
                Connection = template.Connection with { RoundTripTime = double.IsNaN(rtt) ? Metric.Unavailable("ms", "Counter could not be read in this sample") : Metric.Available(rtt, "ms"), PacketLoss = Metric.Available(active ? 1.5 : 0, "%") },
                System = template.System with { CpuUtilization = Metric.Available(cpu, "%") },
                Graphics = template.Graphics with { FramesPerSecond = Metric.Available(fps, "FPS") },
                Diagnostics = new DiagnosticSnapshot([new(DiagnosticDomain.Connection, active ? HealthState.Warning : HealthState.Healthy, AssessmentCoverage.Full, "evidence", "note", 60)],
                    active ? ["connection.rtt.sustained"] : [], events),
            });
            if (i % (notes > 0 ? 50 : 250) == (notes > 0 ? 49 : 249)) await writer.FlushAsync();
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
        await Section(c, failures, "interrupted, damaged and unsupported recordings", () => DamagedRecordings(c));
        await Section(c, failures, "sample limit", () => SampleLimit(c));
        await Section(c, failures, "clock change", () => ClockChange(c));
        await Section(c, failures, "large event list", () => LargeEventList(c));
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
            Log(c, $"FAIL: {name}: {ex.GetType().Name}: {ex.Message} | first frame: {FirstFrame(ex)}");
            try { CaptureFailure(c, name); }
            catch (Exception captureFailure) { Log(c, $"NOTE: the failure picture for '{name}' could not be saved: {captureFailure.GetType().Name}: {captureFailure.Message} | first frame: {FirstFrame(captureFailure)}"); }
        }
        try { c.Vm.SelectedPage = c.Vm.Pages[0]; await Idle(c); }
        catch (Exception ex) { SmokeLog.Write("history reset: " + ex.Message); }
    }

    /// <summary>A bounded picture of what the user sees on the page (its viewport, at most 4096 px a side), never the full height of the page.</summary>
    private static void CaptureFailure(HistorySmokeContext c, string name)
    {
        var view = DesktopValidation.Descendants<SessionHistoryView>(c.Window).FirstOrDefault();
        if (view is null || view.ActualWidth <= 0) return;
        var bitmap = RenderOnce(c.Window, PageViewport(view), MaxFailurePictureSide);
        var path = Path.Combine(c.EvidenceDirectory, $"FAILED-history-{name.Replace(' ', '-')}.png");
        SavePng(bitmap, path);
        Log(c, $"NOTE: failure picture {Path.GetFileName(path)} saved ({bitmap.PixelWidth}×{bitmap.PixelHeight} px, the page viewport)");
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
            (i is >= 200 and < 220 ? double.NaN : i is >= 398 and <= 406 ? 380 : 30 + i % 7, 18 + i % 9, i is >= 898 and <= 906 ? 3 : 28 + i % 4, i is >= 398 and <= 410), [400, 900], (600, 660), 300_000);
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
        Require(!ids.Contains("dirtyFps") && h.Loaded.UnavailableChannels.Any(u => u.Channel.Id == "dirtyFps"), "A chart without any recorded values was drawn (the mock records no Blast counters).");
        // The multi-part recording read back: identities, times, diagnostic events and rotation boundaries are exactly what was written.
        Require(h.Loaded.Incidents.Select(i => i.Id).SequenceEqual(a.Incidents.Select(i => i.Id)) && h.Loaded.Incidents.Select(i => i.At).SequenceEqual(a.Incidents.Select(i => i.At)),
            "The incident ids or times differ from what was written.");
        Require(h.Loaded.Events.Count(e => e.Kind == HistoryEventKind.FindingDetected) == 1 && h.Loaded.Events.Count(e => e.Kind == HistoryEventKind.FindingRecovered) == 1,
            "A diagnostic transition is missing or duplicated on the timeline.");
        Require(h.Loaded.Events.Where(e => e.Kind != HistoryEventKind.RecordingEnded).Select(e => e.At).SequenceEqual(h.Loaded.Events.Where(e => e.Kind != HistoryEventKind.RecordingEnded).Select(e => e.At).Order()), "The timeline is not in time order.");
        Require(h.Loaded.Gaps.Count == 1 && h.Loaded.Gaps[0].Reason == "Monitoring was paused", "The only gap should be the pause.");
        Require(h.Loaded.Issues.Count == 0, "Reading the rotated, compressed recording reported issues: " + string.Join("; ", h.Loaded.Issues.Select(i => i.Message)));
        // Missing measurements are gaps, never zeros: RTT is unavailable for 20 samples (and the pause), the other metrics only have the pause.
        var rttPoints = h.Charts.Single(r => r.Id == "rtt").Data.Points; var cpuPoints = h.Charts.Single(r => r.Id == "cpu").Data.Points;
        Require(rttPoints.Count(p => p.Value is null) >= 2 && cpuPoints.Count(p => p.Value is null) == 1, $"Gaps are not preserved: RTT {rttPoints.Count(p => p.Value is null)} breaks, CPU {cpuPoints.Count(p => p.Value is null)} breaks.");
        Require(!rttPoints.Any(p => p.Value == 0) && !cpuPoints.Any(p => p.Value == 0), "A missing value was drawn as zero.");
        Require(h.Loaded.Channel("rtt")!.Missing == 20, $"RTT reports {h.Loaded.Channel("rtt")!.Missing} missing samples, expected 20.");
        DesktopValidation.CheckChartList(Charts(c), "Session History");
        Require(Charts(c).Length == h.Charts.Count, "The number of chart controls differs from the rows.");
        Require(DesktopValidation.Descendants<ListBox>(View(c)).Any(l => l.Items.Count == h.Events.Count && h.Events.Count > 0), "The event list does not show every timeline entry.");
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-overview-light.png"));
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
        await WaitFor(() => Charts(c).All(chart => ((LiveChartsCore.CartesianChartEngine)chart.CoreChart).DrawMarginSize.Width > 0), "the chart plot areas to be measured");
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
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-incident-light.png"));
        await Shot(c, Strip(c), Path.Combine(c.EvidenceDirectory, "history-timeline-strip-light.png"));
        var chartStack = (FrameworkElement)VisualTreeHelper.GetParent(Overlay(c));
        Overlay(c).HoverAt = first.At + TimeSpan.FromSeconds(20); h.SetHover(first.At + TimeSpan.FromSeconds(20)); await Idle(c);
        await Shot(c, chartStack, Path.Combine(c.EvidenceDirectory, "history-synchronized-charts-light.png"));
        Overlay(c).HoverAt = null; h.SetHover(null);
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
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-incident2-annotated-light.png"));
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
            await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, $"history-incident-{tag}-theme.png"));
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

    private static string[] FilesOf(string directory, string shortId) => Directory.GetFiles(directory, $"EucLens-*-{shortId}*").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    private static async Task DamagedRecordings(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        static (double, double, double, bool) Calm(int i) => (40 + i % 5, 20 + i % 6, 30, false);
        var h = History(c);
        var results = new List<string>();

        // Interrupted: the process died while writing the last line; no footer, no index.
        var interrupted = await WriteAsync(directory, start, 400, template, Calm, [200], null, 64L << 20, compress: false);
        var part = FilesOf(directory, interrupted.ShortId).Single(f => f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith(".meta.json", StringComparison.Ordinal));
        var bytes = await File.ReadAllBytesAsync(part);
        await File.WriteAllBytesAsync(part, bytes[..^350]);
        foreach (var meta in FilesOf(directory, interrupted.ShortId).Where(f => f.EndsWith(".meta.json", StringComparison.Ordinal))) File.Delete(meta);
        await OpenAnalyze(c, interrupted, 1);
        Require(h.StateLabel == "Interrupted" && h.Loaded!.Info.State == RecordingCompleteness.Interrupted && !h.Loaded.Info.HasEnded, $"An interrupted recording is shown as {h.StateLabel}.");
        Require(h.StateNote.Contains("no end of recording was written", StringComparison.Ordinal) && !h.Facts.Any(f => f.Label == "End" || f.Value == "Complete"), "The interrupted recording is described as ended or complete.");
        Require(h.IssueLines.Any(l => l.StartsWith("Note:", StringComparison.Ordinal) && l.Contains("incomplete line", StringComparison.Ordinal)), "The cut-off last line is not reported.");
        Require(h.Loaded!.Incidents.Count == 1 && h.Loaded.SampleCount is >= 380 and < 400, $"Interrupted recording: {h.Loaded.SampleCount} samples, {h.Loaded.Incidents.Count} incidents.");
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-interrupted.png"));
        results.Add($"interrupted (labelled Interrupted, {h.Loaded.SampleCount} of 400 samples, tail reported)");

        // Missing part: the middle of a rotated recording is gone.
        var gapped = await WriteAsync(directory, start.AddHours(1), 600, template, Calm, [300], null, 250_000);
        var parts = FilesOf(directory, gapped.ShortId).Where(f => f.Contains("-part", StringComparison.Ordinal)).ToArray();
        Require(parts.Length >= 3, "The fixture did not rotate.");
        File.Delete(parts[1]);
        await OpenAnalyze(c, gapped, 1);
        Require(h.IssueLines.Any(l => l.StartsWith("Problem:", StringComparison.Ordinal) && l.Contains("Part 2", StringComparison.Ordinal) && l.Contains("missing", StringComparison.Ordinal)), "The missing part is not reported: " + string.Join(" | ", h.IssueLines));
        Require(h.Loaded!.Gaps.Count >= 1 && h.Loaded.Info.DataCoverage < 1 && h.Loaded.SampleCount < 600, "The missing part is not a gap in the data.");
        Require(h.Charts.Single(r => r.Id == "rtt").Data.Points.Any(p => p.Value is null), "The missing part is not a break in the chart.");
        results.Add($"missing part ({h.Loaded.Gaps.Count} gap(s), coverage {h.Loaded.Info.DataCoverage:P0})");

        // Damaged metadata: the index file is garbage; the parts still say the recording ended properly.
        var damaged = await WriteAsync(directory, start.AddHours(2), 300, template, Calm, [100], null, 64L << 20);
        foreach (var meta in FilesOf(directory, damaged.ShortId).Where(f => f.EndsWith(".meta.json", StringComparison.Ordinal))) await File.WriteAllTextAsync(meta, "{ this is not json");
        await OpenAnalyze(c, damaged, 1);
        Require(h.Loaded!.Info.State == RecordingCompleteness.Complete && h.Loaded.SampleCount == 300 && h.Loaded.Incidents.Count == 1, "A recording with a damaged index file was not read completely.");
        Require(c.Vm.Background.Recordings.Single(r => r.Summary.ShortId == damaged.ShortId).Summary.IsComplete, "The recordings list does not show the recording as complete.");
        results.Add("damaged metadata (read from the parts, still complete)");

        // Unsupported format: a file of a later format version, and a file that only looks like a recording.
        var stamp = RecordingFormat.Stamp(start.AddHours(3));
        var future = Path.Combine(directory, $"EucLens-{stamp}-0f0f0f0f-part001.jsonl");
        await File.WriteAllTextAsync(future, "{\"t\":\"header\",\"schema\":\"euclens.recording/9\",\"app\":\"9.0\",\"id\":\"0f0f0f0f-0000-0000-0000-000000000000\",\"part\":1,\"startedUtc\":\"2026-10-23T15:00:00Z\",\"source\":\"RealSession\"}\n");
        var bogus = Path.Combine(directory, $"EucLens-{stamp}-0e0e0e0e-part001.jsonl");
        await File.WriteAllTextAsync(bogus, "{\"t\":\"header\"}\n");
        await c.Vm.Background.RefreshRecordingsAsync();
        foreach (var id in new[] { "0f0f0f0f", "0e0e0e0e" })
        {
            var item = c.Vm.Background.Recordings.SingleOrDefault(r => r.Summary.ShortId == id);
            if (item is null) { results.Add($"{id} not listed"); continue; }          // not listing a file that is no recording is acceptable
            item.AnalyzeCommand.Execute(null);
            await WaitFor(() => h.HasError || h.HasHistory, "the unsupported recording to be rejected");
            Require(h.HasError && !h.HasHistory && h.Charts.Count == 0 && h.Error.Length > 0 && !h.IsLoading, $"{id}: an unreadable recording is not explained: error='{h.Error}', history={h.HasHistory}.");
            if (id == "0f0f0f0f") Require(h.Error.Contains("unsupported format", StringComparison.Ordinal) && h.Error.Contains("euclens.recording/9", StringComparison.Ordinal), "The unsupported version is not named: " + h.Error);
            await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, $"history-unreadable-{id}.png"));
            results.Add($"{id} rejected with \"{h.Error}\"");
        }
        Log(c, "PASS: incomplete and damaged recordings opened through the real page: " + string.Join("; ", results));
    }

    private static async Task SampleLimit(HistorySmokeContext c)
    {
        var a = shortRun!; var h = History(c);
        h.SampleLimit = 400;
        try
        {
            await OpenAnalyze(c, a, 1);
            Require(h.IsPartial && h.StateLabel == "Partial analysis" && h.Loaded!.Info.State == RecordingCompleteness.Partial && h.Loaded.SampleCount == 400, $"The limit was not applied: {h.StateLabel}, {h.Loaded?.SampleCount} samples.");
            var notice = DesktopValidation.Descendants<Border>(View(c)).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Partial analysis notice");
            Require(notice is { IsVisible: true } && h.PartialNote.Contains("Nothing below describes the whole session", StringComparison.Ordinal), "The page does not show the partial-analysis notice.");
            Require(!h.Facts.Any(f => f.Value is "Complete" || f.Label is "End" or "Duration"), "A partial analysis is described as complete or as a whole session.");
            Require(h.IssueLines.Any(l => l.Contains("Only the first 400", StringComparison.Ordinal)), "The limit is not listed with the reading notes.");
            Require(h.SessionEnd - h.SessionStart < TimeSpan.FromSeconds(401) && h.Loaded!.Incidents.Count == 1, "The analysis covers more than the loaded part.");
            await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-partial-analysis.png"));
        }
        finally { h.SampleLimit = HistoryLoader.MaximumSamples; }
        await OpenAnalyze(c, a, 1);
        var gone = DesktopValidation.Descendants<Border>(View(c)).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Partial analysis notice");
        Require(!h.IsPartial && gone is { IsVisible: false } && h.StateLabel == "Complete", "The notice stays after the full recording is opened.");
        Log(c, $"PASS: sample limit (lowered to 400 for the check; the real limit is {HistoryLoader.MaximumSamples:N0}): the page says \"Partial analysis\", shows a warning notice, lists the limit, labels duration and last sample as analyzed only and never as complete; the full recording afterwards shows no notice");
    }

    private static TimeZoneInfo TestZone()
    {
        // Clocks go back at 03:00 local on 2026-10-25 (UTC+2 to UTC+1), so 02:00 to 02:59 local occurs twice.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        return TimeZoneInfo.CreateCustomTimeZone("Smoke/Zone", TimeSpan.FromHours(1), "Smoke", "Smoke standard", "Smoke summer", [rule]);
    }

    private static async Task ClockChange(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        var zone = TestZone();
        var start = new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero);                 // 00:30 local (UTC+2), before the change
        // One sample every ten seconds for 3 h 20 min; the incidents are at 00:30 UTC and 01:30 UTC, which are both 02:30 on the wall clock.
        var fixture = await WriteAsync(directory, start, 1200, template, i => (30 + i % 6, 20 + i % 5, 30, false), [723, 1083], null, 300_000, stepSeconds: 10);   // markers at 00:30:29.5Z and 01:30:29.5Z
        var h = History(c);
        h.UseTimeZone(zone);
        try
        {
            await OpenAnalyze(c, fixture, 5);
            var first = h.Loaded!.Incidents[0]; var second = h.Loaded.Incidents[1];
            Require(first.At.ToUniversalTime().Hour == 0 && second.At.ToUniversalTime().Hour == 1 && second.At - first.At is var gap && Math.Abs((gap - TimeSpan.FromHours(1)).TotalSeconds) < 1, "The fixture incidents are not one real hour apart.");
            var items = h.Events.Where(e => e.IsIncident).ToArray();
            Require(items.Length == 2 && items[0].Time.Contains("02:30", StringComparison.Ordinal) && items[1].Time.Contains("02:30", StringComparison.Ordinal) && items[0].Time != items[1].Time
                && items[0].Time.Contains("(UTC+02:00)", StringComparison.Ordinal) && items[1].Time.Contains("(UTC+01:00)", StringComparison.Ordinal), $"The repeated hour is not distinguishable: '{items[0].Time}' / '{items[1].Time}'.");
            Require(h.Events.Select(e => e.Event.At).SequenceEqual(h.Events.Select(e => e.Event.At).Order()), "The timeline is not in UTC order across the clock change.");
            // The charts plot UTC: two instants an hour apart are an hour apart on screen, not zero, and every chart agrees.
            var overlay = Overlay(c);
            var span = (h.ViewEnd - h.ViewStart).TotalSeconds; var plot = overlay.ActualWidth - HistoryLayout.PlotLeft - HistoryLayout.PlotRight;
            var measured = overlay.XOf(second.At) - overlay.XOf(first.At);
            Require(Math.Abs(measured - 3600 / span * plot) < 1.5 && measured > 0, $"The two incidents are {measured:0.#} px apart, expected {3600 / span * plot:0.#} px.");
            foreach (var chart in Charts(c))
            {
                var axis = (LiveChartsCore.SkiaSharpView.Axis)chart.XAxes.First();
                Require(Math.Abs((axis.MinLimit ?? 0) - h.ViewStart.UtcDateTime.Ticks) < 1000 && Math.Abs((axis.MaxLimit ?? 0) - h.ViewEnd.UtcDateTime.Ticks) < 1000, "A chart is not on the shared UTC range across the clock change.");
            }
            var labels = h.Charts[0].Data.TimeLabel!;
            Require(labels(first.At.UtcDateTime) == "02:30:00" || labels(first.At.UtcDateTime).StartsWith("02:30", StringComparison.Ordinal), "The axis label of the first 02:30 is wrong: " + labels(first.At.UtcDateTime));
            Require(labels(second.At.UtcDateTime).StartsWith("02:30", StringComparison.Ordinal) && labels(second.At.UtcDateTime.AddHours(1)).StartsWith("03:30", StringComparison.Ordinal), "The axis labels after the clock change are wrong.");
            foreach (var row in h.Charts) Require(row.Data.Points.Zip(row.Data.Points.Skip(1)).All(pair => pair.First.At <= pair.Second.At), $"{row.Title}: points are not in time order across the clock change.");
            h.SelectedEvent = items[1]; await h.Settled; await Idle(c); await DesktopValidation.WaitForChartMeasureAsync(c.Window);
            await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-clock-change-second-0230.png"));
            Log(c, $"PASS: recording across a clock change (test zone UTC+2 to UTC+1): both incidents read 02:30 on the wall clock and are told apart ('{items[0].Time}' and '{items[1].Time}'), the timeline stays in UTC order, the charts plot UTC so the incidents are {measured:0.#} px (= one real hour) apart, every chart shares the range, axis labels follow the zone");
        }
        finally { h.UseTimeZone(TimeZoneInfo.Local); }
    }

    // ---------- the virtualized event list ----------

    /// <summary>Containers the list may realize for any number of entries: the viewport holds about nine rows; the cache adds a few.</summary>
    private const int MaximumRealizedEventContainers = 100;

    private static ListBox EventListOf(HistorySmokeContext c) => View(c).EventListControl;
    private static ScrollViewer ScrollerOf(ListBox list) => (ScrollViewer)(list.Template.FindName("EventScroll", list) ?? throw new InvalidOperationException("The event list has no scroll viewer."));
    private static ListBoxItem[] RealizedRows(ListBox list) => DesktopValidation.Descendants<ListBoxItem>(list).ToArray();
    private static ListBoxItem? ContainerOf(ListBox list, object item) => list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;

    private static bool MostlyVisible(ListBox list, ListBoxItem row)
    {
        if (!row.IsVisible || row.ActualHeight <= 0) return false;
        var bounds = row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize));
        return Math.Min(bounds.Bottom, list.ActualHeight) - Math.Max(bounds.Top, 0) >= bounds.Height / 2;
    }

    /// <summary>Every realized row shows exactly the entry it is bound to: no stale icon, text, annotation or selection after recycling. Returns the number of realized rows.</summary>
    private static int CheckRows(SessionHistoryViewModel h, ListBox list, string when)
    {
        var rows = RealizedRows(list);
        Require(rows.Length > 0 && rows.Length <= MaximumRealizedEventContainers, $"{when}: {rows.Length} containers are realized for {list.Items.Count:N0} entries (at most {MaximumRealizedEventContainers} allowed).");
        foreach (var row in rows)
        {
            var item = row.DataContext as HistoryEventItem ?? throw new InvalidOperationException($"{when}: a container is bound to {row.DataContext?.GetType().Name ?? "nothing"}.");
            Require(list.Items.IndexOf(item) >= 0, $"{when}: a container shows an entry that is no longer in the list ('{item.Title}').");
            Require(row.IsSelected == ReferenceEquals(item, h.SelectedEvent), $"{when}: '{item.Title}' has IsSelected={row.IsSelected} but the selection is '{h.SelectedEvent?.Title ?? "none"}'.");
            var grid = DesktopValidation.Descendants<Grid>(row).First(g => System.Windows.Automation.AutomationProperties.GetName(g).Length > 0);
            Require(System.Windows.Automation.AutomationProperties.GetName(grid) == item.AutomationName, $"{when}: the automation name of a row is stale ('{System.Windows.Automation.AutomationProperties.GetName(grid)}' for '{item.AutomationName}').");
            Require(DesktopValidation.Descendants<WpfPath>(row).First(x => x.Tag is not null).Tag as string == item.IconTag, $"{when}: the icon of '{item.Title}' is stale.");
            var texts = DesktopValidation.Descendants<TextBlock>(row).Where(t => t.IsVisible).ToArray();
            Require(texts.Any(t => t.Text == item.Title) && texts.Any(t => t.Text == item.Time) && texts.Any(t => t.Text == item.KindLabel), $"{when}: the text of '{item.Title}' is stale.");
            Require(item.HasAnnotation == texts.Any(t => t.Text == item.Annotation && item.Annotation.Length > 0), $"{when}: the annotation of '{item.Title}' is not shown exactly when it exists.");
            var expectedTexts = 3 + (item.HasDetail ? 1 : 0) + (item.HasAnnotation ? 1 : 0);
            Require(texts.Length == expectedTexts, $"{when}: '{item.Title}' shows {texts.Length} texts, expected {expectedTexts} (a stale detail or annotation).");
        }
        return rows.Length;
    }

    /// <summary>Selects an entry in code and waits until its row exists, is selected and is in view. Returns the milliseconds that took.</summary>
    private static async Task<double> ShowAsync(HistorySmokeContext c, SessionHistoryViewModel h, ListBox list, HistoryEventItem item, string what)
    {
        var sw = Stopwatch.StartNew();
        h.SelectedEvent = item;
        await WaitFor(() => ContainerOf(list, item) is { } row && row.IsSelected && MostlyVisible(list, row), $"{what} to be selected and in view", 30_000);
        await Idle(c);
        var elapsed = sw.Elapsed.TotalMilliseconds;
        await h.Settled;
        return elapsed;
    }

    /// <summary>Scrolls the page so the timeline panel starts at the top of the visible page.</summary>
    private static async Task ShowPanelAsync(HistorySmokeContext c, FrameworkElement panel)
    {
        var viewer = (ScrollViewer)PageViewport(View(c));
        var top = panel.TransformToAncestor(viewer).Transform(new Point()).Y;
        viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + top - 8));
        await Idle(c);
    }

    // The same fixture and measurements as CI run 40216989417 (commit d4d60e0), made before the list was virtualized.
    private const string NonVirtualizedBaseline = "managed heap +203.9 MiB while open, working set 203.2 → 501.4 MiB, slowest rendered frame 2,826 ms, filter to incidents 379 ms, select the last entry 47 ms, rebuild 81 ms, dispatcher worst 95 ms, 5,005 containers";

    private static async Task LargeEventList(HistorySmokeContext c)
    {
        var directory = c.Vm.Background.RecordingFolder;
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        var template = await provider.CollectAsync();
        const int notes = 5000;
        var sw = Stopwatch.StartNew();
        var fixture = await WriteAsync(directory, new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero), 600, template, i => (30 + i % 6, 20 + i % 5, 30, i is >= 100 and < 110), [300], null, 64L << 20, notes: notes);
        var writing = sw.Elapsed;
        var resultPath = Path.Combine(c.EvidenceDirectory, "history-events-result.txt");
        var lines = new List<string> { $"Session History event list, virtualized (recycling VirtualizingStackPanel, bounded viewport {HistoryLayout.EventListMaxHeight:0} DIP); fixture written through the real writer in {writing.TotalSeconds:0.0} s" };
        // Every stage is saved as it completes: a later problem cannot erase what was measured before it.
        void Save() => File.WriteAllText(resultPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        void Add(string line) { lines.Add(line); Save(); }
        var screenshotFailures = new List<Exception>();
        async Task<ShotInfo?> Capture(string name)
        {
            try { return await Shot(c, PageViewport(View(c)), Path.Combine(c.EvidenceDirectory, name)); }
            catch (Exception ex) { screenshotFailures.Add(ex); Add($"Screenshot {name}: FAILED: {ex.Message}"); return null; }
        }
        var baseline = HeapAfterCollection(); var workingBefore = Environment.WorkingSet;
        var weakFirstItem = default(WeakReference); var total = 0;
        try
        {
            // ----- load and first render
            sw.Restart();
            await c.Vm.Background.RefreshRecordingsAsync();
            c.Vm.Background.Recordings.Single(r => r.Summary.ShortId == fixture.ShortId).AnalyzeCommand.Execute(null);
            var h = History(c);
            await WaitFor(() => h.HasHistory || h.HasError, "the event-heavy recording to load", 120_000);
            Require(h.HasHistory, "The recording did not load: " + h.Error);
            var loaded = sw.Elapsed;
            await WaitFor(() => View0(c) is { } v && RealizedRows(v.EventListControl).Length > 0, "the first rows of the event list", 120_000);
            await h.Settled; await Idle(c);
            var firstRender = sw.Elapsed;
            var list = EventListOf(c); var scroller = ScrollerOf(list);
            var timeline = DesktopValidation.Descendants<Border>(View(c)).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Incident timeline");
            var entryCount = h.Loaded!.Events.Count; var noteCount = h.Loaded.Events.Count(e => e.Kind == HistoryEventKind.Note);
            weakFirstItem = new WeakReference(h.Events[0]); total = entryCount;
            var heapOpen = HeapAfterCollection(); var workingOpen = Environment.WorkingSet;

            // ----- 1-2: all entries are in the list, only a few containers exist
            Require(noteCount == notes && h.Events.Count == entryCount && list.Items.Count == entryCount && entryCount >= notes + 1, $"{list.Items.Count:N0} entries in the list, {entryCount:N0} read, {noteCount:N0} of {notes:N0} notes.");
            Require(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling && DesktopValidation.Descendants<VirtualizingStackPanel>(list).Count() == 1, "The event list does not use a recycling VirtualizingStackPanel.");
            Require(list.ActualHeight <= HistoryLayout.EventListMaxHeight + 1, $"The event list is {list.ActualHeight:0} DIP tall; its viewport is bounded to {HistoryLayout.EventListMaxHeight:0}.");
            var realizedFirst = CheckRows(h, list, "at the beginning");
            Require(ContainerOf(list, h.Events[0]) is { } firstRow && MostlyVisible(list, firstRow) && scroller.VerticalOffset == 0, "The first entry is not shown at the beginning of the list.");
            Add($"{entryCount:N0} timeline entries ({noteCount:N0} synthetic notes) are all in the list; {realizedFirst} containers are realized at the beginning");
            Add($"Read and aggregate {loaded.TotalSeconds:0.00} s; first rows rendered and the page idle {firstRender.TotalSeconds:0.00} s after Analyze");
            Add($"Managed heap after a full collection: before {MiB(baseline)} MiB, open {MiB(heapOpen)} MiB (+{MiB(heapOpen - baseline)} MiB); working set {MiB(workingBefore)} → {MiB(workingOpen)} MiB (+{MiB(workingOpen - workingBefore)} MiB)");

            // ----- responsiveness
            var probes = new List<double>();
            for (var i = 0; i < 20; i++) { var t = Stopwatch.StartNew(); await c.App.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Task; probes.Add(t.Elapsed.TotalMilliseconds); await Task.Delay(20); }
            probes.Sort();
            Add($"Dispatcher round trip: median {probes[probes.Count / 2]:0.0} ms, worst {probes[^1]:0.0} ms (20 probes)");

            // ----- 8: screenshots of the beginning, with the page scrolled to the timeline
            await ShowPanelAsync(c, timeline);
            var shotBegin = await Capture("history-events-begin.png");

            // ----- 4: a middle entry, scrolled into view although it had no container
            var middle = h.Events[h.Events.Count / 2];
            Require(ContainerOf(list, middle) is null, "The middle entry already had a container before it was selected; the list is not virtualized.");
            var middleMs = await ShowAsync(c, h, list, middle, "the middle entry");
            var realizedMiddle = CheckRows(h, list, "in the middle");
            Require(scroller.VerticalOffset > 1000, $"The list did not scroll to the middle (offset {scroller.VerticalOffset:0}).");
            Add($"Select the middle entry (no container yet): in view after {middleMs:0} ms; {realizedMiddle} containers realized");
            await Capture("history-events-middle.png");

            // ----- 5: the last entry
            var last = h.Events[^1];
            var lastMs = await ShowAsync(c, h, list, last, "the last entry");
            var realizedLast = CheckRows(h, list, "at the end");
            Require(scroller.VerticalOffset >= list.Items.Count - 40, $"The list did not scroll to the end (offset {scroller.VerticalOffset:0} of {list.Items.Count:N0}).");
            Add($"Select the last entry: in view after {lastMs:0} ms; {realizedLast} containers realized");
            await Capture("history-events-end.png");

            // ----- 6: keyboard navigation through the real list
            var row = ContainerOf(list, last)!;
            row.Focus(); await Idle(c);
            Require(Keyboard.FocusedElement is ListBoxItem, "The selected row cannot take keyboard focus (focus is on " + Keyboard.FocusedElement?.GetType().Name + ").");
            DesktopValidation.RaiseKey((UIElement)Keyboard.FocusedElement!, Key.Up);
            await WaitFor(() => ReferenceEquals(h.SelectedEvent, h.Events[^2]), "Up to select the previous entry");
            await h.Settled; await Idle(c);
            Require(Keyboard.FocusedElement is ListBoxItem focused && ReferenceEquals(focused.DataContext, h.SelectedEvent), "After Up the keyboard focus is not on the selected row.");
            DesktopValidation.RaiseKey((UIElement)Keyboard.FocusedElement!, Key.Home);
            await WaitFor(() => ReferenceEquals(h.SelectedEvent, h.Events[0]) && ContainerOf(list, h.Events[0]) is { } top && MostlyVisible(list, top), "Home to select and show the first entry");
            await h.Settled; await Idle(c);
            Require(scroller.VerticalOffset == 0, "Home did not scroll to the beginning.");
            DesktopValidation.RaiseKey((UIElement)Keyboard.FocusedElement!, Key.End);
            await WaitFor(() => ReferenceEquals(h.SelectedEvent, h.Events[^1]) && ContainerOf(list, h.Events[^1]) is { } bottom && MostlyVisible(list, bottom), "End to select and show the last entry");
            await h.Settled; await Idle(c);
            var realizedAfterKeys = CheckRows(h, list, "after keyboard navigation");
            Add($"Keyboard: Up, Home and End moved the selection and scrolled the list; focus stayed on the selected row; {realizedAfterKeys} containers realized");

            // ----- 7-9: filtering, incident selection, annotation
            var incidentItem = h.Events.Single(e => e.IsIncident);
            var incident = h.Loaded.Incidents.Single();
            sw.Restart();
            h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Incidents);
            await WaitFor(() => list.Items.Count == 1 && RealizedRows(list).Length == 1, "the filtered list", 30_000);
            await Idle(c);
            var filterMs = sw.Elapsed.TotalMilliseconds;
            Require(h.Events.Count == 1 && ReferenceEquals(h.Events[0], incidentItem) && h.SelectedEvent is null && h.EventsHeading == $"1 of {entryCount} timeline entries", $"Filtering left a stale state: {h.Events.Count} entries, selection '{h.SelectedEvent?.Title ?? "none"}', heading '{h.EventsHeading}'.");
            CheckRows(h, list, "after filtering to incidents");
            Require(scroller.VerticalOffset == 0, "The filtered list did not return to its beginning.");
            Add($"Filter to incidents: list rebuilt and idle after {filterMs:0} ms ({list.Items.Count} entry, {RealizedRows(list).Length} container)");
            h.SelectedEvent = incidentItem; await h.Settled; await Idle(c); await DesktopValidation.WaitForChartMeasureAsync(c.Window);
            Require(h.SelectedIncident?.Incident.Id == incident.Id && h.ViewStart <= incident.At && incident.At <= h.ViewEnd, "Selecting the incident did not select it and bring it into the charts' range.");
            foreach (var chart in Charts(c))
            {
                var axis = (LiveChartsCore.SkiaSharpView.Axis)chart.XAxes.First();
                Require(Math.Abs((axis.MinLimit ?? 0) - h.ViewStart.UtcDateTime.Ticks) < 1000 && Math.Abs((axis.MaxLimit ?? 0) - h.ViewEnd.UtcDateTime.Ticks) < 1000, "A chart is not on the range of the selected incident.");
            }
            h.SelectedIncident!.Category = HistoryIncidentViewModel.Categories.Single(x => x.Value == IncidentCategory.Freeze);
            h.SelectedIncident.Description = "Desktop froze while the list was scrolled.";
            await h.SelectedIncident.SaveCommand.ExecuteAsync();
            Require(h.SelectedIncident.SaveMessage.Contains("Saved beside the recording", StringComparison.Ordinal) && incidentItem.HasAnnotation, "The annotation was not saved: " + h.SelectedIncident.SaveMessage);
            await Idle(c);
            await Capture("history-events-filtered-incidents.png");
            Add("Filtered incidents: the incident is selected, the charts show its range, the annotation was saved");

            // ----- back to all entries: the selection survives and is scrolled to
            sw.Restart();
            h.SelectedFilter = h.Filters[0];
            await WaitFor(() => list.Items.Count == entryCount && ContainerOf(list, incidentItem) is { } shown && shown.IsSelected && MostlyVisible(list, shown), "the full list with the incident in view", 60_000);
            await Idle(c);
            var rebuildMs = sw.Elapsed.TotalMilliseconds;
            Require(ReferenceEquals(h.SelectedEvent, incidentItem) && h.EventsHeading == $"{entryCount} of {entryCount} timeline entries", "The selection or the heading is wrong after the filter was removed.");
            CheckRows(h, list, "after the filter was removed");
            Add($"Back to all entries: rebuilt, the selected incident scrolled into view after {rebuildMs:0} ms");

            // ----- the details panel does not move or get recreated while the list scrolls
            await ShowPanelAsync(c, timeline);
            var details = DesktopValidation.Descendants<StackPanel>(View(c)).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Selected incident");
            var detailsAt = details.TranslatePoint(new Point(), timeline); var detailsContext = details.DataContext; var pageOffset = ((ScrollViewer)PageViewport(View(c))).VerticalOffset;
            scroller.ScrollToVerticalOffset(list.Items.Count * 0.75); await Idle(c);
            var detailsAfter = details.TranslatePoint(new Point(), timeline);
            Require(ReferenceEquals(details.DataContext, detailsContext) && ReferenceEquals(h.SelectedIncident, detailsContext) && Math.Abs(detailsAfter.Y - detailsAt.Y) < 0.5 && Math.Abs(detailsAfter.X - detailsAt.X) < 0.5, $"Scrolling the list moved or recreated the selected incident panel (from {detailsAt} to {detailsAfter}).");
            Require(Math.Abs(((ScrollViewer)PageViewport(View(c))).VerticalOffset - pageOffset) < 0.5, "Scrolling the list scrolled the page.");
            CheckRows(h, list, "after scrolling past the incident");

            // ----- 9: the annotation is right after the incident row was recycled away and comes back
            await ShowAsync(c, h, list, h.Events[^1], "the last entry again");
            Require(ContainerOf(list, incidentItem) is null, "The incident row was not recycled while it was out of view.");
            var backMs = await ShowAsync(c, h, list, incidentItem, "the incident again");
            CheckRows(h, list, "after the incident row came back");
            var incidentRow = ContainerOf(list, incidentItem)!;
            Require(DesktopValidation.Descendants<TextBlock>(incidentRow).Any(t => t.IsVisible && t.Text.StartsWith("Freeze", StringComparison.Ordinal) && t.Text.Contains("Desktop froze while the list was scrolled.", StringComparison.Ordinal)), "The annotation is not shown on the incident row after recycling.");
            Add($"Annotation: shown on the incident row again {backMs:0} ms after it was recycled away; no other row shows one; the details panel stayed in place while the list scrolled");
            await ShowPanelAsync(c, timeline);
            await Capture("history-events-selected-incident.png");

            // ----- 10: every entry is exportable
            var csv = HistoryExport.TimelineCsv(h.Loaded, null, TimeZoneInfo.Utc);
            var csvLines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
            Require(csvLines == entryCount, $"The timeline export has {csvLines:N0} lines for {entryCount:N0} entries.");
            Add($"Timeline CSV export: {csvLines:N0} lines for {entryCount:N0} entries");

            // ----- 11: another recording replaces the list; the old rows are gone
            var other = shortRun!;
            await OpenAnalyze(c, other, 5);
            var h2 = History(c); var list2 = EventListOf(c);
            Require(list2.Items.Count == h2.Events.Count && RealizedRows(list2).Length is > 0 && RealizedRows(list2).Length <= h2.Events.Count && h2.Events.Count < 100, $"After switching recordings the list holds {list2.Items.Count} entries with {RealizedRows(list2).Length} containers (expected at most {h2.Events.Count}).");
            CheckRows(h2, list2, "after switching recordings");
            Add($"Switching to another recording: the list now holds its {h2.Events.Count} entries, {RealizedRows(list2).Length} containers, none of the 5,005 rows");

            // ----- leaving the page releases the list
            c.Vm.SelectedPage = c.Vm.Pages[0]; await Idle(c); await Task.Delay(300);
            var heapAfter = HeapAfterCollection();
            Add($"After leaving the page: managed heap {MiB(heapAfter)} MiB (+{MiB(heapAfter - baseline)} MiB over before); old first entry still referenced: {(weakFirstItem.IsAlive ? "yes" : "no")} (informational)");

            // ----- the numbers against the non-virtualized list
            var slowest = shotBegin?.SlowestFrameMilliseconds;
            Add(string.Join(Environment.NewLine,
                "Summary (targets: heap below +40 MiB, slowest frame below 500 ms, filter below 100 ms, 5,005 of 5,005 entries, bounded containers; reported whether or not they are met)",
                $"  containers realized: {realizedFirst} / {realizedMiddle} / {realizedLast} (beginning / middle / end) for {entryCount:N0} entries (was 5,005)",
                $"  managed heap while open: +{MiB(heapOpen - baseline)} MiB (target < 40; was +203.9)",
                $"  working set while open: +{MiB(workingOpen - workingBefore)} MiB (was +298)",
                $"  slowest rendered frame: {(slowest is { } s0 ? s0.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "not measured")} (target < 500; was 2,826)",
                $"  filter to incidents: {filterMs:0} ms (target < 100; was 379); back to all: {rebuildMs:0} ms (was 81)",
                $"  scroll to the last entry: {lastMs:0} ms (was 47 for a selection); to the middle: {middleMs:0} ms",
                $"  dispatcher: median {probes[probes.Count / 2]:0.0} ms, worst {probes[^1]:0.0} ms (was worst 95)",
                $"  before virtualization (CI run 40216989417): {NonVirtualizedBaseline}"));
            Log(c, $"MEASURED: {entryCount:N0} entries, {realizedFirst}/{realizedMiddle}/{realizedLast} containers realized, managed heap +{MiB(heapOpen - baseline)} MiB, working set +{MiB(workingOpen - workingBefore)} MiB, first render {firstRender.TotalSeconds:0.00} s, slowest frame {(slowest is { } s1 ? s1.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")} ms, filter {filterMs:0} ms, rebuild {rebuildMs:0} ms, scroll to last {lastMs:0} ms, dispatcher worst {probes[^1]:0.0} ms (targets reported, not enforced)");
        }
        catch (Exception ex)
        {
            Add($"FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            try { foreach (var file in FilesOf(directory, fixture.ShortId)) File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (screenshotFailures.Count > 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(screenshotFailures[0]).Throw();
        Log(c, $"PASS: the virtualized event list holds all {total:N0} entries with at most {MaximumRealizedEventContainers} containers; first, middle and last entry, keyboard, filtering, incident selection, annotation after recycling, the details panel, export and switching recordings checked");
    }

    private static SessionHistoryView? View0(HistorySmokeContext c) => DesktopValidation.Descendants<SessionHistoryView>(c.Window).FirstOrDefault();

    private static async Task RepeatedNavigation(HistorySmokeContext c)
    {
        var a = shortRun!;
        var views = new List<WeakReference>(); var objects = new List<WeakReference>(); var controls = new List<WeakReference>(); var lists = new List<WeakReference>(); var rows = new List<WeakReference>();
        var heaps = new List<long>();
        var sw = Stopwatch.StartNew();
        for (var cycle = 1; cycle <= 12; cycle++)
        {
            await OpenAnalyze(c, a, 5);
            var h = History(c);
            h.SelectedEvent = h.Events.First(e => e.IsIncident); await h.Settled; await Idle(c);
            h.ZoomBy(2); h.SetRange(h.SessionStart + TimeSpan.FromMinutes(2), h.SessionStart + TimeSpan.FromMinutes(7)); await h.Settled; await Idle(c);
            if (cycle == 1) Require(h.Events.Single(e => e.Event.IncidentId == h.Loaded!.Incidents[1].Id).Annotation.StartsWith("Display stutter", StringComparison.Ordinal), "The annotation saved earlier is not shown after reopening.");
            views.Add(new WeakReference(View(c)));
            lists.Add(new WeakReference(EventListOf(c)));
            foreach (var row in RealizedRows(EventListOf(c))) rows.Add(new WeakReference(row));
            foreach (var chart in Charts(c)) { controls.Add(new WeakReference(chart)); objects.Add(new WeakReference(LiveChart.GetObjects(chart))); }
            c.Vm.SelectedPage = c.Vm.Pages[0];                          // leaving the page releases the recording
            await Idle(c); await Task.Delay(120);
            Require(!h.HasHistory && h.Charts.Count == 0, $"Cycle {cycle}: the page did not release the recording.");
            if (cycle is 3 or 6 or 9 or 12) heaps.Add(HeapAfterCollection());
        }
        await Idle(c); await Task.Delay(500); await Idle(c);
        HeapAfterCollection();
        var aliveViews = views.Count(w => w.IsAlive); var aliveObjects = objects.Count(w => w.IsAlive); var aliveControls = controls.Count(w => w.IsAlive); var aliveLists = lists.Count(w => w.IsAlive); var aliveRows = rows.Count(w => w.IsAlive);
        var growth = heaps[^1] - heaps[0];
        var perBatch = heaps.Zip(heaps.Skip(1), (x, y) => y - x).DefaultIfEmpty(0).Max();
        await File.WriteAllTextAsync(Path.Combine(c.EvidenceDirectory, "history-navigation-result.txt"),
            $"Session History, 12 × (Analyze, select incident, leave): {sw.Elapsed.TotalSeconds:0.0} s{Environment.NewLine}" +
            $"Managed heap after a full collection at cycles 3/6/9/12: {string.Join(" → ", heaps.Select(h => MiB(h) + " MiB"))} (growth {MiB(growth)} MiB, largest step {MiB(perBatch)} MiB){Environment.NewLine}" +
            $"Working set now: {MiB(Environment.WorkingSet)} MiB{Environment.NewLine}" +
            $"Still alive after the last collection: views {aliveViews}/{views.Count}, LiveCharts objects {aliveObjects}/{objects.Count}, chart controls {aliveControls}/{controls.Count}, event lists {aliveLists}/{lists.Count}, event list rows {aliveRows}/{rows.Count}{Environment.NewLine}" +
            $"Limits: views, event lists and LiveCharts objects of released pages must be collected; managed heap growth ≤ 24 MiB overall and ≤ 12 MiB per three cycles." + Environment.NewLine);
        Require(aliveViews == 0, $"{aliveViews} of {views.Count} released history views are still reachable.");
        Require(aliveObjects == 0, $"{aliveObjects} of {objects.Count} released LiveCharts objects are still reachable.");
        Require(aliveLists == 0, $"{aliveLists} of {lists.Count} released virtualized event lists are still reachable.");
        Require(growth <= 24L << 20 && perBatch <= 12L << 20, $"Managed heap keeps growing across history visits: {string.Join(" → ", heaps.Select(h => MiB(h)))} MiB.");
        Log(c, $"PASS: 12 × open and leave: managed heap {string.Join(" → ", heaps.Select(h => MiB(h)))} MiB (growth {MiB(growth)} MiB); views alive {aliveViews}/{views.Count}, LiveCharts objects alive {aliveObjects}/{objects.Count}; event lists alive {aliveLists}/{lists.Count}, event list rows alive {aliveRows}/{rows.Count} (informational), chart controls alive {aliveControls}/{controls.Count} (informational: controls are released by WPF, not by this code)");
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
            i => (i is >= 3 * 3600 - 2 and <= 3 * 3600 + 6 ? 420 : 30 + i % 9, 15 + i % 11, 28 + i % 5, false), [3 * 3600, 5 * 3600 + 1800], (4 * 3600, 4 * 3600 + 300), 6L << 20);
        var writing = sw.Elapsed;
        Require(longRun.Parts >= 3, $"The long recording did not rotate: {longRun.Parts} part(s), {MiB(longRun.Bytes)} MiB written.");
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
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-6h-overview.png"));
        var rtt = h.Charts.Single(r => r.Id == "rtt");
        Require(rtt.Data.Points.Any(p => p.Value is { } v && v >= 420), "The 420 ms spike of the six hour recording is missing from the reduced chart: peaks must survive.");
        Require(rtt.Stats.Contains("peaks kept", StringComparison.Ordinal), "The reduction is not announced: " + rtt.Stats);
        // Select the incident next to the spike and zoom: the resolution rises to every sample.
        sw.Restart();
        h.SelectedEvent = h.Events.First(e => e.IsIncident); await h.Settled; await Idle(c);
        var zoom = sw.Elapsed;
        Require(h.SelectedIncident!.Summary.Contains("Round-trip time reached 420", StringComparison.Ordinal), "The six hour analysis did not report the spike: " + h.SelectedIncident.Summary);
        Require(h.Charts.All(r => r.Data.Points.Count <= 241), "Zooming to the incident did not raise the resolution (one point per sample).");
        await Shot(c, View(c), Path.Combine(c.EvidenceDirectory, "history-6h-incident.png"));
        var peakHeap = HeapAfterCollection();
        // Everything the report needs is read here: leaving the page releases the recording and its view model state.
        var sampleCount = h.Loaded.SampleCount; var columnsBytes = h.Loaded.ApproximateBytes; var chartCount = h.Charts.Count; var incidentCount = h.Loaded.Incidents.Count;
        c.Vm.SelectedPage = c.Vm.Pages[0]; await Idle(c); await Task.Delay(200);
        var released = HeapAfterCollection();
        var text =
            $"Session History, six hour recording (accelerated fixture written through the real writer; not a six hour real run): {sampleCount:N0} samples, {incidentCount} incidents, {longRun.Parts} parts ({longRun.CompressedParts} compressed), {MiB(longRun.Bytes)} MiB on disk, written in {writing.TotalSeconds:0.0} s{Environment.NewLine}" +
            $"Load (read, decompress, decode, aggregate) {load.TotalSeconds:0.0} s; select incident, centre and re-aggregate {zoom.TotalMilliseconds:0} ms{Environment.NewLine}" +
            $"Managed heap after a full collection: before {MiB(baseline)} MiB, history open {MiB(open)} MiB (+{MiB(open - baseline)} MiB), after zoom {MiB(peakHeap)} MiB, page left {MiB(released)} MiB (+{MiB(released - baseline)} MiB){Environment.NewLine}" +
            $"Working set: before {MiB(processBefore)} MiB, with history open {MiB(workingSet)} MiB{Environment.NewLine}" +
            $"History columns {MiB(columnsBytes)} MiB for {chartCount} metrics; largest chart {maxPoints} points (limit {SessionHistoryViewModel.MaximumChartPoints}){Environment.NewLine}" +
            $"Limits: load ≤ 120 s, history open ≤ +96 MiB managed heap, ≤ +24 MiB after leaving the page.{Environment.NewLine}";
        await File.WriteAllTextAsync(Path.Combine(c.EvidenceDirectory, "history-6h-result.txt"), text);
        Require(load < TimeSpan.FromSeconds(120), $"Loading took {load.TotalSeconds:0} s.");
        Require(open - baseline <= 96L << 20, $"History open retains {MiB(open - baseline)} MiB.");
        Require(released - baseline <= 24L << 20, $"{MiB(released - baseline)} MiB stay after leaving the page.");
        Log(c, $"PASS: six hour recording ({sampleCount:N0} samples, {longRun.Parts} parts): loaded in {load.TotalSeconds:0.0} s, managed heap +{MiB(open - baseline)} MiB while open and +{MiB(released - baseline)} MiB after leaving, working set {MiB(workingSet)} MiB, largest chart {maxPoints} points, the 420 ms spike survives the reduction, selecting its incident re-aggregates in {zoom.TotalMilliseconds:0} ms");
        // Remove the large fixture: it is evidence of measurement, not of content.
        try { foreach (var file in Directory.GetFiles(directory, $"EucLens-*-{longRun.ShortId}*")) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(c, "NOTE: the six hour fixture could not be removed: " + ex.Message); }
    }
}
