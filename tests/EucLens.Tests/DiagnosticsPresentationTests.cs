using System.Xml.Linq;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using EucLens.App.Presentation;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>Diagnostics page presentation: filtering, sorting, selection, evidence, coverage, empty states and trends.</summary>
public sealed class DiagnosticsPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => T0.AddTicks(ticks);
        public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private sealed class FakeDesktop : IDesktopServices
    {
        public string? Copied { get; private set; }
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public bool CopyText(string text) { Copied = text; return true; }
    }

    private static Metric M(double? value, string unit) => value is { } v
        ? new(v, unit, Source: "synthetic", SampleStatus: MetricStatus.Available)
        : new(null, unit, "Blast counters not found", "synthetic", SampleStatus: MetricStatus.Unavailable);

    private static SessionSnapshot Snap(DateTimeOffset at, double? rtt = 30, double? loss = 0, double? fps = 30, double? cpu = 20, double? ram = 40,
        bool? hardwareGpu = false, double? jitter = null, WorkloadContext workload = WorkloadContext.Idle, double? dirty = null)
    {
        var blast = new Dictionary<string, Metric> { ["rtt"] = M(rtt, "ms") };
        if (dirty is not null) blast["dirtyFps"] = M(dirty, "FPS");
        if (jitter is not null) { blast["jitter"] = M(jitter, "raw (unit unverified)"); blast["estimatedBandwidth"] = M(5000, "raw (unit unverified)"); }
        var host = new List<MetricEntry>
        {
            new("memory.availableGiB", MetricCategory.WindowsMemory, "Available", M(4, "GiB")),
            new("cpu.logicalProcessors", MetricCategory.WindowsCpu, "Logical processors", M(2, "count")),
            new("gpu.vendor", MetricCategory.Gpu, "Vendor", M(null, "")),
        };
        var na = M(null, "%");
        return new(at, new(RemoteProtocol.OmnissaBlast, 3, "user", "client", "vdi", "Windows 11", "N/A", at, false), workload,
            new(M(rtt, "ms"), M(null, "ms"), M(null, "Mbps"), M(1, "Mbps"), M(loss, "%")),
            new(M(fps, "FPS"), "Raw code 1", "N/A", M(null, "%"), "Blast", null, "1920 × 1080"),
            new(hardwareGpu == true ? "NVIDIA A16-2Q" : "None (software rendering)", na, na, na, na, "1.0", null,
                hardwareGpu == false ? "No hardware GPU: Microsoft Basic Render Driver ×2 (DXGI software adapter (Microsoft Basic Render Driver); CPU rendering.)" : "NVIDIA", hardwareGpu),
            new(M(cpu, "%"), M(ram, "%"), M(1, "MB/s")), BlastMetrics: blast, HostMetrics: host);
    }

    /// <summary>Feeds a real engine with a fake monotonic clock and applies every report to the presentation model.</summary>
    private sealed class Session
    {
        public TestClock Clock { get; } = new();
        public DiagnosticIntelligenceEngine Engine { get; }
        public FakeDesktop Desktop { get; } = new();
        public DiagnosticsViewModel Vm { get; }
        public Session(DiagnosticThresholds? t = null)
        {
            Engine = new(DiagnosticRuleSet.Default(), Clock, t);
            Vm = new(Desktop);
            if (t is not null) Vm.Configure(t);
        }
        public DiagnosticReport Run(int samples, Func<DateTimeOffset, SessionSnapshot> make, double interval = 1)
        {
            DiagnosticReport report = null!;
            for (var i = 0; i < samples; i++)
            {
                Clock.Advance(interval);
                var s = make(Clock.GetUtcNow());
                report = Engine.Evaluate(s);
                Vm.Apply(report, s);
            }
            return report;
        }
    }

    private static DiagnosticFinding Finding(string id, Severity severity, string title, int detectedSecond, int lastSecond, int? recoveredSecond = null,
        DiagnosticDomain domain = DiagnosticDomain.Connection) =>
        new(id, domain, severity, title, $"{title} description", T0.AddSeconds(detectedSecond), T0.AddSeconds(lastSecond),
            recoveredSecond is { } r ? T0.AddSeconds(r) : null, recoveredSecond is null ? null : "Recovered for test.", 60, 58, 60,
            [new("Window P95", "165 ms", null, "≥ 150 ms warning"), new("Window mean", "120 ms"), new("Observation", "60 s window · 58 valid of 60 samples")],
            "Warning when RTT ≥ 150 ms.", ["Cause A"], ["Check A.", "Check B."], "Limitation.");

    private static readonly DiagnosticFinding[] Mixed =
    [
        Finding("info.a", Severity.Info, "Zeta information", 1, 50),
        Finding("warn.a", Severity.Warning, "Beta warning", 10, 50),
        Finding("old.warn", Severity.Warning, "Alpha recovered", 2, 20, 30),
        Finding("crit.a", Severity.Critical, "Gamma critical", 20, 50),
    ];

    private static DiagnosticsViewModel WithFindings(IReadOnlyList<DiagnosticFinding> findings, FakeDesktop? desktop = null)
    {
        var vm = new DiagnosticsViewModel(desktop ?? new FakeDesktop());
        vm.Apply(new DiagnosticReport(HealthStatus.Critical, [], Assessments:
        [
            new(DiagnosticDomain.Connection, HealthState.Critical, AssessmentCoverage.Full, "", "", 60),
            new(DiagnosticDomain.Graphics, HealthState.Healthy, AssessmentCoverage.Full, "", "", 60),
            new(DiagnosticDomain.Guest, HealthState.Healthy, AssessmentCoverage.Full, "", "", 60),
        ], Findings: findings, Hypotheses: [], Inputs: []), Snap(T0));
        return vm;
    }

    // ---------- filtering ----------

    [Fact]
    public void FilterCountsPartitionFindingsAndInformationIsNotAProblem()
    {
        var vm = WithFindings(Mixed);
        int Count(FindingFilterKind k) => vm.Filters.Single(f => f.Kind == k).Count;
        Assert.Equal(4, Count(FindingFilterKind.All));
        Assert.Equal(2, Count(FindingFilterKind.ActiveProblems));
        Assert.Equal(1, Count(FindingFilterKind.Critical));
        Assert.Equal(1, Count(FindingFilterKind.Warning));
        Assert.Equal(1, Count(FindingFilterKind.Information));
        Assert.Equal(1, Count(FindingFilterKind.Recovered));
        Assert.True(vm.HasActiveProblems);
        Assert.Equal("2 active problems (warning or critical)", vm.Headline);
        Assert.Equal("Connection: Critical · Most severe: Gamma critical", vm.HeadlineDetail);
        Assert.Equal("Critical", vm.HeadlineState);

        vm.SelectedFilter = vm.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
        Assert.Equal(["crit.a", "warn.a"], vm.Findings.Select(f => f.RuleId));
        Assert.All(vm.Findings, f => Assert.True(f.IsProblem));
        vm.SelectedFilter = vm.Filters.Single(f => f.Kind == FindingFilterKind.Information);
        Assert.Equal("info.a", Assert.Single(vm.Findings).RuleId);
        Assert.Equal("Informational", vm.Findings[0].State);
        Assert.Equal("Neutral", vm.Findings[0].Marker);
        vm.SelectedFilter = vm.Filters.Single(f => f.Kind == FindingFilterKind.Recovered);
        var recovered = Assert.Single(vm.Findings);
        Assert.Equal("Recovered", recovered.State);
        Assert.Equal("Warning", recovered.SeverityLabel);
        Assert.Equal("Neutral", recovered.Marker);
    }

    // ---------- sorting ----------

    [Fact]
    public void DefaultOrderIsCriticalWarningRecoveredInformation()
    {
        var vm = WithFindings(Mixed);
        Assert.Equal(["crit.a", "warn.a", "old.warn", "info.a"], vm.Findings.Select(f => f.RuleId));
        Assert.Equal(FindingSortKey.Severity, vm.SortKey);
        Assert.Equal("▲", vm.Columns[0].Glyph);
    }

    [Fact]
    public void ColumnSortTogglesDirectionAndUpdatesGlyphs()
    {
        var vm = WithFindings(Mixed);
        var finding = vm.Columns.Single(c => c.Key == FindingSortKey.Finding);
        finding.SortCommand.Execute(null);
        Assert.Equal(["Alpha recovered", "Beta warning", "Gamma critical", "Zeta information"], vm.Findings.Select(f => f.Title));
        Assert.Equal("▲", finding.Glyph);
        Assert.Equal("", vm.Columns[0].Glyph);
        finding.SortCommand.Execute(null);
        Assert.Equal(["Zeta information", "Gamma critical", "Beta warning", "Alpha recovered"], vm.Findings.Select(f => f.Title));
        Assert.Contains("descending", finding.AutomationName);

        // Time columns start newest/longest first.
        vm.Columns.Single(c => c.Key == FindingSortKey.FirstDetected).SortCommand.Execute(null);
        Assert.Equal(["crit.a", "warn.a", "old.warn", "info.a"], vm.Findings.Select(f => f.RuleId));
        vm.SortBy(FindingSortKey.Duration, true);
        Assert.Equal("info.a", vm.Findings[0].RuleId); // 49 s, longest
        vm.SortBy(FindingSortKey.State, false);
        Assert.Equal(["crit.a", "warn.a", "old.warn", "info.a"], vm.Findings.Select(f => f.RuleId));
    }

    // ---------- selection and evidence ----------

    [Fact]
    public void SelectionFollowsTopFindingUntilTheUserChoosesAndSurvivesUpdates()
    {
        var vm = WithFindings(Mixed);
        Assert.Equal("crit.a", vm.SelectedFinding!.RuleId);
        var chosen = vm.Findings.Single(f => f.RuleId == "info.a");
        vm.SelectedFinding = chosen;
        Assert.True(vm.HasSelection);

        // A per-second update replaces values in place: same item, still selected.
        vm.Apply(new DiagnosticReport(HealthStatus.Critical, [], Findings: Mixed.Select(f => f with { LastConfirmedAt = f.LastConfirmedAt.AddSeconds(1) }).ToArray(), Inputs: []), Snap(T0.AddSeconds(1)));
        Assert.Same(chosen, vm.SelectedFinding);
        Assert.Equal("50 s", chosen.DurationLabel);

        // A selector clearing its selection while the item is still listed does not lose it.
        vm.SelectedFinding = null;
        Assert.Same(chosen, vm.SelectedFinding);

        // Filtering the selection away selects the first visible finding instead.
        vm.SelectedFilter = vm.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
        Assert.Equal("crit.a", vm.SelectedFinding!.RuleId);
    }

    [Fact]
    public void EvidenceTableShowsStatisticAndOnlyEngineDefinedThresholds()
    {
        var s = new Session();
        s.Run(60, at => Snap(at, rtt: 200));
        var item = s.Vm.Findings.Single(f => f.RuleId == DiagnosticRuleIds.Rtt);
        Assert.Same(item, s.Vm.SelectedFinding);
        var p95 = item.Evidence.Single(e => e.Measurement == "95th percentile (P95)");
        Assert.Equal("200 ms", p95.Observed);
        Assert.Equal("≥ 150 ms warning · ≥ 300 ms critical", p95.Threshold);
        Assert.Equal("", item.Evidence.Single(e => e.Measurement == "Mean over window").Threshold);
        Assert.Contains(item.Evidence, e => e.Measurement == "Latest sample" && e.Threshold.Length == 0);
        Assert.Contains(item.Evidence, e => e.Measurement.StartsWith("Time at or above 150", StringComparison.Ordinal) && e.Threshold == "≥ 70 % of covered time");
        Assert.DoesNotContain(item.Evidence, e => e.Measurement is "Observation" or "Source");
        Assert.Contains("60 s window", item.Observation);
        Assert.StartsWith("1.  ", item.Actions[0]);
        Assert.Equal(s.Engine.Evaluate(Snap(s.Clock.GetUtcNow(), rtt: 200)).Findings!.Single(f => f.RuleId == DiagnosticRuleIds.Rtt).RecommendedActions.Count, item.Actions.Count);
    }

    [Fact]
    public void PacketLossThresholdSitsOnTheMeanItIsJudgedBy()
    {
        var s = new Session();
        s.Run(60, at => Snap(at, loss: 2));
        var item = s.Vm.Findings.Single(f => f.RuleId == DiagnosticRuleIds.PacketLoss);
        Assert.Equal("≥ 1 % warning · ≥ 5 % critical", item.Evidence.Single(e => e.Measurement == "Mean over window").Threshold);
        Assert.Equal("", item.Evidence.Single(e => e.Measurement == "95th percentile (P95)").Threshold);
    }

    [Fact]
    public void CopyPutsEvidenceThresholdsAndOrderedActionsOnTheClipboard()
    {
        var desktop = new FakeDesktop();
        var vm = WithFindings(Mixed, desktop);
        vm.CopyFindingCommand.Execute(null);
        Assert.Contains("EUC Lens finding: Gamma critical", desktop.Copied);
        Assert.Contains("95th percentile (P95): 165 ms (threshold ≥ 150 ms warning)", desktop.Copied);
        Assert.Contains("1. Check A.", desktop.Copied);
        Assert.Contains("2. Check B.", desktop.Copied);
        Assert.Equal("Finding copied to the clipboard.", vm.CopyStatus);
        vm.CopySummaryCommand.Execute(null);
        Assert.Contains("2 active problems", desktop.Copied);
        // A later sample must not wipe the copy confirmation while the selection is unchanged.
        vm.Apply(new DiagnosticReport(HealthStatus.Critical, [], Findings: Mixed, Inputs: []), Snap(T0.AddSeconds(1)));
        Assert.Equal("Diagnostic summary copied to the clipboard.", vm.CopyStatus);
        Assert.Contains("[Critical] Gamma critical", desktop.Copied);
    }

    // ---------- health overview, coverage and empty states ----------

    [Fact]
    public void ShortObservationStatesNoConclusionAndInsufficientEvidence()
    {
        var s = new Session();
        s.Run(5, at => Snap(at));
        Assert.All(s.Vm.Domains, d => Assert.Equal("Insufficient Data", d.State));
        Assert.Equal("Insufficient Data", s.Vm.HeadlineState);
        Assert.StartsWith("No conclusion yet", s.Vm.Headline);
        Assert.Equal("Insufficient evidence", s.Vm.DataQuality.Level);
        Assert.StartsWith("0 of ", s.Vm.DataQuality.Rows.Single(r => r.Label == "Diagnostic inputs evaluated").Value);
        s.Vm.SelectedFilter = s.Vm.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
        Assert.True(s.Vm.IsListEmpty);
        Assert.Equal("No findings yet.", s.Vm.ListEmptyTitle);
    }

    [Fact]
    public void HealthySessionExplainsWhatWasEvaluated()
    {
        var s = new Session();
        s.Run(70, at => Snap(at, jitter: 3));
        Assert.All(s.Vm.Domains, d => Assert.Equal("Healthy", d.State));
        Assert.False(s.Vm.HasActiveProblems);
        Assert.Equal("No active performance warnings in the last 60 s", s.Vm.Headline);
        Assert.Equal("Healthy", s.Vm.HeadlineState);
        var connection = s.Vm.Domains.Single(d => d.DomainKey == DiagnosticDomain.Connection);
        Assert.Equal("Partial", connection.Coverage);
        Assert.Equal("No active warnings", connection.ActiveProblems);
        Assert.Contains(connection.Measurements, m => m is { Label: "RTT P95", Value: "30 ms", Threshold: "≥ 150 ms warning" });
        Assert.Contains(connection.Measurements, m => m is { Label: "Packet loss (uplink) mean", Value: "0 %" });
        Assert.Contains(connection.Details, d => d.StartsWith("Jitter and estimated bandwidth units unverified", StringComparison.Ordinal));
        // Low FPS is only judged for active workloads, so no FPS threshold is shown for Idle.
        Assert.Contains(s.Vm.Domains.Single(d => d.DomainKey == DiagnosticDomain.Graphics).Measurements, m => m is { Label: "FPS P50", Threshold: "" });

        Assert.Equal("Partial coverage", s.Vm.DataQuality.Level);
        Assert.Equal("2", s.Vm.DataQuality.Rows.Single(r => r.Label.StartsWith("Unverified units", StringComparison.Ordinal)).Value);
        // Collector availability (5 of 6 raw counters) is reported separately from diagnostic coverage.
        Assert.Equal("5 of 6", s.Vm.DataQuality.Rows.Single(r => r.Label == "Collector metrics with a value").Value);

        s.Vm.SelectedFilter = s.Vm.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems);
        Assert.True(s.Vm.IsListEmpty);
        Assert.Equal("No active performance warnings detected in the selected observation window.", s.Vm.ListEmptyTitle);
        Assert.Contains("Connection: RTT", s.Vm.ListEmptyDetail);
        s.Vm.SelectedFilter = s.Vm.Filters[0];
        Assert.True(s.Vm.ShowNoProblemsBanner); // information findings are listed, but nothing needs attention
    }

    [Fact]
    public void MissingBlastMetricsAreNotAvailableNeverZeroAndNotCalledHealthy()
    {
        var s = new Session();
        s.Run(70, at => Snap(at, rtt: null, loss: null, fps: null));
        var connection = s.Vm.Domains.Single(d => d.DomainKey == DiagnosticDomain.Connection);
        Assert.Equal("Insufficient Data", connection.State);
        Assert.Contains(connection.Measurements, m => m is { Label: "RTT P95", Value: "Not available" });
        Assert.StartsWith("Not assessed: RTT not available", connection.Summary);
        Assert.Equal("Healthy", s.Vm.Domains.Single(d => d.DomainKey == DiagnosticDomain.Guest).State);
        // Guest is fine, but Connection and Graphics could not be assessed: never a green overall statement.
        Assert.Equal("Insufficient Data", s.Vm.HeadlineState);
        Assert.Contains("Not assessed (insufficient data): Connection, Graphics", s.Vm.HeadlineDetail);
        Assert.All(s.Vm.Rtt.Points, p => Assert.Null(p.Value));
        Assert.False(s.Vm.Rtt.HasData);
        Assert.True(s.Vm.Rtt.ShowNoData);
        Assert.True(s.Vm.Cpu.HasData);
    }

    [Fact]
    public void SoftwareRenderingIsOneNeutralObservationWithDeduplicatedAdapterText()
    {
        var s = new Session();
        s.Run(3, at => Snap(at));
        var item = s.Vm.Findings.Single(f => f.RuleId == DiagnosticRuleIds.SoftwareRendering);
        Assert.Equal("Informational", item.State);
        Assert.False(item.IsProblem);
        Assert.Equal("Neutral", item.Marker);
        Assert.Contains(item.Evidence, e => e is { Measurement: "GPU diagnostics", Observed: "Not applicable" });
        Assert.Equal("Microsoft Basic Render Driver ×2 — CPU rendering", item.Evidence.Single(e => e.Measurement == "Adapter").Observed);
        Assert.Contains(s.Vm.Domains.Single(d => d.DomainKey == DiagnosticDomain.Guest).Measurements,
            m => m is { Label: "GPU", Value: "Software rendering", Threshold: "Not applicable" });
        Assert.Equal(0, s.Vm.Filters.Single(f => f.Kind == FindingFilterKind.ActiveProblems).Count);
    }

    [Theory]
    [InlineData("No hardware GPU: Microsoft Basic Render Driver ×2 (DXGI software adapter (Microsoft Basic Render Driver); CPU rendering.)", "Microsoft Basic Render Driver ×2 — CPU rendering")]
    [InlineData("No hardware GPU: Microsoft Basic Render Driver (DXGI software adapter (Microsoft Basic Render Driver); CPU rendering.); VMware SVGA 3D (VMware SVGA virtual display adapter; no physical GPU.)",
        "Microsoft Basic Render Driver — CPU rendering; VMware SVGA 3D (VMware SVGA virtual display adapter; no physical GPU.)")]
    [InlineData("No display adapters were enumerated.", "No display adapters were enumerated.")]
    [InlineData(null, "N/A")]
    public void AdapterTextIsDeduplicatedForPresentationOnly(string? input, string expected) => Assert.Equal(expected, GpuText.Adapters(input));

    [Fact]
    public void ClearRemovesEverythingFromThePreviousMode()
    {
        var vm = WithFindings(Mixed);
        vm.Clear();
        Assert.Empty(vm.Findings);
        Assert.Null(vm.SelectedFinding);
        Assert.All(vm.Domains, d => { Assert.Equal("Insufficient Data", d.State); Assert.Empty(d.Measurements); });
        Assert.All(vm.Trends, t => Assert.Empty(t.Points));
        Assert.All(vm.Filters, f => Assert.Equal(0, f.Count));
        Assert.StartsWith("No conclusion yet", vm.Headline);
    }

    // ---------- trends ----------

    [Fact]
    public void TrendsShareTheSelectedWindowAndDrawOnlyConfiguredThresholds()
    {
        var t = new DiagnosticThresholds { ShortWindowSeconds = 20, StandardWindowSeconds = 40, LongWindowSeconds = 90, RttWarningMs = 120, RttCriticalMs = 250, CpuWarningPercent = 85 };
        var s = new Session(t);
        Assert.Equal(["20 s", "40 s", "90 s"], s.Vm.TrendWindows.Select(w => w.Label));
        s.Run(100, at => Snap(at));
        s.Vm.SelectedTrendWindow = s.Vm.TrendWindows[0];
        var min = s.Vm.Rtt.Data.XMin;
        Assert.All(s.Vm.Trends, c => Assert.Equal(min, c.Data.XMin));
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, s.Vm.Cpu.Data.XMax - min);
        // History is bounded by the long window, not by the number of samples received.
        Assert.InRange(s.Vm.Cpu.Points.Count, 90, 93);
        Assert.Equal([120d, 250d], s.Vm.Rtt.Data.References.Select(x => x.Value));
        Assert.Equal(85, Assert.Single(s.Vm.Cpu.Data.References).Value);
        Assert.Equal("Warning ≥ 120 ms · Critical ≥ 250 ms", s.Vm.Rtt.Reference);
        // The value axis includes the warning line; the critical line is hidden while outside the plotted range.
        Assert.True(s.Vm.Rtt.Data.YMax >= 120);
        Assert.False(s.Vm.Rtt.Data.References.Last().Visible);

        // Reloading thresholds moves the existing lines in a chart view; its section objects (and paints) are kept.
        var view = new ChartObjects(s.Vm.Rtt.Data);
        var first = view.Sections[0];
        s.Vm.Configure(t with { RttWarningMs = 100 });
        Assert.Same(first, view.Sections[0]);
        Assert.Equal(100, ((RectangularSection)first).Yi);
    }

    [Fact]
    public void CollectionGapsStayGapsInEveryTrend()
    {
        var s = new Session();
        s.Run(10, at => Snap(at));
        s.Run(1, at => Snap(at), interval: 20);
        Assert.All(s.Vm.Trends, c => Assert.Contains(c.Points, p => p.Value is null));
        Assert.Equal(12, s.Vm.Cpu.Points.Count);
        Assert.Null(s.Vm.Cpu.Points[10].Value);
    }

    // ---------- view model integration and themes ----------

    [Fact]
    public async Task MainViewModelDrivesTheDiagnosticsPresentation()
    {
        await using var demo = new MockMetricsProvider();
        var vm = new MainViewModel(demo, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), new SessionRecorder(), new FakeDesktop(), NullLogger<MainViewModel>.Instance);
        await vm.InitializeAsync(default);
        using (var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500))) await vm.RunAsync(run.Token);
        Assert.Same(vm.Diagnostics.Domains, vm.HealthTiles);
        Assert.Equal(vm.ConnectionHealth, vm.Diagnostics.Domains[0].State);
        Assert.Contains(vm.Diagnostics.Cpu.Points, p => p.Value is not null);
        Assert.Contains(vm.Diagnostics.Rtt.Points, p => p.Value is not null);
        vm.SelectedScenario = vm.Scenarios[1];
        Assert.All(vm.Diagnostics.Trends, t => Assert.Empty(t.Points));
        Assert.Empty(vm.Diagnostics.Findings);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));

    [Fact]
    public void BothThemesDefineEveryDynamicResourceTheDiagnosticsPageUses()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> Keys(string file) => XDocument.Load(Path.Combine(Repo(), "src/EucLens.App/Themes", file)).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().ToHashSet();
        var light = Keys("Light.xaml");
        var dark = Keys("Dark.xaml");
        Assert.Equal(light.Order(), dark.Order());
        var used = new[] { "src/EucLens.App/Views/DiagnosticsView.xaml", "src/EucLens.App/Themes/Controls.xaml" }
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(Repo(), f)), @"\{DynamicResource (\w+)\}").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.Contains(key, light));
    }

    // ---------- 0.4.2: conditional thresholds, compact status, health rows ----------

    private static DomainHealthItem Tile(Session s, DiagnosticDomain d) => s.Vm.Domains.Single(x => x.DomainKey == d);

    [Fact]
    public void LowFpsThresholdIsShownAsApplicableOnlyWhenTheEngineVerifiesAnActiveWorkload()
    {
        // Idle desktop: conditional, hidden, and said to be hidden. Unconditional thresholds are unaffected.
        var idle = new Session();
        idle.Run(70, at => Snap(at, fps: 2));
        var line = Assert.Single(idle.Vm.Fps.Data.References);
        Assert.True(line.Conditional);
        Assert.False(line.Applies);
        Assert.False(line.Visible);
        Assert.Contains("conditional: applies during verified active graphics workload; line hidden", idle.Vm.Fps.Reference);
        Assert.All(idle.Vm.Rtt.Data.References, r => Assert.True(r.Applies));
        Assert.All(idle.Vm.Cpu.Data.References, r => Assert.True(r.Applies && !r.Conditional));

        // Selecting an active workload is not verification: without dirty frames the line stays hidden.
        var declared = new Session();
        declared.Run(70, at => Snap(at, fps: 30, workload: WorkloadContext.InteractiveGraphics));
        Assert.False(Assert.Single(declared.Vm.Fps.Data.References).Applies);
        var declaredRow = Tile(declared, DiagnosticDomain.Graphics).Measurements.Single(m => m.Label == "FPS P50");
        Assert.StartsWith("Low-FPS rule inactive", declaredRow.Condition);
        Assert.Equal("< 15 FPS warning", declaredRow.Threshold);

        // A static screen (dirty frames below the activity threshold) is not an active workload either.
        var staticScreen = new Session();
        staticScreen.Run(70, at => Snap(at, fps: 2, dirty: 1, workload: WorkloadContext.InteractiveGraphics));
        Assert.False(Assert.Single(staticScreen.Vm.Fps.Data.References).Applies);

        // Active workload with a changing screen confirmed by dirty frames: applies, drawn, and says so.
        var verified = new Session();
        var report = verified.Run(70, at => Snap(at, fps: 30, dirty: 25, workload: WorkloadContext.InteractiveGraphics));
        var active = Assert.Single(verified.Vm.Fps.Data.References);
        Assert.True(active.Applies && active.Visible);
        Assert.Contains("applies now (verified active graphics workload)", verified.Vm.Fps.Reference);
        Assert.StartsWith("Low-FPS rule active", Tile(verified, DiagnosticDomain.Graphics).Measurements.Single(m => m.Label == "FPS P50").Condition);
        // Applicability is metadata for FPS only; every other threshold is unconditional.
        Assert.All(report.Inputs!.Where(i => i.Key != DiagnosticMetrics.Fps), i => Assert.Null(i.ThresholdApplies));
        Assert.True(report.Inputs!.Single(i => i.Key == DiagnosticMetrics.Fps).ThresholdApplies);

        // Back to an idle workload: the next sample hides it again.
        verified.Run(1, at => Snap(at, fps: 30, dirty: 25, workload: WorkloadContext.Idle));
        Assert.False(Assert.Single(verified.Vm.Fps.Data.References).Applies);
        Assert.False(verified.Vm.Fps.Data.References[0].Visible);
    }

    [Fact]
    public void HiddenConditionalLineDoesNotStretchTheValueAxis()
    {
        var idle = new Session();
        idle.Run(70, at => Snap(at, fps: 2));
        Assert.True(idle.Vm.Fps.Data.YMax < 5); // 2 FPS × 1.15, not stretched to the 15 FPS line
        var verified = new Session();
        verified.Run(70, at => Snap(at, fps: 30, dirty: 25, workload: WorkloadContext.InteractiveGraphics));
        Assert.True(verified.Vm.Fps.Data.YMax > 30);
    }

    [Fact]
    public void HealthTilesKeepRequiredAndAvailableRowsAndExplainPartialCoverage()
    {
        var s = new Session();
        s.Run(70, at => Snap(at, jitter: 3));
        var connection = Tile(s, DiagnosticDomain.Connection);
        Assert.DoesNotContain(connection.Measurements, m => m.Label.StartsWith("Reconnects", StringComparison.Ordinal));
        Assert.Contains("reconnects unavailable", connection.CoverageReason);
        Assert.Contains("units unverified", connection.CoverageReason);
        // The sample-level detail of an unavailable input stays behind the expander.
        Assert.Contains(connection.Details, d => d.StartsWith("Reconnects:", StringComparison.Ordinal));
        var graphics = Tile(s, DiagnosticDomain.Graphics);
        Assert.DoesNotContain(graphics.Measurements, m => m.Label.StartsWith("Dirty FPS", StringComparison.Ordinal) || m.Label.StartsWith("Out queueing", StringComparison.Ordinal));
        Assert.Contains("dirty fps", graphics.CoverageReason);
        Assert.Contains("unavailable", graphics.CoverageReason);
        Assert.Contains(Tile(s, DiagnosticDomain.Guest).Measurements, m => m.Label == "CPU P95");
        Assert.Contains("disk active time unavailable", Tile(s, DiagnosticDomain.Guest).CoverageReason);
        Assert.All(s.Vm.Domains, d => Assert.Contains("Partial", d.AutomationName.Replace("Coverage Partial", "Partial", StringComparison.Ordinal)));

        // A required input that is missing stays visible as "Not available" (never silently hidden).
        var missing = new Session();
        missing.Run(70, at => Snap(at, rtt: null));
        Assert.Contains(Tile(missing, DiagnosticDomain.Connection).Measurements, m => m is { Label: "RTT P95", Value: "Not available" });
        Assert.Equal("required inputs have too few valid samples", Tile(missing, DiagnosticDomain.Connection).CoverageReason);
    }

    [Fact]
    public void StatusAreaCountsActiveFindingsByLevelAndSummarisesTheAssessment()
    {
        var vm = WithFindings(Mixed);
        Assert.Equal((1, 1, 1), (vm.CriticalCount, vm.WarningCount, vm.InfoCount)); // the recovered warning is not counted
        Assert.Equal("Critical 1 · Warning 1 · Information 1", vm.CountsLabel);
        Assert.Equal("Assessment", vm.AssessmentSummary);

        var s = new Session();
        s.Run(70, at => Snap(at, rtt: 200));
        Assert.StartsWith("Assessment — ", s.Vm.AssessmentSummary);
        Assert.Contains(s.Vm.Hypotheses[0].Statement, s.Vm.AssessmentSummary);
        Assert.True(s.Vm.CriticalCount + s.Vm.WarningCount >= 1);
        s.Vm.Clear();
        Assert.Equal("Critical 0 · Warning 0 · Information 0", s.Vm.CountsLabel);
        Assert.Equal("Assessment", s.Vm.AssessmentSummary);
    }

    [Fact]
    public void TrendChartsUseALargerLabelSizeThanTheDashboardDefault()
    {
        var s = new Session();
        Assert.All(s.Vm.Trends, t => Assert.Equal(TrendChart.LabelSize, t.Data.LabelSize));
        Assert.True(TrendChart.LabelSize > new ChartData("x", "ms", 10).LabelSize);
        var view = new ChartObjects(s.Vm.Rtt.Data);
        Assert.Equal(TrendChart.LabelSize, view.XAxes[0].TextSize);
        Assert.Equal(TrendChart.LabelSize, view.YAxes[0].TextSize);
        Assert.All(view.Sections.Cast<RectangularSection>(), x => Assert.Equal(TrendChart.LabelSize, x.LabelSize));
        var dashboard = new ChartObjects(new ChartData("RTT", "ms", 10));
        Assert.Equal(11, dashboard.XAxes[0].TextSize);
    }

    // ---------- static checks of the XAML (WPF itself is validated on Windows) ----------

    private static readonly System.Xml.Linq.XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static System.Xml.Linq.XDocument Xaml(string relative) => System.Xml.Linq.XDocument.Load(Path.Combine(Repo(), "src/EucLens.App", relative));

    [Fact]
    public void DiagnosticsXamlUsesTokensForTypographyAndResolvesEveryResource()
    {
        var files = new[] { "Views/DiagnosticsView.xaml", "Views/TrendTileView.xaml" };
        string[] sources = ["Themes/Foundations.xaml", "Themes/Controls.xaml", "Themes/Light.xaml", "App.xaml", .. files];
        var keys = sources
            .SelectMany(f => Xaml(f).Descendants().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>()).ToHashSet();
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App", file));
            var missing = System.Text.RegularExpressions.Regex.Matches(text, @"\{StaticResource (\w+)\}").Select(m => m.Groups[1].Value).Where(k => !keys.Contains(k)).Distinct().ToArray();
            Assert.True(missing.Length == 0, $"{file} uses undefined resources: {string.Join(", ", missing)}");
            // Font sizes come from the shared typography tokens, never from numbers scattered through the view.
            Assert.DoesNotMatch(@"FontSize=""\d", text);
        }
        var tokens = Xaml("Themes/Foundations.xaml").Descendants().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().ToHashSet();
        Assert.All(new[] { "ReadingFontSize", "ValueFontSize", "StateFontSize", "MetaFontSize", "TooltipFontSize", "TrendChartHeight", "FindingsRegionReserve", "FindingsRegionMinHeight" }, k => Assert.Contains(k, tokens));
        // Taller trend plots than the 0.4.1 compact charts.
        var height = (string)Xaml("Themes/Foundations.xaml").Descendants().Single(e => (string?)e.Attribute(X + "Key") == "TrendChartHeight");
        Assert.True(double.Parse(height, System.Globalization.CultureInfo.InvariantCulture) >= 180);
    }

    [Fact]
    public void FindingsAndEvidenceAreKeyboardReachableAndScrollIndependently()
    {
        var all = Xaml("Views/DiagnosticsView.xaml").Descendants().ToArray();
        System.Xml.Linq.XElement Named(string name) => all.Single(e => (string?)e.Attribute(X + "Name") == name);
        string Attr(System.Xml.Linq.XElement e, string local) => e.Attributes().Single(a => a.Name.LocalName == local).Value;
        var evidence = Named("EvidenceScroll");
        Assert.Equal("True", Attr(evidence, "Focusable"));
        Assert.Equal("True", Attr(evidence, "IsTabStop"));
        Assert.Equal("True", Attr(evidence, "WheelChaining.Enabled"));
        Assert.Equal("True", Attr(Named("FindingsScroll"), "WheelChaining.Enabled"));
        Assert.Equal("{StaticResource SplitterThumb}", Attr(Named("FindingsSplitter"), "Style"));
        // Only the two panes scroll on their own, and the page scrolls around them.
        Assert.Equal(["EvidenceScroll", "FindingsScroll"], all.Where(e => e.Name.LocalName == "ScrollViewer").Select(e => (string?)e.Attribute(X + "Name")).OfType<string>().Order());
        // The region fills the visible page height and is a split with exactly master, splitter and detail.
        var region = Named("FindingsRegion");
        Assert.Equal(3, region.Elements().Count());
        // Its height follows the visible page through an explicit subscription that is removed on unload (no long-lived reference to the view).
        var code = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views/DiagnosticsView.xaml.cs"));
        Assert.Contains("page.ScrollChanged += OnPageScrollChanged", code);
        Assert.Contains("page.ScrollChanged -= OnPageScrollChanged", code);
        Assert.Contains("Unloaded += OnUnloaded", code);
    }

    [Fact]
    public void BothThemesDefineEveryDynamicResourceTheTrendTileUses()
    {
        var light = Xaml("Themes/Light.xaml").Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().ToHashSet();
        var dark = Xaml("Themes/Dark.xaml").Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().ToHashSet();
        var used = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views/TrendTileView.xaml")), @"\{DynamicResource (\w+)\}")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(used);
        Assert.All(used, k => { Assert.Contains(k, light); Assert.Contains(k, dark); });
    }

    // ---------- smoke-test harness guarantees (source-level; the harness itself runs on Windows) ----------

    [Fact]
    public void SmokeHarnessChecksKeyboardAccessPerElementWithoutRequiringAListBoxTabStop()
    {
        var code = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/DesktopValidation.cs"));
        // WPF: a ListBox is not a tab stop (its rows are); requiring it would be wrong.
        Assert.DoesNotContain("list.IsTabStop", code);
        Assert.DoesNotContain("cannot be reached with the keyboard", code);
        foreach (var named in new[] { "Evidence panel:", "Splitter:", "Findings list:" })
            Assert.Contains(named, code);
        // Rows, navigation modes, real focus movement, arrow-key selection and the empty state are all exercised.
        foreach (var fragment in new[] { "ListBoxItem", "KeyboardNavigationMode.Once", "KeyboardNavigationMode.Contained", "MoveFocus(new TraversalRequest", "Keyboard.Focus(", "Key.Down", "CheckEmptyFindingsAsync", "real focus movement NOT verified" })
            Assert.Contains(fragment, code);
    }

    [Fact]
    public void SoakMeasurementsArePersistedBeforeAnyThresholdOrLaterValidation()
    {
        var code = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/App.xaml.cs"));
        var persist = code.IndexOf("await File.WriteAllTextAsync(SoakResultPath()", StringComparison.Ordinal);
        Assert.True(persist > 0, "The soak result is not written to its own file.");
        foreach (var later in new[] { "Diagnostics page is not responsive", "Managed heap grew across Diagnostics navigation", "Managed heap keeps growing per Diagnostics visit", "DesktopValidation.CheckCharts(MainWindow" })
            Assert.True(code.IndexOf(later, StringComparison.Ordinal) > persist, $"'{later}' runs before the soak measurements are persisted.");
        Assert.Contains("After {(b + 1) * 12} cycles", code); // one line per 12-cycle batch: managed heap and working set
        // The thresholds themselves are unchanged.
        foreach (var limit in new[] { "48 * MiB", "8 * MiB", "Percentile(.5) > 200", "latencies[^1] > 2000" })
            Assert.Contains(limit, code);
    }

    [Fact]
    public void CiAlwaysUploadsTheFailureEvidence()
    {
        var workflow = File.ReadAllText(Path.Combine(Repo(), ".github/workflows/windows.yml"));
        var step = workflow[workflow.IndexOf("Upload test and smoke evidence", StringComparison.Ordinal)..];
        step = step[..step.IndexOf("Upload portable application", StringComparison.Ordinal)];
        Assert.Contains("if: always()", step);
        foreach (var path in new[] { "smoke-result.txt", "smoke-progress.txt", "visual-evidence/", "TestResults/*.trx" })
            Assert.Contains(path, step);
        // A failing layout visit keeps its own screenshot and state; a failing run keeps the evidence gathered so far.
        var validation = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/DesktopValidation.cs"));
        Assert.Contains("FAILED-", validation);
        Assert.Contains("evidence gathered before the failure", File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/App.xaml.cs")));
    }

    [Fact]
    public void ClippingCheckMeasuresACloneWithTheSameTypographyAndKeepsItsTolerance()
    {
        var code = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/DesktopValidation.cs"));
        // The natural width must come from a TextBlock clone (same font, OpenType features such as tabular numerals, formatting, DPI).
        Assert.DoesNotContain("new FormattedText(", code);
        foreach (var fragment in new[] { "NaturalWidth(text)", "typeof(Typography)", "SetRootDpi(clone", "SetTextFormattingMode(clone", "clone.Measure(" })
            Assert.Contains(fragment, code);
        // The assertion itself is as strict as before: 1 DIP tolerance, no suppression.
        Assert.Contains("natural > text.ActualWidth + 1", code);
        // The state dump reports the page host's viewport, not an arbitrary scroll viewer.
        Assert.Contains("PageHost(window)", code);
        Assert.DoesNotContain("Descendants<ScrollViewer>(window).FirstOrDefault();", code);
    }
}
