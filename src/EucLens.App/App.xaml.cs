using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EucLens.App;

public partial class App : Application
{
    private ServiceProvider? services;
    private readonly CancellationTokenSource cancellation = new();
    private Task? sampling;
    private bool closing;
    private bool smoke;
    private readonly List<string> unhandled = [];
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        smoke = e.Args.Contains("--smoke-test");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var registrations = new ServiceCollection();
            registrations.AddLogging(builder => builder.AddProvider(new LocalFileLoggerProvider()));
            registrations.AddSingleton<IDemoMetricsProvider, MockMetricsProvider>();
            registrations.AddSingleton<IBlastCounterReader, WindowsBlastCounterReader>();
            registrations.AddSingleton<IBlastSessionContext, WindowsBlastSessionContext>();
            registrations.AddSingleton<OmnissaBlastProvider>();
            registrations.AddSingleton<IHostMetricsCollector>(_ => new HostMetricsCollector());
            registrations.AddSingleton<ISessionProtocolProbe, WtsSessionProtocolProbe>();
            registrations.AddSingleton<IMetricsProvider, RealSessionMetricsProvider>();
            registrations.AddSingleton<ISessionRecorder, SessionRecorder>();
            registrations.AddSingleton<IDiagnosticEngine>(_ => new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()));
            registrations.AddSingleton<IDiagnosticSettingsStore, DiagnosticSettingsStore>();
            registrations.AddSingleton<IDesktopServices>(new DesktopServices { AutoConfirmNewRecording = smoke });
            registrations.AddSingleton<MainViewModel>();
            services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var vm = services.GetRequiredService<MainViewModel>();
            BindingErrorListener? bindingErrors = null;
            if (smoke)
            {
                DispatcherUnhandledException += (_, args) => unhandled.Add(args.Exception.ToString());
                TaskScheduler.UnobservedTaskException += (_, args) => unhandled.Add(args.Exception.ToString());
                bindingErrors = new BindingErrorListener();
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            }
            var window = new MainWindow { DataContext = vm };
            MainWindow = window;
            window.Closing += WindowClosing;
            window.Show();
            await vm.InitializeAsync(cancellation.Token);
            sampling = vm.RunAsync(cancellation.Token);
            if (smoke) await RunSmokeAsync(vm, bindingErrors!);
        }
        catch (Exception ex)
        {
            services?.GetService<ILogger<App>>()?.LogCritical(ex, "Application startup or smoke test failed.");
            if (smoke) await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), ex.ToString());
            else MessageBox.Show($"EUC Lens could not start. {ex.Message}", "EUC Lens", MessageBoxButton.OK, MessageBoxImage.Error);
            await StopAsync();
            closing = true;
            Shutdown(1);
        }
    }
    private async Task RunSmokeAsync(MainViewModel vm, BindingErrorListener errors)
    {
        var recorder = services!.GetRequiredService<ISessionRecorder>();
        var directory = Path.Combine(Path.GetTempPath(), "EucLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidence = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message + " Status: " + vm.Status); }
        static double?[] Points(ISeries[] series) => ((LineSeries<DateTimePoint>)series[0]).Values!.Select(p => p.Value).ToArray();
        try
        {
            // CI runners have no Horizon Agent: Real Session must keep running and report N/A, never demo values.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.RealSession);
            vm.StartRecordingCommand.Execute(null);
            await Task.Delay(2200);
            vm.StopRecordingCommand.Execute(null);
            Check(vm.Status.StartsWith("REAL SESSION", StringComparison.Ordinal) || vm.Status.StartsWith("Recording stopped", StringComparison.Ordinal), "Real Session status missing.");
            Check(vm.ModeBanner.StartsWith("REAL SESSION", StringComparison.Ordinal), "Real Session banner missing.");
            // No Horizon Agent: Blast fields are N/A. Host CPU is protocol-independent and must be measured.
            string Card(string label) => vm.OverviewCards.SingleOrDefault(c => c.Label == label)?.Value
                ?? throw new InvalidOperationException($"Overview card {label} missing ({vm.OverviewCards.Count} cards, page {vm.SelectedPage.Name}, mode {vm.ModeLabel}). Status: {vm.Status}");
            Check(Card("RTT") == "N/A" && Card("FPS") == "N/A" && Card("BANDWIDTH") == "N/A", "Real Session without Horizon Agent must show N/A for Blast metrics.");
            Check(Card("CPU") != "N/A" && Card("CPU").EndsWith('%'), "Real CPU utilization was not measured.");
            Check(vm.SystemCards.Any(c => c.Label == "Available RAM" && c.Value.EndsWith("GiB", StringComparison.Ordinal)), "Real available RAM was not measured.");
            Check(vm.ProtocolDisplay == "Not detected" && vm.ProtocolDetail.Contains("Not detected", StringComparison.Ordinal), "Real Session must report the protocol as not detected.");
            Check(vm.SessionCards.Any(c => c.Label == "Active protocol" && c.Value == "Not detected"), "Session card shows a demo protocol in Real Session.");
            Check(vm.GpuCards.Count > 0 && vm.GraphicsCards.Any(c => c.Label == "Active monitors"), "GPU/display rows missing.");
            Check(vm.RecordingState.Contains("REAL SESSION", StringComparison.Ordinal) && vm.SampleCount >= 1, "Real Session recording failed.");
            var realRtt = Points(vm.RttSeries);
            Check(realRtt.Length >= 1 && realRtt.All(v => v is null), "Real RTT chart must contain gaps, not fabricated values.");
            // Diagnostics: three independent domains; a few seconds of data must never read as Healthy.
            var d = vm.Diagnostics;
            Check(vm.HealthTiles.Count == 3 && vm.ConnectionHealth == "Insufficient Data" && vm.GraphicsHealth == "Insufficient Data" && vm.GuestHealth == "Insufficient Data",
                $"Short observation must be Insufficient Data: {vm.ConnectionHealth}/{vm.GraphicsHealth}/{vm.GuestHealth}.");
            Check(d.Hypotheses.Count > 0, "Diagnostic assessment statement missing.");
            Check(d.HeadlineState == "Insufficient Data" && !d.HasActiveProblems && d.Headline.StartsWith("No conclusion yet", StringComparison.Ordinal),
                "Short observation must not state a conclusion: " + d.Headline);
            Check(d.DataQuality.Level == "Insufficient evidence" && d.DataQuality.Rows.Count == 5, "Evidence coverage indicator missing: " + d.DataQuality.Level);
            Check(d.DataQuality.Rows.Any(r => r.Label == "Collector metrics with a value" && r.Value.Contains(" of ", StringComparison.Ordinal)),
                "Collector availability must be shown separately from diagnostic coverage.");
            Check(d.Domains.All(x => x.Measurements.Count > 0 && x.Details.Count > 0), "Domain measurements or coverage details missing.");
            Check(d.Domains.Single(x => x.Domain == "Connection Health").Measurements.Any(m => m.Label == "RTT P95" && m.Value == "Not available" && m.Threshold.Contains("150", StringComparison.Ordinal)),
                "Unavailable Blast RTT must read 'Not available' with its configured threshold, never 0.");
            Check(vm.GpuCards.Any(c => c.Label == "Graphics acceleration") || vm.GpuCards.Any(c => c.Label == "GPU model"), "GPU acceleration state missing.");
            if (vm.GpuCards.Any(c => c.Label == "Graphics acceleration"))
            {
                Check(d.Findings.Any(f => f.RuleId == "gpu.software-rendering" && f.Severity == "INFO" && f.State == "Informational" && f.Marker == "Neutral"),
                    "Software rendering must be a neutral information finding, not a warning.");
                Check(d.Domains.Single(x => x.Domain == "VDI Guest Health").Measurements.Any(m => m.Label == "GPU" && m.Value == "Software rendering" && m.Threshold == "Not applicable"),
                    "Guest health must state software rendering once, as not applicable.");
            }
            Check(!d.Findings.Any(f => f.IsProblem) && d.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems).Count == 0, "No warning may be raised from a few seconds of data.");
            Check(d.Trends.Count == 4 && d.Cpu.Points.Any(p => p.Value is not null) && d.Rtt.Points.All(p => p.Value is null) && !d.Rtt.HasData,
                "Diagnostic trends must show measured CPU and leave unavailable RTT as gaps.");
            vm.SelectedPage = vm.Pages.Single(p => p is DiagnosticsPage);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            d.SelectedTrendWindow = d.TrendWindows[2];
            Check(d.Cpu.XAxes[0].MaxLimit - d.Cpu.XAxes[0].MinLimit == TimeSpan.FromSeconds(120).Ticks && d.Rtt.XAxes[0].MinLimit == d.Cpu.XAxes[0].MinLimit,
                "Trend charts must share the selected 120 s window.");
            d.SelectedTrendWindow = d.TrendWindows[1];
            d.SelectedFilter = d.Filters.Single(f => f.Kind == FindingFilterKind.Information);
            Check(d.Findings.All(f => f.State == "Informational"), "Information filter failed.");
            d.SelectedFilter = d.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
            Check(d.IsListEmpty && d.ListEmptyTitle.Length > 0, "Active-problems filter must show an explained empty state.");
            d.SelectedFilter = d.Filters[0];
            if (d.Findings.Count > 0)
            {
                var chosen = d.Findings[^1];
                d.SelectedFinding = chosen;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(1200);
                Check(ReferenceEquals(d.SelectedFinding, chosen) && d.HasSelection && chosen.Evidence.Count > 0, "Selected finding or its evidence was lost after a sample update.");
                d.CopyFindingCommand.Execute(null);
                evidence.Add("Clipboard on runner: " + d.CopyStatus);
            }
            d.SortBy(FindingSortKey.Finding, false);
            Check(d.Findings.Select(f => f.Title).SequenceEqual(d.Findings.Select(f => f.Title).Order(StringComparer.CurrentCultureIgnoreCase)), "Sorting by finding failed.");
            d.SortBy(FindingSortKey.Severity, false);
            evidence.Add("Diagnostics on runner: " + string.Join("; ", d.Domains.Select(t => $"{t.Domain}={t.State} ({t.Coverage}; {t.Summary})")) +
                $" | status: {d.Headline} | evidence coverage: {d.DataQuality.Level}, {d.DataQuality.Summary} " +
                string.Join(", ", d.DataQuality.Rows.Select(r => $"{r.Label}={r.Value}")) +
                " | findings: " + string.Join(", ", d.Findings.Select(f => $"{f.RuleId}:{f.Severity}/{f.State}")) + " | assessment: " + string.Join(" / ", d.Hypotheses.Select(h => h.Statement)));
            Check(Points(vm.CpuSeries).Any(v => v is not null), "CPU history chart is empty.");

            // Advanced Metrics: raw rows, category filter and search.
            vm.SelectedPage = vm.Pages.Single(p => p is AdvancedPage);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(vm.AdvancedRows.Count > 10 && vm.AdvancedRows.Any(r => r.Id == "cpu.utilization" && r.Status == "Available"), "Advanced Metrics lacks real CPU rows.");
            Check(vm.AdvancedRows.Any(r => r.Category == "Horizon Blast Session") && vm.AdvancedRows.Any(r => r.Category == "Display"), "Advanced Metrics categories missing.");
            vm.SelectedAdvancedCategory = vm.AdvancedCategories.Single(c => c.Value == MetricCategory.WindowsMemory);
            Check(vm.AdvancedRows.Count > 0 && vm.AdvancedRows.All(r => r.Category == "Windows Memory"), "Advanced category filter failed.");
            vm.SelectedAdvancedCategory = vm.AdvancedCategories[0];
            vm.AdvancedSearch = "nvml";
            Check(vm.AdvancedRows.Any(r => r.Id == "nvml.status"), "NVML status row missing (provider must initialize without NVIDIA hardware).");
            evidence.Add("NVML on runner: " + vm.AdvancedRows.Single(r => r.Id == "nvml.status").Value);
            evidence.Add("GPU on runner: " + string.Join("; ", vm.GpuCards.Select(c => $"{c.Label}={c.Value}")));
            vm.AdvancedSearch = "";
            vm.SelectedPage = vm.Pages[0];
            await recorder.ExportJsonAsync(Path.Combine(directory, "real.json"));
            await recorder.ExportCsvAsync(Path.Combine(directory, "real.csv"));
            var realCsv = await File.ReadAllLinesAsync(Path.Combine(directory, "real.csv"));
            Check(realCsv[0].Contains("data_source", StringComparison.Ordinal) && realCsv[0].Contains("blast_rtt_status", StringComparison.Ordinal), "Real CSV header lacks Blast columns.");
            Check(realCsv.Skip(1).All(l => l.Split(',')[1] == "RealSession"), "Real CSV contains non-real rows.");
            using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "real.json"))))
                Check(json.RootElement.EnumerateArray().All(e => !e.GetProperty("Session").GetProperty("IsDemo").GetBoolean()
                    && e.GetProperty("BlastMetrics").GetProperty("rtt").GetProperty("Value").ValueKind == System.Text.Json.JsonValueKind.Null), "Real JSON contains demo or fabricated data.");
            Check(realCsv[0].Contains("host_cpu.utilization_status", StringComparison.Ordinal) && realCsv[0].Contains("protocol_detection", StringComparison.Ordinal), "Real CSV header lacks host columns.");
            Check(realCsv[0].EndsWith(",diag_connection,diag_graphics,diag_guest,diag_coverage,diag_active_findings,diag_events", StringComparison.Ordinal), "Real CSV lacks diagnostic columns.");
            using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "real.json"))))
                Check(json.RootElement.EnumerateArray().All(e => e.GetProperty("Diagnostics").GetProperty("Assessments").GetArrayLength() == 3), "Real JSON lacks diagnostic assessments.");
            evidence.Add($"Real Session: {realCsv.Length - 1} samples, Blast N/A, CPU {Card("CPU")}; status: {vm.Status}");

            // Demo Mode: synthetic values, separate recording, live charts populated.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
            Check(vm.ModeBanner.StartsWith("DEMO DATA", StringComparison.Ordinal), "Demo banner missing.");
            Check(vm.ProtocolDisplay.EndsWith("(demo selection)", StringComparison.Ordinal), "Demo protocol is not labelled as a demo selection.");
            vm.StartRecordingCommand.Execute(null);
            Check(recorder.IsRecording && recorder.SampleCount == 0, "New demo recording did not replace the real recording.");
            await Task.Delay(2200);
            Check(vm.OverviewCards.Any(c => c.Value != "N/A" && c.Value != "Unavailable"), "Demo Mode did not produce values.");
            Check(Points(vm.RttSeries).Any(v => v is not null) && Points(vm.FpsSeries).Any(v => v is not null), "Demo live charts are empty.");
            Check(Points(vm.RecordingSeries).Any(v => v is not null), "Recording chart is empty.");

            // Switching mode during a recording must stop it, so demo and real samples never mix.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.RealSession);
            Check(!recorder.IsRecording && vm.Status.Contains("Recording stopped", StringComparison.Ordinal), "Mode switch did not stop the recording.");
            Check(Points(vm.RttSeries).Length == 0 && vm.OverviewCards.Count == 0, "Mode switch did not clear live data.");
            Check(recorder.Samples.All(s => s.Session.IsDemo), "Recording mixed real and demo samples.");
            await recorder.ExportCsvAsync(Path.Combine(directory, "demo.csv"));
            var demoCsv = await File.ReadAllLinesAsync(Path.Combine(directory, "demo.csv"));
            Check(demoCsv.Length > 1 && demoCsv.Skip(1).All(l => l.Split(',')[1] == "Demo"), "Demo CSV contains non-demo rows.");
            evidence.Add($"Demo Mode: {demoCsv.Length - 1} samples; Real→Demo→Real switching stopped recording and cleared live data.");
            await Task.Delay(1500);
            Check(vm.OverviewCards.Single(c => c.Label == "RTT").Value == "N/A" && vm.ProtocolDisplay == "Not detected", "Real Session after switching back must not show demo values.");
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
        }
        finally { Directory.Delete(directory, true); }
        vm.StartRecordingCommand.Execute(null);
        await Task.Delay(2200);
        vm.StopRecordingCommand.Execute(null);
        if (vm.SampleCount < 1 || vm.HealthTiles.Count != 3) throw new InvalidOperationException("Sampling/diagnostics smoke check failed.");
        // Demo data drives every trend; select a finding so layout captures include the evidence panel.
        if (vm.Diagnostics.Findings.Count > 0) vm.Diagnostics.SelectedFinding = vm.Diagnostics.Findings[0];
        foreach (var page in vm.Pages)
        {
            vm.SelectedPage = page;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(350);
        }
        var soak = await DiagnosticsSoakAsync(vm);
        vm.IsDark = false;
        vm.SelectedPage = vm.Pages[0];
        await Task.Delay(500);
        vm.IsDark = true;
        await DesktopValidation.RunAsync(MainWindow, vm);
        directory = Path.Combine(Path.GetTempPath(), "EucLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await recorder.ExportJsonAsync(Path.Combine(directory, "demo.json"));
            await recorder.ExportCsvAsync(Path.Combine(directory, "demo.csv"));
            if (new FileInfo(Path.Combine(directory, "demo.json")).Length == 0) throw new IOException("Empty JSON export.");
        }
        finally { Directory.Delete(directory, true); }
        if (errors.Errors.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join("\n", errors.Errors));
        GC.Collect(); GC.WaitForPendingFinalizers();
        if (unhandled.Count > 0) throw new InvalidOperationException("Unhandled exceptions: " + string.Join("\n", unhandled));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), "PASS: startup, diagnostics (3 independent domains, Insufficient Data and no conclusion on short observation, evidence coverage vs collector availability, software rendering as neutral information, trends with gaps and a shared window, filters with counts and empty states, sorting, stable selection with evidence, clipboard, diag export columns), Real Session without Horizon Agent (Blast N/A, real CPU/RAM, recording + JSON/CSV with host columns), protocol display, Advanced Metrics filter/search, NVML-absent initialization, Real↔Demo switching, demo sampling and live charts, all six views, themes, recording, JSON/CSV export, themed charts, navigation/resize layout matrix, WPF client captures, no WPF binding errors. Actual Windows DPI changes require manual acceptance.\n" + soak + "\n" + string.Join("\n", evidence));
        await StopAsync();
        closing = true;
        Shutdown(0);
    }
    /// <summary>
    /// Demo high-latency scenario on the Diagnostics page: sustained findings must appear with evidence, the UI thread
    /// must stay responsive with four live charts, and repeated navigation must not grow the managed heap.
    /// </summary>
    private async Task<string> DiagnosticsSoakAsync(MainViewModel vm)
    {
        var d = vm.Diagnostics;
        vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
        vm.SelectedScenario = vm.Scenarios.Single(s => s.Value == DemoScenario.HighNetworkLatency);
        vm.SelectedPage = vm.Pages.Single(p => p is DiagnosticsPage);
        var latencies = new List<double>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90) && !(d.HasActiveProblems && d.Findings.Any(f => f.RuleId == "connection.packetloss.uplink")))
        {
            var probe = Stopwatch.StartNew();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            latencies.Add(probe.Elapsed.TotalMilliseconds);
            await Task.Delay(250);
        }
        var seconds = clock.Elapsed.TotalSeconds;
        if (!d.HasActiveProblems || d.HeadlineState != "Warning" && d.HeadlineState != "Critical")
            throw new InvalidOperationException($"High-latency demo raised no sustained finding after {seconds:0} s: {d.Headline}");
        var rtt = d.Findings.SingleOrDefault(f => f.RuleId == "connection.rtt.sustained") ?? throw new InvalidOperationException("Sustained RTT finding missing.");
        if (!rtt.IsProblem || !rtt.Evidence.Any(e => e.Measurement == "95th percentile (P95)" && e.Threshold.StartsWith("≥ 150 ms", StringComparison.Ordinal)) || rtt.Actions.Count == 0)
            throw new InvalidOperationException("RTT finding lacks P95 evidence with its configured threshold or recommended actions.");
        if (d.Domains[0].State is not ("Warning" or "Critical")) throw new InvalidOperationException("Connection Health did not reflect the RTT finding.");
        d.SelectedFilter = d.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
        if (d.Findings.Count == 0 || !d.Findings.All(f => f.IsProblem)) throw new InvalidOperationException("Active-problems filter failed on real findings.");
        d.SortBy(FindingSortKey.Severity, false);
        if (d.Findings.Zip(d.Findings.Skip(1)).Any(p => p.First.Card.Rank > p.Second.Card.Rank)) throw new InvalidOperationException("Default severity order violated.");
        d.SelectedFilter = d.Filters[0];
        d.SelectedFinding = rtt;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(1100);
        if (!ReferenceEquals(d.SelectedFinding, rtt)) throw new InvalidOperationException("User selection lost after a sample update.");
        if (!d.Rtt.HasData || d.Rtt.Sections.Length != 2) throw new InvalidOperationException("RTT trend or its threshold lines missing.");

        // Memory: charts are recreated on every visit; the managed heap must return to a stable size.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        var workingBefore = Process.GetCurrentProcess().WorkingSet64;
        for (var i = 0; i < 12; i++)
        {
            vm.SelectedPage = vm.Pages[0];
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            vm.SelectedPage = vm.Pages.Single(p => p is DiagnosticsPage);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = GC.GetTotalMemory(true);
        var workingAfter = Process.GetCurrentProcess().WorkingSet64;
        latencies.Sort();
        double Percentile(double p) => latencies[Math.Min(latencies.Count - 1, (int)(p * latencies.Count))];
        const double MiB = 1024 * 1024;
        var result = $"Diagnostics soak (Demo high latency, page open, 4 live charts): findings after {seconds:0} s ({string.Join(", ", d.Findings.Select(f => $"{f.RuleId}:{f.Severity}"))}); " +
            $"UI dispatcher latency over {latencies.Count} probes median {Percentile(.5):0.0} ms, P95 {Percentile(.95):0.0} ms, max {latencies[^1]:0.0} ms; " +
            $"12 navigation cycles: managed heap {before / MiB:0.0} → {after / MiB:0.0} MiB, working set {workingBefore / MiB:0} → {workingAfter / MiB:0} MiB.";
        if (Percentile(.5) > 200 || latencies[^1] > 2000) throw new InvalidOperationException("Diagnostics page is not responsive. " + result);
        if (after - before > 48 * MiB) throw new InvalidOperationException("Managed heap grew across Diagnostics navigation. " + result);
        if (d.Findings.Count > 0) d.SelectedFinding = d.Findings[0];
        return result;
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        var vm = services?.GetService<MainViewModel>();
        if (!smoke && vm?.HasUnexportedSamples == true && MessageBox.Show(MainWindow,
            "There are unexported recording samples. Exit and discard them? Choose No to return and export.",
            "Unsaved recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        closing = true;
        try { await StopAsync(); Shutdown(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Shutdown(1); }
    }
    private async Task StopAsync()
    {
        cancellation.Cancel();
        if (sampling is not null) await sampling;
        if (services is not null) { await services.DisposeAsync(); services = null; }
    }
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
