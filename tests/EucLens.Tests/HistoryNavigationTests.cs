using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Selecting a finding and navigating the charts are two separate actions. Choosing a finding by any route (the list, an occurrence, a group, the timeline, the keyboard, the occurrence list of the
/// details, a correlation) only selects it: the charts, their range, the highlight, the emphasis, the hover readout and the page stay as they were. "Show in charts" is the explicit action.
/// </summary>
public sealed class HistoryNavigationTests
{
    private static SessionSnapshot Recurring(int i) =>
        HistoryFindingsPresentationTests.S(i, rtt: i is >= 250 and < 330 or >= 1000 and < 1090 ? 210 : 30, loss: i is >= 270 and < 320 ? 3 : 0,
            cpu: i is >= 200 and < 300 or >= 700 and < 800 or >= 1300 and < 1420 ? 97 : 20, fps: i is >= 1500 and < 1600 ? 7 : 30, dirty: i is >= 1500 and < 1600 ? 28 : 20);

    private static async Task<SessionHistoryViewModel> OpenAsync(Func<int, SessionSnapshot> make, int count = 1800, Func<int, HistoryFixture.RecordingContext, Task>? step = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, step);
        var h = HistoryFindingsPresentationTests.CreateHistory(rec.Storage);
        await h.OpenAsync(HistoryFindingsPresentationTests.Summary(rec.Storage, rec.ShortId));
        await h.Settled;
        return h;
    }

    private sealed record Snapshot(DateTimeOffset Start, DateTimeOffset End, bool Emphasis, DateTimeOffset? Highlight, DateTimeOffset? Peak, DateTimeOffset? Hover, int ChartsRequests, string Range);
    private static Snapshot Take(SessionHistoryViewModel h) => new(h.ViewStart, h.ViewEnd, h.HasEmphasis, h.Findings.HighlightStart, h.Findings.PeakAt, h.HoverAt, h.Findings.ChartsRequest, h.RangeText);

    [Fact]
    public async Task ChoosingAFindingByAnyRouteOnlySelectsItAndLeavesTheChartsTheRangeAndThePageAlone()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var manualStart = h.SessionStart.AddMinutes(12); var manualEnd = h.SessionStart.AddMinutes(17);
        h.SetRange(manualStart, manualEnd);                                                         // a range the user chose
        var untouched = Take(h);
        Assert.Equal(manualStart, h.ViewStart);

        var cpu = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        var routes = new (string Route, Action Choose)[]
        {
            ("a single finding in the list", () => f.SelectedRow = f.FindingRows.First(r => r.IsSingle)),
            ("a group header", () => f.SelectedRow = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation")),
            ("the header's own open and close gesture", () => f.ToggleGroup("guest.cpu.saturation")),
            ("an occurrence in the list", () => f.SelectedRow = f.FindingRows.First(r => r.IsOccurrence)),
            ("a bar on the timeline", () => f.SelectById(f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").Last().Id)),
            ("Ctrl+Left and Ctrl+Right", () => { f.SelectAdjacent(1); f.SelectAdjacent(1); f.SelectAdjacent(-1); }),
            ("the occurrence list of the details", () => f.SelectedDetailOccurrence = f.DetailOccurrences.FirstOrDefault() ?? f.FindingRows.First(r => r.IsOccurrence)),
            ("a performance correlation", () => f.SelectedCorrelation = f.PerformanceCorrelations.First()),
            ("an availability note", () => { if (f.ContextCorrelations.Count > 0) f.SelectedContextCorrelation = f.ContextCorrelations[0]; }),
        };
        foreach (var (route, choose) in routes)
        {
            choose();
            Assert.True(untouched == Take(h), $"choosing {route} changed the charts, the range, the highlight, the hover or asked the page to scroll: {untouched} → {Take(h)}");
        }
        Assert.True(f.HasSelection);
        Assert.Equal(manualStart, h.ViewStart); Assert.Equal(manualEnd, h.ViewEnd);
    }

    [Fact]
    public async Task EverySelectionStillUpdatesTheTimelineTheListAndTheDetails()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var target = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).Last();

        f.SelectById(target.Id);                                                                    // what a click on its bar does

        Assert.Equal(target.Id, f.TimelineSelectedId);                                              // the bar stays highlighted
        Assert.Same(target, f.SelectedObservation); Assert.Same(target, f.DetailObservation); Assert.False(f.IsPreview);
        Assert.True(f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation").IsExpanded);   // its group is open
        Assert.Same(target, f.SelectedRow!.Item);
        Assert.Equal(h.SessionStart, h.ViewStart);                                                  // and still nothing moved
        Assert.Equal(target.Id, f.SelectedObservation!.Observation.Id);                             // the identifier is the engine's own
    }

    [Fact]
    public async Task ShowInChartsIsTheExplicitActionThatFocusesTheExactIntervalAndAsksThePageToScrollOnce()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var target = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).ElementAt(1);
        f.SelectById(target.Id);
        var requests = f.ChartsRequest;
        Assert.True(f.ShowInChartsCommand.CanExecute(null));

        f.ShowInChartsCommand.Execute(null);

        Assert.Equal(requests + 1, f.ChartsRequest);                                                // the page is asked to bring the charts into view, once
        Assert.Equal(target.Observation.Start, f.HighlightStart); Assert.Equal(target.Observation.End, f.HighlightEnd);
        Assert.Equal(target.Observation.Primary.WorstAt, f.PeakAt);
        Assert.True(h.ViewStart <= target.Observation.Start && h.ViewEnd >= target.Observation.End);
        Assert.True(h.ViewEnd - h.ViewStart < h.SessionEnd - h.SessionStart);
        Assert.Equal([target.Observation.Primary.ChannelId], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id));
        Assert.True(f.ChartsFocusedOnFinding);
    }

    [Fact]
    public async Task ShowInChartsForAGroupFocusesTheSpanOfItsOccurrencesWithoutAHighlightedInterval()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        f.SelectedRow = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        Assert.Equal(h.SessionStart, h.ViewStart);
        f.ShowInChartsCommand.Execute(null);
        var members = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").ToArray();
        Assert.True(h.ViewStart <= members.Min(m => m.Observation.Start) && h.ViewEnd >= members.Max(m => m.Observation.End));
        Assert.Null(f.HighlightStart);                                                              // the occurrences share no interval
    }

    [Fact]
    public async Task ShowFullRecordingRestoresTheWholeRangeAndEscapeKeepsARangeTheUserChose()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var a = h.SessionStart.AddMinutes(12); var b = h.SessionStart.AddMinutes(17);
        h.SetRange(a, b);
        f.SelectById(f.Observations.First().Id);

        f.ClearSelection();                                                                         // Escape in a list

        Assert.False(f.HasSelection); Assert.True(f.IsPreview);                                     // the preview is back
        Assert.Equal(a, h.ViewStart); Assert.Equal(b, h.ViewEnd);                                   // the user's range is not undone
        Assert.Equal(0, f.ChartsRequest);

        f.SelectById(f.Observations.First().Id);
        f.ShowInChartsCommand.Execute(null);                                                        // now a finding is shown in the charts
        f.ClearSelection();                                                                         // Escape returns the charts to the whole recording
        Assert.False(f.HasSelection); Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd); Assert.False(h.HasEmphasis); Assert.False(f.ChartsFocusedOnFinding);

        h.SetRange(a, b); f.SelectById(f.Observations.First().Id);
        f.ShowFullRecordingCommand.Execute(null);                                                   // the button always shows everything
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd); Assert.False(f.HasSelection);
    }

    [Fact]
    public async Task IncidentMarkersAndManualRangesStillNavigateAndAFindingSelectedAfterwardsDoesNotUndoThem()
    {
        var h = await OpenAsync(Recurring, step: async (i, c) => { if (i == 1000) await c.MarkIncidentAsync(); });
        var f = h.Findings;
        var incident = h.Loaded!.Incidents.Single();

        h.SelectIncident(incident);                                                                 // an incident marker centres the charts, as before
        await h.Settled;
        Assert.True(h.ViewStart <= incident.At && h.ViewEnd >= incident.At && h.ViewEnd - h.ViewStart < h.SessionEnd - h.SessionStart);
        var afterIncident = (h.ViewStart, h.ViewEnd);

        f.SelectById(f.Observations.First().Id);                                                    // choosing a finding leaves it there
        Assert.Equal(afterIncident, (h.ViewStart, h.ViewEnd));

        h.ZoomBy(0.5);                                                                              // zoom, a manual range, the reset: all still the user's
        Assert.True(h.ViewEnd - h.ViewStart < afterIncident.Item2 - afterIncident.Item1);
        h.SetRange(h.SessionStart.AddMinutes(3), h.SessionStart.AddMinutes(6));
        Assert.Equal(h.SessionStart.AddMinutes(3), h.ViewStart);
        f.SelectAdjacent(1);
        Assert.Equal(h.SessionStart.AddMinutes(3), h.ViewStart);                                    // the keyboard did not change the zoom
        h.ResetZoom();
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
    }

    [Fact]
    public async Task ARecordingMadeByAnEarlierVersionWithAHistoricalWorkloadBehavesTheSame()
    {
        // Samples of 0.5.0 carry the workload the user had selected; they are read and ignored, and selection and navigation work as for any recording.
        var h = await OpenAsync(i => Recurring(i) with { Workload = WorkloadContext.VideoPlayback });
        var f = h.Findings;
        Assert.True(f.HasAnalysis); Assert.NotEmpty(f.Observations);
        Assert.True(f.IsPreview);
        var before = Take(h);
        f.SelectedRow = f.FindingRows.First(r => r.IsGroup);
        Assert.Equal(before with { }, Take(h));
        f.ShowInChartsCommand.Execute(null);
        Assert.True(f.ChartsFocusedOnFinding); Assert.True(h.HasEmphasis || f.HighlightStart is null);
        Assert.True(h.ViewEnd - h.ViewStart < h.SessionEnd - h.SessionStart);
    }

    [Fact]
    public async Task ChangingTheFiltersOrTheRecordingNeverMovesTheCharts()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        h.SetRange(h.SessionStart.AddMinutes(5), h.SessionStart.AddMinutes(9));
        var before = Take(h);
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Connection);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Warning);
        f.SelectById(f.VisibleObservations.First().Id);
        f.SelectedDomain = f.DomainFilters[0]; f.SelectedSeverity = f.SeverityFilters[0];
        Assert.Equal(before.Start, h.ViewStart); Assert.Equal(before.End, h.ViewEnd); Assert.Equal(before.ChartsRequests, f.ChartsRequest);
    }

    [Fact]
    public void OnlyTheExplicitRequestCanScrollThePageToTheCharts()
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "../.."));
        var code = File.ReadAllText(Path.Combine(repo, "src/EucLens.App/Views/SessionHistoryView.xaml.cs"));
        // BringChartIntoView is defined once and called from exactly one place: the handler of ChartsRequest, which only "Show in charts" raises.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, "BringChartIntoView\\(").Count);
        Assert.Contains("OnChartsRequested(object? sender, PropertyChangedEventArgs e) => BringChartIntoView();", code);
        var vm = File.ReadAllText(Path.Combine(repo, "src/EucLens.App/ViewModels/HistoryFindingsViewModel.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(vm, "RequestCharts\\(\\);"));                    // raised by ShowInCharts only
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(vm, "navigate\\(target\\)"));                     // the charts are told to move in one place
    }

    // ---------- concise details and unambiguous times ----------

    [Fact]
    public async Task TheDefaultReadingIsBuiltFromStructuredFieldsOnlyAndTheFullExplanationIsTheEnginesOwnText()
    {
        var h = await OpenAsync(Recurring);
        var o = h.Findings.Observations.First(x => x.Observation.RuleId == "guest.cpu.saturation");
        var facts = o.Detail.Facts.ToDictionary(x => x.Label, x => x.Value);
        var obs = o.Observation;
        Assert.Equal($"{FindingText.Count(obs.MatchingSamples)} of {FindingText.Count(obs.SampleCount)} valid samples met the criterion · coverage {FindingText.Percent(obs.Coverage)}", facts["Samples"]);
        Assert.Equal(HistoryTime.Format(obs.Primary.WorstAt, TimeZoneInfo.Utc), facts["Worst value at (UTC)"]);
        Assert.StartsWith("Recovered: valid values were back within the criterion at ", facts["Status"]);
        Assert.Equal(["Samples", "Worst value at (UTC)", "Status"], o.Detail.Facts.Select(x => x.Label));       // nothing the tiles already say: no value, duration or interval
        Assert.Equal(obs.Explanation, o.Detail.Explanation);                                                    // the narrative is the engine's, unchanged
        Assert.All(o.Detail.Facts, x => Assert.DoesNotContain("occurred because", x.Value));
        Assert.Contains(o.Detail.Utc, o.Detail.Technical);                                                      // the UTC interval stays, in the technical detail
    }

    [Fact]
    public async Task AFindingThatEndedWithTheDataHasNoClaimOfRecovery()
    {
        // The RTT episode runs into a stretch with no samples: its status says recovery is not confirmed.
        var rec = await HistoryFixture.RecordAsync(1800, i => HistoryFindingsPresentationTests.S(i, rtt: i is >= 300 and < 380 ? 210 : 30), skipSample: i => i is >= 380 and < 460);
        var h = HistoryFindingsPresentationTests.CreateHistory(rec.Storage);
        await h.OpenAsync(HistoryFindingsPresentationTests.Summary(rec.Storage, rec.ShortId)); await h.Settled;
        var o = h.Findings.Observations.Single(x => x.Observation.RuleId == "conn.rtt.high");
        Assert.StartsWith("Recovery not confirmed", o.Detail.Facts.Single(x => x.Label == "Status").Value);
        Assert.DoesNotContain(o.Detail.Facts, x => x.Value.StartsWith("Recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheShownTimesSayWhetherTheyAreUtcOrLocalEverywhere()
    {
        var h = await OpenAsync(Recurring);                                                         // the tests show UTC
        Assert.Equal("Times are UTC.", h.TimeBasis); Assert.Equal("Times are UTC.", h.Findings.TimeBasis);
        var o = h.Findings.Observations.First();
        Assert.Equal("When (UTC)", o.WhenLabel);
        Assert.Equal("Occurrences (3) · UTC time".Replace("(3)", $"({h.Findings.Observations.Count(x => x.Observation.RuleId == "guest.cpu.saturation")})"),
            (h.Findings.SelectedRow = h.Findings.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation")) is null ? "" : h.Findings.DetailOccurrencesHeader);
        Assert.StartsWith("First to last (UTC)", h.Findings.DetailGroup!.WhenLabel);

        var rec = await HistoryFixture.RecordAsync(600, Recurring);
        var plusOne = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "Test +1", "Test +1");
        var local = HistoryFindingsPresentationTests.CreateHistory(rec.Storage, plusOne);
        await local.OpenAsync(HistoryFindingsPresentationTests.Summary(rec.Storage, rec.ShortId)); await local.Settled;
        Assert.Equal("Times are local time (UTC+01:00).", local.TimeBasis); Assert.Equal(local.TimeBasis, local.Findings.TimeBasis);
        Assert.Equal("When (local)", local.Findings.Observations[0].WhenLabel);
    }

    [Fact]
    public void WhenTheClocksChangeInsideARecordingBothOffsetsAreNamedAndTheStoredInstantsAreNotTouched()
    {
        // A zone whose clocks go back at 09:00 local on 23 October 2026 (UTC+2 → UTC+1).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 9, 0, 0), 10, 23));
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-dst", TimeSpan.FromHours(1), "Test DST", "Test standard", "Test daylight", [rule]);
        var start = new DateTimeOffset(2026, 10, 23, 6, 30, 0, TimeSpan.Zero);                        // 08:30 local (UTC+2)
        var end = start.AddHours(3);                                                                // 09:30 UTC = 10:30 local (UTC+1)
        var basis = HistoryTime.Basis(zone, start, end);
        Assert.Contains("UTC+02:00 at the start", basis); Assert.Contains("UTC+01:00 at the end", basis); Assert.Contains("clocks changed", basis);
        Assert.Equal("Times are local time (UTC+02:00).", HistoryTime.Basis(zone, start, start.AddMinutes(20)));
        Assert.Equal("Times are local time (UTC+01:00).", HistoryTime.Basis(zone, end, end.AddHours(1)));
        // two changes that cancel out between the ends are still found
        var spring = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 23), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 24));
        var brief = TimeZoneInfo.CreateCustomTimeZone("test-brief", TimeSpan.FromHours(1), "Test brief", "Test standard", "Test daylight", [spring]);
        Assert.Contains("clocks changed", HistoryTime.Basis(brief, new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));
        // the instants are what they were: converting never changes them
        Assert.Equal(start, TimeZoneInfo.ConvertTime(start, zone).ToUniversalTime());
        Assert.Equal(end, TimeZoneInfo.ConvertTime(end, zone).ToUniversalTime());
        Assert.NotEqual(HistoryTime.Format(start, zone), HistoryTime.Format(end, zone));
        Assert.True(HistoryTime.IsUtc(TimeZoneInfo.Utc)); Assert.False(HistoryTime.IsUtc(zone));
        Assert.Equal("local", HistoryTime.Word(zone)); Assert.Equal("UTC", HistoryTime.Word(TimeZoneInfo.Utc));
    }

    [Fact]
    public void ThePageShowsTheTimeBasisNextToTheTimelineAndTheFindingsAndKeepsTheFullExplanationBehindADisclosure()
    {
        var xaml = File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "../..")), "src/EucLens.App/Views/SessionHistoryView.xaml"));
        Assert.Contains("{Binding Monitor.History.TimeBasis}", xaml);
        Assert.Contains("{Binding TimeBasis}", xaml);
        Assert.Contains("{Binding DetailObservation.WhenLabel}", xaml);
        Assert.Contains("Header=\"What was observed — full explanation\"", xaml);
        Assert.Contains("DetailObservation.Detail.Facts", xaml);
        // the explanation sits inside that disclosure, not directly in the pane
        var expander = xaml.IndexOf("Header=\"What was observed — full explanation\"", StringComparison.Ordinal);
        var explanation = xaml.IndexOf("Text=\"{Binding DetailObservation.Detail.Explanation}\"", StringComparison.Ordinal);
        var closing = xaml.IndexOf("</Expander>", expander, StringComparison.Ordinal);
        Assert.True(expander < explanation && explanation < closing);
    }

    private static string Source([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
}
