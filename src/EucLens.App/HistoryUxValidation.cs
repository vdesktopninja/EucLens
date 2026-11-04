using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly record struct Point(double Rtt, double Loss, double Fps, double? Dirty, double Dropped, double Cpu, double Ram);

    private sealed record Scenario(string Key, string Title, int Samples, int StepSeconds, Func<double, Point> Shape, int[] IncidentSeconds, (int From, int To)[] Pauses);
    private sealed record Written(Scenario Scenario, string ShortId);

    private static bool In(double t, double from, double to) => t >= from && t < to;
    private static Point Healthy(double t) => new(30 + t % 7, 0, 28 + t % 4, 20 + t % 5, 0.5, 22 + t % 9, 45);

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

    /// <summary>Layout facts of one state, logged as evidence (the redesign adds its assertions here).</summary>
    private static Task Layout(HistorySmokeContext c, string scenario, bool dark, int width, int height, double scale, FrameworkElement client)
    {
        var view = HistoryValidation.View(c);
        HistoryValidation.Log(c, string.Create(CultureInfo.InvariantCulture, $"NOTE: layout {scenario} {(dark ? "dark" : "light")} {width}×{height} {scale:P0}: client {client.ActualWidth:0}×{client.ActualHeight:0} DIP, page {view.ActualWidth:0}×{view.ActualHeight:0} DIP"));
        return Task.CompletedTask;
    }
}
