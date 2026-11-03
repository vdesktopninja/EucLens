using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Providers;
using System.Text.Json;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The findings experience of Session History: filters and counts, evidence detail, navigation to the real interval and chart, correlation presentation, data quality,
/// the Unknown-protocol state, and the lifetime of everything (stale results, release, repeated use). Recordings go through the real writer and loader.
/// Runs alone, like the other memory measurements: the heap-growth test reads the heap of the whole process, which other test classes allocate into while it runs.
/// </summary>
[Collection(SerialPerformanceCollection.Name)]
public sealed class HistoryFindingsPresentationTests
{
    private static readonly string Dir = HistoryFixture.Dir;
    private static DateTimeOffset At(double seconds) => HistoryFixture.T0.AddSeconds(seconds);

    internal sealed class Desktop : IDesktopServices
    {
        public string? ExportPath { get; set; }
        public int Asked { get; private set; }
        public string? SelectExportPath(bool json) { Asked++; return ExportPath; }
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public string? SelectFolder(string? initialDirectory) => null;
        public bool OpenPath(string path, bool selectFile = false) => true;
    }

    internal static SessionHistoryViewModel CreateHistory(MemoryStorage storage, TimeZoneInfo? zone = null, Desktop? desktop = null)
    {
        desktop ??= new Desktop(); var recorder = new SessionRecorder();
        var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop, storage: storage);
        return new(background, desktop, recorder, storage, zone ?? TimeZoneInfo.Utc);
    }

    internal static RecordingSummary Summary(MemoryStorage storage, string shortId) => RecordingCatalog.Scan(storage, Dir).Single(s => s.ShortId == shortId);

    internal static SessionSnapshot S(int i, double? rtt = 30, double loss = 0, double fps = 30, double cpu = 20, double dirty = 20, bool unknownProtocol = false)
    {
        var s = HistoryFixture.Sample(i, rtt: rtt is { } r ? r + i % 5 : null, loss: loss, fps: fps + i % 3, cpu: cpu + i % 4);
        var blast = new Dictionary<string, Metric>(s.BlastMetrics ?? new Dictionary<string, Metric>()) { ["dirtyFps"] = Metric.Available(dirty + i % 3, "FPS") };
        s = s with { BlastMetrics = blast };
        return unknownProtocol ? s with { Session = s.Session with { Protocol = RemoteProtocol.Unknown, ProtocolDetection = "Not detected: no Horizon Blast counters for this Windows session" } } : s;
    }

    /// <summary>1,800 samples: an RTT episode with packet loss (peak 7.1 % at second 360), a CPU spike and a frame-rate drop during activity. Four observations, one correlation.</summary>
    internal static SessionSnapshot Busy(int i) =>
        S(i, rtt: i is >= 300 and < 420 ? 210 : 30, loss: i == 360 ? 7.1 : i is >= 330 and < 400 ? 2 : 0, cpu: i is >= 900 and < 940 ? 97 : 20, fps: i is >= 1200 and < 1280 ? 6 : 30);

    private static async Task<(SessionHistoryViewModel History, MemoryStorage Storage)> OpenAsync(Func<int, SessionSnapshot> make, int count = 1800, TimeZoneInfo? zone = null,
        Func<int, bool>? skip = null, Func<int, HistoryFixture.RecordingContext, Task>? step = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, step, skipSample: skip);
        var history = CreateHistory(rec.Storage, zone);
        await history.OpenAsync(Summary(rec.Storage, rec.ShortId));
        await history.Settled;
        return (history, rec.Storage);
    }

    internal static HistoryObservationItem Row(SessionHistoryViewModel h, string ruleId) => h.Findings.Observations.Single(o => o.Observation.RuleId == ruleId);
    private static void SameTime(long ticks, double? x) => Assert.InRange(Math.Abs(ticks - x!.Value), 0, 1_000);

    // ---------- filters and summary ----------

    [Fact]
    public async Task TheSummaryAndTheSeverityFiltersAreComputedFromTheActualAnalysis()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        Assert.Equal(4, f.Observations.Count);
        Assert.Equal("4 observations · 3 warnings · 1 informational · 1 performance correlation", f.Summary);
        Assert.Equal(["All (4)", "Critical (0)", "Warning (3)", "Info (1)"], f.SeverityFilters.Select(c => c.Label));
        Assert.Equal(4, f.VisibleObservations.Count);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Warning);
        Assert.Equal(3, f.VisibleObservations.Count); Assert.All(f.VisibleObservations, o => Assert.Equal(Severity.Warning, o.Observation.Severity));
        Assert.Equal("Showing 3 of 4 findings", f.FilterNote);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Critical);
        Assert.Empty(f.VisibleObservations); Assert.Equal("No finding matches these filters.", f.EmptyNote);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Info);
        Assert.Equal("guest.cpu.spike", Assert.Single(f.VisibleObservations).Observation.RuleId);
        f.SelectedSeverity = f.SeverityFilters[0];
        Assert.Equal(4, f.VisibleObservations.Count); Assert.Equal("", f.FilterNote);
    }

    [Fact]
    public async Task TheAreaFiltersShowOnlyTheirDomainAndCountEachOne()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        Assert.Equal(["All areas (4)", "Connection (2)", "Graphics (1)", "VDI Guest (1)"], f.DomainFilters.Select(c => c.Label));
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Connection);
        Assert.Equal(["conn.loss.sustained", "conn.rtt.high"], f.VisibleObservations.Select(o => o.Observation.RuleId).Order(StringComparer.Ordinal));
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Graphics);
        Assert.Equal("gfx.fps.low", Assert.Single(f.VisibleObservations).Observation.RuleId);
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Guest);
        Assert.Equal("guest.cpu.spike", Assert.Single(f.VisibleObservations).Observation.RuleId);
        // the two filters combine
        f.SelectedDomain = f.DomainFilters[0]; f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Warning);
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Guest);
        Assert.Empty(f.VisibleObservations);
    }

    [Fact]
    public async Task RowsShowTheMeasuredValueTheUnitTheTimeAndTheDuration()
    {
        var (h, _) = await OpenAsync(Busy);
        var loss = Row(h, "conn.loss.sustained");
        Assert.Equal("Warning", loss.SeverityLabel); Assert.Equal("Sustained packet loss", loss.Title);
        Assert.StartsWith("Packet loss: P95 2", loss.Measurement);
        Assert.Contains("max 7.1%", loss.Measurement);                                  // the recorded peak, with the recorded unit
        Assert.Equal("08:05:30–08:06:39", loss.Time);                                      // the recording starts at 08:00 UTC; the zone of the page here is UTC
        Assert.Equal("1 min 09 s", loss.Duration);
        var fps = Row(h, "gfx.fps.low");
        Assert.Contains("lowest 6 FPS", fps.Measurement);                                // lower is worse: the minimum, not the maximum
        Assert.Equal("", loss.StatusBadge);
        Assert.False(loss.HasStatusBadge);
        Assert.Contains("Packet loss", loss.AutomationName); Assert.Contains("1 min 09 s", loss.AutomationName);
    }

    [Fact]
    public async Task EveryObservationIsKeptInTheSeverityOrderThenTimeOrder()
    {
        var (h, _) = await OpenAsync(Busy);
        var order = h.Findings.Observations.Select(o => (o.Observation.Severity, o.Observation.Start)).ToArray();
        Assert.Equal(order.OrderBy(o => o.Severity == Severity.Critical ? 0 : o.Severity == Severity.Warning ? 1 : 2).ThenBy(o => o.Start), order);
    }

    // ---------- evidence detail ----------

    [Fact]
    public async Task TheDetailKeepsTheOriginalExplanationAndShowsTheKeyValuesWithoutExpanding()
    {
        var (h, _) = await OpenAsync(Busy);
        var o = Row(h, "conn.loss.sustained");
        var d = o.Detail;
        Assert.Equal(o.Observation.Explanation, d.Explanation);                          // the deterministic text, unchanged
        Assert.Contains("does not establish the underlying cause", d.Explanation);
        Assert.StartsWith("Highest 7.1% at 08:06:00", d.KeyValues);
        Assert.Contains("matching of", d.KeyValues); Assert.Contains("coverage 100.0%", d.KeyValues);
        Assert.Equal("UTC 08:05:30–08:06:39", d.Utc);
        var metric = Assert.Single(d.Metrics);
        Assert.Equal("Packet loss", metric.Metric); Assert.Equal("%", metric.Unit); Assert.Equal("7.1", metric.Maximum); Assert.Equal("08:06:00", metric.WorstAt);
        Assert.Contains("Criterion: at or above 1 %", d.Threshold[0]); Assert.Contains("Basis: a fixed threshold.", d.Threshold);
        Assert.Contains(d.Limitations, l => l.Contains("uplink"));                       // limitations are carried, not dropped
        Assert.StartsWith("Ended: valid values were back within", d.Ending);
        Assert.True(d.HasLimitations);
    }

    [Fact]
    public async Task ABaselineThresholdAndItsReliabilityAreShownInTheEvidence()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 600 and < 604 ? 400 : 30));
        var spike = Row(h, "conn.rtt.deviation");
        var lines = spike.Detail.Threshold;
        Assert.Contains(lines, l => l.StartsWith("Basis: a fixed threshold together with deviation", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Session baseline: median", StringComparison.Ordinal) && l.Contains("valid samples over"));
    }

    // ---------- navigation ----------

    [Fact]
    public async Task SelectingPacketLossShowsItsRealUtcIntervalWithContextOnBothSides()
    {
        var (h, _) = await OpenAsync(Busy);
        var loss = Row(h, "conn.loss.sustained");
        h.Findings.SelectedObservation = loss;
        await h.Settled;
        var o = loss.Observation;
        Assert.Equal(At(330), o.Start); Assert.Equal(At(399), o.End);                    // recorded UTC instants
        var pad = TimeSpan.FromSeconds(34.5);                                            // half the 69 s finding: between 15 s and the 2 min context
        Assert.Equal(o.Start - pad, h.ViewStart); Assert.Equal(o.End + pad, h.ViewEnd);
        Assert.Equal(TimeSpan.Zero, h.ViewStart.Offset);
        Assert.Same(loss, h.Findings.SelectedObservation); Assert.True(h.Findings.HasSelection);
    }

    [Fact]
    public async Task TheSelectedTimeIsUtcInternallyAndOnlyThePresentationUsesTheZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(1), "Test", "Test standard", "Test summer", [rule]);   // +02:00 on 23 October 2026
        var rec = await HistoryFixture.RecordAsync(1800, Busy);
        var utc = CreateHistory(rec.Storage); var local = CreateHistory(rec.Storage, zone);
        foreach (var h in new[] { utc, local }) { await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled; h.Findings.SelectedObservation = Row(h, "conn.loss.sustained"); await h.Settled; }
        Assert.Equal(utc.ViewStart, local.ViewStart); Assert.Equal(utc.ViewEnd, local.ViewEnd);   // same instants whatever the zone
        Assert.Equal(utc.Findings.PeakAt, local.Findings.PeakAt);
        Assert.Equal("08:05:30–08:06:39", Row(utc, "conn.loss.sustained").Time);
        Assert.Equal("10:05:30–10:06:39", Row(local, "conn.loss.sustained").Time);                // +2 h: DST, shown in local time
        Assert.Equal("UTC 08:05:30–08:06:39", Row(local, "conn.loss.sustained").Detail.Utc);       // the UTC line is always there
    }

    [Fact]
    public async Task TheChartThatCarriesTheEvidenceIsEmphasizedAndTheOthersAreNot()
    {
        var (h, _) = await OpenAsync(Busy);
        h.Findings.SelectedObservation = Row(h, "conn.loss.sustained");
        Assert.Equal(["packetLoss"], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id));
        Assert.DoesNotContain(h.Charts, c => c.IsSupporting);
        Assert.Equal("Evidence for the selected finding", h.Charts.Single(c => c.IsPrimary).EmphasisLabel);
        Assert.Contains("Evidence for the selected finding", h.Charts.Single(c => c.IsPrimary).AutomationName);
        Assert.True(h.HasEmphasis);
        h.Findings.SelectedObservation = Row(h, "gfx.fps.low");                           // another finding moves the emphasis; it never accumulates
        Assert.Equal(["fps"], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id));
        Assert.Equal(["dirtyFps"], h.Charts.Where(c => c.IsSupporting).Select(c => c.Id));  // the activity measurement that made the frame rate meaningful
        Assert.All(h.Charts.Where(c => !c.IsEmphasized), c => Assert.Equal("", c.EmphasisLabel));
    }

    [Fact]
    public async Task WorstAtIsTheActualRecordedExtremeAndTheChartsReadItOut()
    {
        var (h, _) = await OpenAsync(Busy);
        var loss = Row(h, "conn.loss.sustained");
        h.Findings.SelectedObservation = loss;
        await h.Settled;
        var worst = loss.Observation.Primary.WorstAt;
        var recorded = h.Loaded!.Extreme("packetLoss", loss.Observation.Start, loss.Observation.End, highest: true)!.Value;
        Assert.Equal(recorded.At, worst); Assert.Equal(7.1, recorded.Value); Assert.Equal(At(360), worst);
        Assert.Equal(worst, h.Findings.PeakAt);
        Assert.Equal(worst, h.HoverAt);
        Assert.Contains("7.1", h.Charts.Single(c => c.Id == "packetLoss").Readout);      // the readout is the recorded value at that time
        // a lower-is-worse metric points at its lowest value
        var fps = Row(h, "gfx.fps.low");
        var lowest = h.Loaded.Extreme("fps", fps.Observation.Start, fps.Observation.End, highest: false)!.Value;
        Assert.Equal(lowest.At, fps.Observation.Primary.WorstAt); Assert.Equal(fps.Observation.Primary.Minimum, lowest.Value);
    }

    [Fact]
    public async Task EveryChartKeepsTheSameVisibleRangeAfterNavigatingToAFinding()
    {
        var (h, _) = await OpenAsync(Busy);
        foreach (var rule in new[] { "conn.rtt.high", "guest.cpu.spike", "gfx.fps.low" })
        {
            h.Findings.SelectedObservation = Row(h, rule);
            await h.Settled;
            Assert.All(h.Charts, c => { SameTime(h.ViewStart.UtcTicks, c.Data.XMin); SameTime(h.ViewEnd.UtcTicks, c.Data.XMax); });
        }
    }

    [Fact]
    public async Task AnObservationNearTheStartOfTheRecordingStaysInsideTheSession()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i < 60 ? 220 : 30), count: 900);
        h.Findings.SelectedObservation = Row(h, "conn.rtt.high");
        Assert.True(h.ViewStart >= h.SessionStart); Assert.True(h.ViewEnd <= h.SessionEnd);
        Assert.True(h.ViewStart <= h.Findings.SelectedObservation!.Observation.Start && h.ViewEnd >= h.Findings.SelectedObservation.Observation.End);
    }

    [Fact]
    public async Task ShowingTheFullRecordingClearsTheSelectionTheEmphasisAndTheRange()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        Assert.False(f.ShowFullRecordingCommand.CanExecute(null));
        f.SelectedObservation = Row(h, "conn.rtt.high");
        Assert.True(f.ShowFullRecordingCommand.CanExecute(null));
        Assert.NotEqual(h.SessionStart, h.ViewStart);
        f.ShowFullRecordingCommand.Execute(null);
        await h.Settled;
        Assert.Null(f.SelectedObservation); Assert.Null(f.HighlightStart); Assert.Null(f.PeakAt); Assert.False(f.HasSelection);
        Assert.False(h.HasEmphasis);
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.False(f.ShowFullRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFilterThatHidesTheSelectedFindingClearsItsSelectionAndEmphasis()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        f.SelectedObservation = Row(h, "conn.rtt.high");
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Info);
        Assert.Null(f.SelectedObservation); Assert.False(h.HasEmphasis); Assert.Null(f.HighlightStart);
    }

    [Fact]
    public async Task AFilterThatKeepsTheSelectedFindingSelectsItAgainInTheList()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        var row = Row(h, "conn.rtt.high");
        f.SelectedObservation = row;
        var raised = 0;
        f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryFindingsViewModel.SelectedObservation)) raised++; };
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Connection);       // the list replaces its items and would lose the selection
        Assert.Same(row, f.SelectedObservation); Assert.True(raised >= 1); Assert.True(h.HasEmphasis);
    }

    [Fact]
    public async Task TheShowInChartsCommandAsksThePageForTheEmphasizedChart()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        Assert.False(f.ShowInChartsCommand.CanExecute(null));
        f.SelectedObservation = Row(h, "conn.rtt.high");
        Assert.True(f.ShowInChartsCommand.CanExecute(null));
        var requested = new List<int>();
        f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryFindingsViewModel.ChartsRequest)) requested.Add(f.ChartsRequest); };
        f.ShowInChartsCommand.Execute(null); f.ShowInChartsCommand.Execute(null);
        Assert.Equal([1, 2], requested);
    }

    // ---------- correlations ----------

    [Fact]
    public async Task ACorrelationWithACommonOverlapIsNavigatedAndHighlightedWithThatOverlapOnly()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        var item = Assert.Single(f.PerformanceCorrelations);
        Assert.Equal(CorrelationTiming.CommonOverlap, item.Correlation.Timing);
        Assert.Equal("Round-trip time + Packet loss", item.Summary);
        f.SelectedCorrelation = item;
        await h.Settled;
        Assert.Equal(item.Correlation.CommonOverlapStart, f.HighlightStart); Assert.Equal(item.Correlation.CommonOverlapEnd, f.HighlightEnd);
        Assert.Equal(At(330), f.HighlightStart); Assert.Equal(At(399), f.HighlightEnd);
        Assert.Null(f.PeakAt);
        Assert.Equal(["packetLoss", "rtt"], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.True(h.ViewStart < At(330) && h.ViewEnd > At(399));
        Assert.Same(item, f.ActiveCorrelation); Assert.Null(f.SelectedObservation); Assert.Equal(2, item.Components.Count);
        Assert.StartsWith("Present together", item.Timing);
    }

    [Fact]
    public async Task AChainedCorrelationNeverClaimsACommonIntervalAndSaysSo()
    {
        // RTT episode A (300-359), loss (350-469), RTT episode C (460-519): A overlaps the loss, the loss overlaps C, A and C never overlap
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 300 and < 360 or >= 460 and < 520 ? 210 : 30, loss: i is >= 350 and < 470 ? 3 : 0));
        var f = h.Findings;
        var chained = Assert.Single(f.PerformanceCorrelations, c => c.Correlation.Timing == CorrelationTiming.Chained);
        Assert.Equal(3, chained.Components.Count);
        Assert.Equal("Linked in a chain: no single time interval contains every component", chained.Timing);
        Assert.DoesNotContain("together", chained.Timing);
        Assert.Contains("No instant is common to all 3 components", chained.Explanation);
        Assert.Equal(2, chained.Links.Count); Assert.All(chained.Links, l => Assert.Contains("overlap", l));
        f.SelectedCorrelation = chained;
        await h.Settled;
        Assert.Null(f.HighlightStart); Assert.Null(f.HighlightEnd);                       // nothing is shaded as shared
        Assert.True(h.ViewStart < At(300) && h.ViewEnd > At(519));                        // the real span of everything linked
        Assert.Equal(["packetLoss", "rtt"], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATolerancePairIsPresentedAsCloseNotOverlapping()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 300 and < 360 ? 210 : 30, loss: i is >= 362 and < 400 ? 3 : 0));
        var pair = Assert.Single(h.Findings.PerformanceCorrelations);
        Assert.Equal(CorrelationTiming.WithinTolerance, pair.Correlation.Timing);
        Assert.Equal("Close together in time, not overlapping", pair.Timing);
        Assert.Contains("3 s apart, no overlap", Assert.Single(pair.Links));
        h.Findings.SelectedCorrelation = pair;
        Assert.Null(h.Findings.HighlightStart);
    }

    [Fact]
    public async Task AvailabilityOnlyCorrelationsStayOutOfThePerformanceListButRemainAvailable()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 300 and < 380 ? 210 : 30), skip: i => i is >= 380 and < 460);
        var f = h.Findings;
        Assert.Empty(f.PerformanceCorrelations);
        var context = Assert.Single(f.ContextCorrelations);
        Assert.Equal(CorrelationPattern.AvailabilityOverlap, context.Correlation.Pattern);
        Assert.Equal("Recording and availability context (1)", f.ContextHeader);
        Assert.Equal("1 observation · 1 warning · 0 performance correlations · 1 availability note", f.Summary);
        Assert.Contains("data coverage", f.DataQuality, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 gap", f.DataQuality);
        f.SelectedContextCorrelation = context;                                          // still selectable and navigable
        Assert.Same(context, f.ActiveCorrelation); Assert.Null(f.SelectedCorrelation);
        Assert.True(h.ViewStart <= At(300));
        Assert.True(h.Charts.Single(c => c.Id == "rtt").IsPrimary);
    }

    [Fact]
    public async Task SelectingAComponentOfACorrelationInspectsThatObservation()
    {
        var (h, _) = await OpenAsync(Busy);
        var f = h.Findings;
        f.SelectedCorrelation = f.PerformanceCorrelations.Single();
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Info);         // would hide the components
        var component = f.PerformanceCorrelations.Single().Components.First();
        f.SelectedComponent = component;
        Assert.Same(component, f.SelectedObservation); Assert.Null(f.SelectedCorrelation);
        Assert.Contains(component, f.VisibleObservations);                               // the filter was reset so the selection is visible
        Assert.Equal(["All (4)", "Critical (0)", "Warning (3)", "Info (1)"], f.SeverityFilters.Select(c => c.Label));
    }

    // ---------- data quality ----------

    [Fact]
    public async Task QualifiedObservationsStayVisibleAndAreLabelled()
    {
        // two thirds of the RTT values are missing during the episode
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 300 and < 420 ? (i % 3 == 0 ? 210 : null) : 30));
        var row = Row(h, "conn.rtt.high");
        Assert.Equal(AnalysisStatus.QualifiedObservation, row.Observation.Status);
        Assert.Contains(row, h.Findings.VisibleObservations);
        Assert.StartsWith("Qualified: ", row.StatusBadge); Assert.True(row.HasStatusBadge);
        Assert.Equal("Qualified: the evidence is incomplete", row.Status);
        Assert.Contains("Qualified", row.AutomationName);
        Assert.DoesNotContain(h.Findings.PerformanceCorrelations, c => c.Components.Contains(row));    // and it takes no part in a correlation
    }

    [Fact]
    public async Task MissingCountersAreNeverShownAsZeroAndAreGroupedByWhatTheyMean()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i is >= 100 and < 160 ? null : 30));
        var f = h.Findings;
        var groups = f.QualityGroups.ToDictionary(g => g.Heading);
        Assert.Contains(groups["No usable measurement"].Lines, l => l.StartsWith("Available bandwidth: ", StringComparison.Ordinal));      // never recorded
        Assert.Contains(groups["Partly missing measurements"].Lines, l => l.StartsWith("Round-trip time: 96.7%", StringComparison.Ordinal));  // 60 of 1,800 samples without a value
        Assert.Contains(groups["Insufficient evidence"].Lines, l => l.StartsWith("Bandwidth consumption close to the available bandwidth: ", StringComparison.Ordinal));
        Assert.Contains(groups["No anomaly detected"].Lines, l => l == "Sustained high round-trip time");
        Assert.DoesNotContain(f.Observations, o => o.Observation.Primary.ChannelId == "rtt");                // the missing minute is not a "0 ms" or an anomaly
        foreach (var group in f.QualityGroups) Assert.NotEmpty(group.Note);
        // the states are separate groups with their own headings
        Assert.True(groups.Count >= 4); Assert.Equal(groups.Count, f.QualityGroups.Select(g => g.Heading).Distinct().Count());
        Assert.StartsWith("Data quality and what could not be evaluated (", f.QualityHeader); Assert.Contains($"({groups.Count} kinds of note)", f.QualityHeader);
    }

    [Fact]
    public async Task AGapIsSummarizedWithTheRecordingsOwnMetadata()
    {
        var (h, _) = await OpenAsync(Busy, skip: i => i is >= 1000 and < 1015);
        var info = h.Loaded!.Info;
        Assert.Equal($"Data coverage: {(info.DataCoverage * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}% · 1 gap · 16 s missing", h.Findings.DataQuality);
    }

    [Fact]
    public async Task ARecordingInProgressIsPresentedAsASnapshotAndAnOpenObservationIsNotCalledRecovered()
    {
        var (h, _) = await OpenAsync(i => S(i, rtt: i >= 1700 ? 210 : 30));                // the problem is still going on when the data stops
        var analysis = AnomalyDetectionEngine.Analyze(h.Loaded!);
        var f = new HistoryFindingsViewModel(TimeZoneInfo.Utc, _ => { });
        f.Show(analysis, h.Loaded!.Info with { State = RecordingCompleteness.InProgress });
        Assert.StartsWith("Snapshot of a recording that is still being written", f.SnapshotNote); Assert.True(f.HasSnapshotNote);
        Assert.Contains("ends where the data ends, not where the problem ended", f.SnapshotNote);
        var open = f.Observations.Single(o => o.Observation.RuleId == "conn.rtt.high");
        Assert.Equal("Ended when data stopped", open.StatusBadge);
        Assert.Null(open.Observation.RecoveredAt); Assert.DoesNotContain("back within the normal criterion", open.Detail.Explanation);
        Assert.Contains("Recovery is not confirmed", open.Detail.Ending);
        Assert.Contains(f.QualityGroups.Single(g => g.Heading == "Recording incomplete").Lines, l => l.Contains("still in progress"));
        var complete = new HistoryFindingsViewModel(TimeZoneInfo.Utc, _ => { });
        complete.Show(analysis, h.Loaded.Info);
        Assert.False(complete.HasSnapshotNote);
    }

    [Fact]
    public async Task ARecordingWithNoFindingSaysNoAnomalyWasDetectedWithoutCallingTheSessionHealthy()
    {
        var (h, _) = await OpenAsync(i => S(i), count: 900);
        var f = h.Findings;
        Assert.Equal("0 observations · 0 performance correlations", f.Summary);
        Assert.Contains("not proof the session was healthy", f.EmptyNote);
        Assert.DoesNotContain("healthy.", f.EmptyNote.Replace("not proof the session was healthy", ""));
        Assert.Contains(f.QualityGroups, g => g.Heading == "No anomaly detected" && g.Note.Contains("not proof the session was healthy"));
    }

    // ---------- unknown protocol ----------

    [Fact]
    public async Task UnknownProtocolIsADetectionStateNotASecondProtocol()
    {
        // Blast is detected, then for a stretch no Blast counters are found (session inactive or disconnected), then it is detected again
        var rec = await HistoryFixture.RecordAsync(900, i => S(i, unknownProtocol: i is >= 300 and < 340));
        var h = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(["Omnissa Horizon Blast", "Unknown protocol"], h.Info.Protocols);                       // the loader reports what was recorded, in order of appearance
        Assert.Equal(2, h.Events.Count(e => e.Kind == HistoryEventKind.SessionChanged));                        // Blast → Unknown → Blast
        Assert.Equal("Omnissa Horizon Blast (not detected during part of the recording)", SessionHistoryViewModel.ProtocolSummary(h.Info));
        Assert.Contains("no Blast counters were found", SessionHistoryViewModel.ProtocolNote(h.Info), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(", Unknown protocol", SessionHistoryViewModel.ProtocolSummary(h.Info));
    }

    [Fact]
    public async Task ARecordingThatIsOnlyUnknownClaimsNoProtocol()
    {
        var rec = await HistoryFixture.RecordAsync(300, i => S(i, unknownProtocol: true));
        var info = HistoryFixture.Load(rec.Storage, rec.ShortId).Info;
        Assert.Equal(["Unknown protocol"], info.Protocols);
        Assert.Equal("Not detected", SessionHistoryViewModel.ProtocolSummary(info));
        Assert.Equal("Not recorded", SessionHistoryViewModel.ProtocolSummary(info with { Protocols = [] }));
        Assert.Equal("Microsoft RDP", SessionHistoryViewModel.ProtocolSummary(info with { Protocols = ["Microsoft RDP"] }));
        Assert.Equal("", SessionHistoryViewModel.ProtocolNote(info with { Protocols = ["Microsoft RDP"] }));
    }

    [Fact]
    public async Task TheAnalysisTreatsBlastWithUnknownGapsAsOneBlastSessionButAnUnknownOnlyRecordingKeepsItsLabel()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => i is >= 300 and < 360 ? S(i, rtt: 210, loss: 3, unknownProtocol: i is >= 330 and < 340) : S(i));
        var h = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Contains("Unknown protocol", h.Info.Protocols);
        var input = AnomalyInput.From(h);
        Assert.Equal(["Omnissa Horizon Blast"], input.Protocols);                                               // "Unknown" is no second protocol, so "Blast" wording stays
        var analysis = AnomalyDetectionEngine.Analyze(input);
        Assert.NotEmpty(analysis.Observations);
        Assert.All(analysis.Observations.Where(o => o.Primary.ChannelId == "packetLoss"), o => Assert.Contains("uplink", o.Explanation));
        var only = await HistoryFixture.RecordAsync(300, i => S(i, unknownProtocol: true));
        Assert.Equal(["Unknown protocol"], AnomalyInput.From(HistoryFixture.Load(only.Storage, only.ShortId)).Protocols);
    }

    // ---------- export ----------

    private static string TempFolder() { var f = Path.Combine(Path.GetTempPath(), "euclens-m3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(f); return f; }

    private static string Fingerprint(MemoryStorage storage) => string.Join("|", storage.Names(Dir).Order(StringComparer.Ordinal).Select(n =>
    {
        using var stream = storage.OpenRead(Path.Combine(Dir, n)); using var ms = new MemoryStream(); stream.CopyTo(ms);
        return n + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
    }));

    [Fact]
    public async Task ExportingTheFindingsWritesBothFilesWithoutChangingTheSelectionTheViewOrTheRecording()
    {
        var folder = TempFolder();
        try
        {
            var rec = await HistoryFixture.RecordAsync(1800, Busy);
            var desktop = new Desktop(); var h = CreateHistory(rec.Storage, desktop: desktop);
            await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled;
            var f = h.Findings;
            Assert.True(h.ExportFindingsCsvCommand.CanExecute(null) && h.ExportFindingsJsonCommand.CanExecute(null));
            f.SelectedObservation = Row(h, "conn.rtt.high"); await h.Settled;
            var (selected, start, end, emphasized) = (f.SelectedObservation, h.ViewStart, h.ViewEnd, h.Charts.Where(c => c.IsPrimary).Select(c => c.Id).ToArray());
            var before = Fingerprint(rec.Storage);

            desktop.ExportPath = Path.Combine(folder, "findings.csv");
            await h.ExportFindingsCsvCommand.ExecuteAsync();
            desktop.ExportPath = Path.Combine(folder, "findings.json");
            await h.ExportFindingsJsonCommand.ExecuteAsync();

            Assert.Contains("Findings exported (JSON)", f.ExportMessage); Assert.Contains("The recording was not changed", f.ExportMessage);
            Assert.Equal(FindingsExport.Json(f.CurrentAnalysis!, h.Loaded!.Info), await File.ReadAllTextAsync(Path.Combine(folder, "findings.json")));
            Assert.Equal(FindingsExport.Csv(f.CurrentAnalysis!, h.Loaded.Info), (await File.ReadAllTextAsync(Path.Combine(folder, "findings.csv"))).TrimStart('﻿'));
            Assert.Same(selected, f.SelectedObservation); Assert.Equal(start, h.ViewStart); Assert.Equal(end, h.ViewEnd);
            Assert.Equal(emphasized, h.Charts.Where(c => c.IsPrimary).Select(c => c.Id));
            Assert.Equal(before, Fingerprint(rec.Storage));                                      // the recording's files are exactly as they were
            Assert.Equal(["findings.csv", "findings.json"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.True(h.ExportFindingsCsvCommand.CanExecute(null));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ACancelledDialogAFailedWriteAndLeavingThePageAreAllHandledWithoutAPartialFile()
    {
        var folder = TempFolder();
        try
        {
            var rec = await HistoryFixture.RecordAsync(1800, Busy);
            var desktop = new Desktop(); var h = CreateHistory(rec.Storage, desktop: desktop);
            Assert.False(h.ExportFindingsCsvCommand.CanExecute(null));                           // nothing analyzed yet
            await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled;
            desktop.ExportPath = null;                                                           // the user closes the save dialog
            await h.ExportFindingsJsonCommand.ExecuteAsync();
            Assert.Equal("", h.Findings.ExportMessage); Assert.Empty(Directory.GetFiles(folder));

            desktop.ExportPath = Path.Combine(folder, "no-such-folder", "f.json");               // the disk or the folder refuses
            await h.ExportFindingsJsonCommand.ExecuteAsync();
            Assert.Contains("could not be exported", h.Findings.ExportMessage); Assert.Contains("No partial file was left", h.Findings.ExportMessage);
            Assert.True(h.ExportFindingsJsonCommand.CanExecute(null));                           // and the user can try again

            desktop.ExportPath = Path.Combine(folder, "left.json");
            var export = h.ExportFindingsJsonCommand.ExecuteAsync();
            h.OnPageHidden();                                                                    // the page is left while the file is being written
            await export;
            var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            Assert.True(files.Length == 0 || files.SequenceEqual(["left.json"]), string.Join(",", files));   // whole file or none, never a temporary or partial one
            if (files.Length == 1) using (JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "left.json")))) { }
            Assert.False(h.ExportFindingsCsvCommand.CanExecute(null));                           // the page released its analysis
            Assert.Null(h.Findings.CurrentAnalysis);
        }
        finally { Directory.Delete(folder, true); }
    }

    // ---------- lifetime ----------

    [Fact]
    public async Task ChangingTheRecordingClearsTheOldSelectionAtOnceAndOnlyTheNewResultsAppear()
    {
        var first = await HistoryFixture.RecordAsync(1800, i => S(i, rtt: i is >= 300 and < 420 ? 210 : 30));
        var second = await HistoryFixture.RecordAsync(1800, i => S(i, cpu: i is >= 900 and < 960 ? 97 : 20), storage: first.Storage);       // two recordings in one folder
        var storage = first.Storage;
        var h = CreateHistory(storage);
        await h.OpenAsync(Summary(storage, first.ShortId)); await h.Settled;
        h.Findings.SelectedObservation = Row(h, "conn.rtt.high");
        Assert.True(h.HasEmphasis);

        var opening = h.OpenAsync(Summary(storage, second.ShortId));                       // Release runs synchronously
        Assert.Null(h.Findings.SelectedObservation); Assert.Empty(h.Findings.Observations); Assert.Empty(h.Findings.PerformanceCorrelations);
        Assert.False(h.HasEmphasis); Assert.Null(h.Findings.HighlightStart);
        await opening; await h.Settled;
        Assert.DoesNotContain(h.Findings.Observations, o => o.Observation.RuleId.StartsWith("conn.", StringComparison.Ordinal));   // nothing of the old recording
        Assert.Contains(h.Findings.Observations, o => o.Observation.RuleId == "guest.cpu.saturation");
    }

    [Fact]
    public async Task AnAnalysisThatFinishesAfterTheRecordingWasLeftNeverShowsItsResult()
    {
        var rec = await HistoryFixture.RecordAsync(1800, Busy);
        var h = CreateHistory(rec.Storage);
        await h.OpenAsync(Summary(rec.Storage, rec.ShortId));                              // the analysis may still be running
        h.OnPageHidden();
        await h.Settled;
        Assert.Empty(h.Findings.Observations); Assert.Empty(h.Findings.VisibleObservations); Assert.False(h.HasHistory); Assert.Equal("Open a recording to analyze its measured performance.", h.Findings.Status);
        Assert.Equal("", h.Findings.Summary);
    }

    [Fact]
    public async Task LeavingThePageReleasesTheAnalysisAndTheChartsAndTheLoadedHistory()
    {
        var rec = await HistoryFixture.RecordAsync(1800, Busy);
        var (history, weak) = await Open(rec);
        history.Findings.SelectedObservation = history.Findings.Observations.First();
        history.OnPageHidden();
        await history.Settled;                                                             // a cancelled chart refresh may still be winding down
        Assert.Empty(history.Findings.Observations); Assert.Empty(history.Charts); Assert.False(history.HasEmphasis); Assert.Null(history.Findings.PeakAt);
        // A worker thread that ran the load may still hold its last result in a stale slot until it has run something else; let the pool turn over first.
        for (var i = 0; i < 20; i++) await Task.Run(() => Thread.Sleep(5));
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.TryGetTarget(out _), "the loaded history is still referenced after the page was left");
        GC.KeepAlive(history);

        static async Task<(SessionHistoryViewModel, WeakReference<LoadedHistory>)> Open(HistoryFixture.Recorded rec)
        {
            var h = CreateHistory(rec.Storage);
            await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled;
            return (h, new WeakReference<LoadedHistory>(h.Loaded!));
        }
    }

    [Fact]
    public async Task ExistingIncidentSelectionAndNavigationStillWorkNextToFindings()
    {
        var (h, _) = await OpenAsync(Busy, step: async (i, c) => { if (i == 1500) await c.MarkIncidentAsync(); });
        h.Findings.SelectedObservation = Row(h, "conn.rtt.high");
        var incident = h.Loaded!.Incidents.Single();
        h.SelectIncident(incident);
        await h.Settled;
        Assert.NotNull(h.SelectedIncident); Assert.Equal(incident.Id, h.SelectedIncident!.Incident.Id);
        Assert.True(h.ViewStart <= incident.At && h.ViewEnd >= incident.At);               // the incident centres the view as before
        Assert.NotEmpty(h.SelectedIncident.Summary);
        h.Findings.SelectedObservation = Row(h, "guest.cpu.spike");                        // and a finding can be selected afterwards
        await h.Settled;
        Assert.True(h.ViewStart <= At(900) && h.ViewEnd >= At(939));
    }

    [Fact]
    public async Task RepeatedOpeningFilteringNavigatingAndClosingDoesNotGrowTheHeap()
    {
        var rec = await HistoryFixture.RecordAsync(1800, Busy);
        async Task Cycle()
        {
            var h = CreateHistory(rec.Storage);
            await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled;
            var f = h.Findings;
            foreach (var severity in f.SeverityFilters) f.SelectedSeverity = severity;
            f.SelectedSeverity = f.SeverityFilters[0];
            foreach (var row in f.Observations.ToArray()) { f.SelectedObservation = row; await h.Settled; }
            f.SelectedCorrelation = f.PerformanceCorrelations.First(); await h.Settled;
            f.ShowFull(); await h.Settled;
            h.OnPageHidden();
        }
        for (var i = 0; i < 3; i++) await Cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 12; i++) await Cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var growth = GC.GetTotalMemory(true) - before;
        Assert.True(growth < 6L * 1024 * 1024, $"heap grew by {growth / 1024.0 / 1024.0:0.0} MiB over 12 cycles");
    }

    [Fact]
    public async Task ManyObservationsStayBoundedAndTheListsOnlyHoldWhatTheyShow()
    {
        // 150 separate RTT spikes: the findings model keeps every observation once, the visible list follows the filter
        var (h, _) = await OpenAsync(i => S(i, rtt: i % 40 == 20 && i >= 100 ? 400 : 30), count: 6200);
        var f = h.Findings;
        Assert.True(f.Observations.Count >= 100); Assert.True(f.Observations.Count <= 500);
        Assert.Equal(f.Observations.Count, f.VisibleObservations.Count);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Critical);
        Assert.True(f.VisibleObservations.Count <= f.Observations.Count);
        Assert.Equal(f.Observations.Count(o => o.Observation.Severity == Severity.Critical), f.VisibleObservations.Count);
        f.SelectedSeverity = f.SeverityFilters[0];
        Assert.Equal(f.Observations.Count, f.VisibleObservations.Count);
    }
}
