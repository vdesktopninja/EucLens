using EucLens.App;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The Session History workspace: findings of one rule grouped under one expandable line, the performance timeline's bars and lanes, and the synchronization between list, timeline and
/// charts. The grouping is presentation only: every observation keeps its identifier, interval, evidence and export row.
/// </summary>
public sealed class HistoryWorkspaceTests
{
    private static string Dir => HistoryFixture.Dir;

    private static SessionSnapshot Recurring(int i) =>
        HistoryFindingsPresentationTests.S(i, rtt: i is >= 250 and < 330 or >= 1000 and < 1090 ? 210 : 30,
            cpu: i is >= 200 and < 300 or >= 700 and < 800 or >= 1300 and < 1420 ? 97 : 20, fps: i is >= 1500 and < 1600 ? 7 : 30, dirty: i is >= 1500 and < 1600 ? 28 : 20);

    private static async Task<SessionHistoryViewModel> OpenAsync(Func<int, SessionSnapshot> make, int count = 1800, Func<int, bool>? skip = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, skipSample: skip);
        var h = HistoryFindingsPresentationTests.CreateHistory(rec.Storage);
        await h.OpenAsync(HistoryFindingsPresentationTests.Summary(rec.Storage, rec.ShortId));
        await h.Settled;
        return h;
    }

    private static HistoryFindingRow Header(HistoryFindingsViewModel f, string rule) => f.FindingRows.Single(r => r.IsGroup && r.RuleId == rule);

    [Fact]
    public async Task SeveralOccurrencesOfOneRuleAreOneExpandableLineWhileEachStaysItsOwnObservation()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var cpu = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").ToArray();
        Assert.Equal(3, cpu.Length);
        var header = Header(f, "guest.cpu.saturation");
        Assert.Equal("3 occurrences", header.CountText);
        Assert.False(header.IsExpanded);
        Assert.DoesNotContain(f.FindingRows, r => r.IsOccurrence);                              // collapsed: the group is one line
        Assert.Equal(f.Observations.GroupBy(o => o.Observation.RuleId).Count(), f.FindingRows.Count);   // one line per rule while every group is collapsed; nothing else changed

        header.IsExpanded = true;

        var occurrences = f.FindingRows.Where(r => r.IsOccurrence).ToArray();
        Assert.Equal(3, occurrences.Length);
        Assert.Equal(f.FindingRows.IndexOf(Header(f, "guest.cpu.saturation")) + 1, f.FindingRows.IndexOf(occurrences[0]));  // directly under the header
        // The very same observations, with their own identifiers, intervals and severities: grouping changed nothing in the analysis.
        Assert.Equal(cpu.Select(o => o.Id).Order(StringComparer.Ordinal), occurrences.Select(o => o.Item!.Id).Order(StringComparer.Ordinal));
        Assert.All(occurrences, o => Assert.Same(f.Observations.Single(x => x.Id == o.Item!.Id), o.Item));
        Assert.Equal(cpu.OrderBy(o => o.Observation.Start).Select(o => o.Id), occurrences.Select(o => o.Item!.Id));   // time order
        Assert.Equal(f.Observations.Count, f.CurrentAnalysis!.Observations.Count);
        Assert.Equal(f.CurrentAnalysis.Observations.Select(o => o.Id).Order(StringComparer.Ordinal), f.Observations.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.Equal(f.Observations.Count, f.Observations.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public async Task AGroupShowsTheWorstSeverityTheMostExtremeValueAndTheTotalAffectedTime()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var header = Header(f, "guest.cpu.saturation");
        var cpu = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").ToArray();
        Assert.Equal(cpu.Select(o => o.Observation.Severity).Min(s => s == Severity.Critical ? 0 : s == Severity.Warning ? 1 : 2) == 0 ? "Critical" : cpu.Any(o => o.Observation.Severity == Severity.Warning) ? "Warning" : "Info", header.SeverityTag);
        var highest = cpu.Max(o => o.Observation.Primary.Maximum);
        Assert.Equal("Highest", header.KeyLabel);
        Assert.Equal(FindingText.Value(highest, cpu[0].Observation.Primary.Unit), header.KeyValue);
        // The occurrences do not overlap, so the total is their sum; the helper below covers the overlapping case.
        var sum = cpu.Aggregate(TimeSpan.Zero, (t, o) => t + o.Observation.Duration);
        Assert.Equal("Total affected " + FindingText.Span(sum), header.TotalText);
        Assert.Equal(cpu.Min(o => o.Observation.Start) is var first ? cpu.OrderBy(o => o.Observation.Start).First().StartLabel + "–" + cpu.OrderBy(o => o.Observation.End).Last().EndLabel : "", header.Time);
    }

    [Fact]
    public void TheAffectedTimeNeverCountsOverlappingIntervalsTwice()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        (DateTimeOffset, DateTimeOffset) I(int from, int to) => (t0.AddSeconds(from), t0.AddSeconds(to));
        Assert.Equal(TimeSpan.FromSeconds(100), FindingGrouping.AffectedTime([I(0, 60), I(40, 100)]));                       // overlap counted once
        Assert.Equal(TimeSpan.FromSeconds(100), FindingGrouping.AffectedTime([I(0, 100), I(20, 30), I(50, 60)]));            // one inside another
        Assert.Equal(TimeSpan.FromSeconds(90), FindingGrouping.AffectedTime([I(0, 60), I(60, 90)]));                         // touching
        Assert.Equal(TimeSpan.FromSeconds(80), FindingGrouping.AffectedTime([I(200, 230), I(0, 50)]));                       // order does not matter, gap not counted
        Assert.Equal(TimeSpan.Zero, FindingGrouping.AffectedTime([]));
    }

    [Fact]
    public async Task ARuleThatFiredOnceIsASingleLineAndNeverAGroupOfOne()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var single = f.FindingRows.Where(r => r.IsSingle).ToArray();
        Assert.NotEmpty(single);
        Assert.All(single, r => { Assert.NotNull(r.Item); Assert.Equal("", r.CountText); Assert.Equal(1, f.Observations.Count(o => o.Observation.RuleId == r.RuleId)); });
        Assert.All(f.FindingRows.Where(r => r.IsGroup), r => Assert.True(f.Observations.Count(o => o.Observation.RuleId == r.RuleId) >= 2));
    }

    [Fact]
    public async Task LinesAreOrderedWorstFirstThenByTimeLikeTheObservationsThemselves()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        int Rank(string tag) => tag switch { "Critical" => 0, "Warning" => 1, _ => 2 };
        var lines = f.FindingRows.Where(r => !r.IsOccurrence).ToArray();
        Assert.Equal(lines.OrderBy(r => Rank(r.SeverityTag)).Select(r => Rank(r.SeverityTag)), lines.Select(r => Rank(r.SeverityTag)));
        Assert.Equal(f.FindingRows.Select(r => r.AutomationName).Count(), f.FindingRows.Count);
        Assert.All(f.FindingRows, r => Assert.False(string.IsNullOrWhiteSpace(r.AutomationName)));
    }

    [Fact]
    public async Task SelectingAnOccurrenceMovesNothingAndShowInChartsMovesTheChartsToItsRealInterval()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        Header(f, "guest.cpu.saturation").IsExpanded = true;
        var second = f.FindingRows.Where(r => r.IsOccurrence).ElementAt(1);

        f.SelectedRow = second;

        Assert.Same(second.Item, f.SelectedObservation);
        Assert.Null(f.HighlightStart); Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd); Assert.False(h.HasEmphasis);   // selected: the charts did not move
        f.ShowInChartsCommand.Execute(null);
        Assert.Equal(second.Item!.Observation.Start, f.HighlightStart);
        Assert.Equal(second.Item.Observation.End, f.HighlightEnd);
        Assert.Equal(second.Item.Id, f.TimelineSelectedId);
        Assert.Same(second.Item, f.SelectedRow!.Item);
        Assert.True(h.ViewStart <= second.Item.Observation.Start && h.ViewEnd >= second.Item.Observation.End);
    }

    [Fact]
    public async Task ChoosingAHeaderSelectsTheGroupShowsItsSpanWithoutAHighlightAndNeverOpensIt()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var members = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).ToArray();

        f.SelectedRow = Header(f, "guest.cpu.saturation");

        Assert.True(f.HasSelectedGroup);
        Assert.Null(f.SelectedObservation);
        Assert.Equal("guest.cpu.saturation", f.SelectedRow!.RuleId); Assert.True(f.SelectedRow.IsGroup);
        Assert.False(Header(f, "guest.cpu.saturation").IsExpanded);                                // choosing a header (an arrow key) does not open it
        Assert.Null(f.HighlightStart); Assert.Null(f.HighlightEnd);                                // the occurrences do not share one interval
        Assert.True(h.ViewStart <= members[0].Observation.Start && h.ViewEnd >= members[^1].Observation.End);   // the span of all of them is in view
        Assert.Equal("3 occurrences", f.SelectedGroup!.CountText);
        Assert.True(f.ShowFullRecordingCommand.CanExecute(null));

        f.ToggleGroup("guest.cpu.saturation");                                                      // the header's own gesture
        Assert.True(Header(f, "guest.cpu.saturation").IsExpanded);
        Assert.True(f.HasSelectedGroup);                                                           // the selection stays on the header
        Assert.Same(Header(f, "guest.cpu.saturation"), f.SelectedRow);

        f.SelectedRow = f.FindingRows.First(r => r.IsOccurrence);                                  // an occurrence replaces the group selection
        Assert.False(f.HasSelectedGroup); Assert.NotNull(f.SelectedObservation);
        f.ShowFull();
        Assert.False(f.HasSelection); Assert.Null(f.SelectedRow);
    }

    [Fact]
    public async Task OpeningAndClosingAGroupKeepsTheHeaderTheSelectionAndEveryOtherLineInPlace()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var other = f.FindingRows.First(r => r.IsSingle);
        f.SelectedRow = other;
        var before = f.FindingRows.ToArray();
        var header = Header(f, "guest.cpu.saturation");

        header.IsExpanded = true;
        Assert.Equal(before.Length + 3, f.FindingRows.Count);
        Assert.All(before, r => Assert.Contains(r, f.FindingRows));                                  // the same line objects: the list control keeps their containers and its focus
        Assert.Same(header, Header(f, "guest.cpu.saturation"));
        Assert.Same(other, f.SelectedRow);                                                           // and the selection

        header.IsExpanded = false;
        Assert.Equal(before, f.FindingRows.ToArray());
        Assert.Same(other, f.SelectedRow);
    }

    [Fact]
    public async Task MovingThroughTheListLineByLineNeverOpensOrClosesAGroup()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var before = f.FindingRows.Where(r => r.IsGroup).Select(r => (r.RuleId, r.IsExpanded)).ToArray();
        foreach (var row in f.FindingRows.ToArray()) f.SelectedRow = f.FindingRows.First(r => r.RuleId == row.RuleId && r.Kind == row.Kind);
        Assert.Equal(before, f.FindingRows.Where(r => r.IsGroup).Select(r => (r.RuleId, r.IsExpanded)).ToArray());
    }

    [Fact]
    public async Task CtrlArrowStepsThroughTheFindingsInTimeOrder()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var ordered = f.VisibleObservations.OrderBy(o => o.Observation.Start).ThenBy(o => o.Observation.End).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        Assert.True(f.SelectAdjacent(1)); Assert.Same(ordered[0], f.SelectedObservation);
        Assert.True(f.SelectAdjacent(1)); Assert.Same(ordered[1], f.SelectedObservation);
        Assert.True(f.SelectAdjacent(-1)); Assert.Same(ordered[0], f.SelectedObservation);
        Assert.True(f.SelectAdjacent(-1)); Assert.Same(ordered[0], f.SelectedObservation);        // stays at the first
        for (var i = 0; i < ordered.Length + 3; i++) f.SelectAdjacent(1);
        Assert.Same(ordered[^1], f.SelectedObservation);                                          // and at the last
        f.ShowFull();
        Assert.True(f.SelectAdjacent(-1)); Assert.Same(ordered[^1], f.SelectedObservation);       // nothing selected: backwards starts at the end
        Assert.Contains("Control", f.TimelineAutomationName);
    }

    [Fact]
    public async Task ClickingAFindingOnTheTimelineSelectsItOpensItsGroupAndTheListFollows()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var target = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).Last();
        Assert.DoesNotContain(f.FindingRows, r => r.IsOccurrence);

        Assert.True(f.SelectById(target.Id));

        Assert.Same(target, f.SelectedObservation);
        Assert.True(Header(f, "guest.cpu.saturation").IsExpanded);                                 // the occurrence is on screen in the list
        Assert.Same(target, f.SelectedRow!.Item);
        Assert.Equal(target.Id, f.TimelineSelectedId);
        Assert.False(f.SelectById("no-such-finding"));
        Assert.Same(target, f.SelectedObservation);
    }

    [Fact]
    public async Task TheTimelineDrawsEveryVisibleFindingAtItsRecordedIntervalAndNothingElse()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        Assert.Equal(f.Observations.Count, f.TimelineBars.Count);
        foreach (var bar in f.TimelineBars)
        {
            var o = f.Observations.Single(x => x.Id == bar.Id).Observation;
            Assert.Equal(o.Start, bar.Start); Assert.Equal(o.End, bar.End);                         // the recorded interval, not an interpolated one
            Assert.Equal(o.Severity, bar.Severity); Assert.Equal(o.Domain, bar.Domain); Assert.Equal(o.RuleId, bar.RuleId);
        }
        Assert.Equal(f.Observations.Select(o => o.Id).Order(StringComparer.Ordinal), f.TimelineBars.Select(b => b.Id).Order(StringComparer.Ordinal));

        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Guest);       // the filters apply to the timeline and the list alike
        Assert.All(f.TimelineBars, b => Assert.Equal(HistoryGroup.Guest, b.Domain));
        Assert.Equal(f.VisibleObservations.Count, f.TimelineBars.Count);
        Assert.Equal("3 occurrences", Header(f, "guest.cpu.saturation").CountText);
    }

    [Fact]
    public async Task ASelectedCorrelationOutlinesItsComponentsOnTheTimeline()
    {
        var h = await OpenAsync(HistoryFindingsPresentationTests.Busy);
        var f = h.Findings;
        var correlation = Assert.Single(f.PerformanceCorrelations);
        f.SelectedCorrelation = correlation;
        Assert.Equal(correlation.Components.Select(c => c.Id).Order(StringComparer.Ordinal), f.TimelineHighlightIds.Order(StringComparer.Ordinal));
        Assert.Null(f.TimelineSelectedId);
        f.ShowFull();
        Assert.Empty(f.TimelineHighlightIds);
    }

    [Fact]
    public async Task GroupsStayOpenAcrossFiltersAndAreRebuiltForAnotherRecording()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        Header(f, "guest.cpu.saturation").IsExpanded = true;
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Warning);
        f.SelectedSeverity = f.SeverityFilters[0];
        Assert.True(Header(f, "guest.cpu.saturation").IsExpanded);
        f.Clear();
        Assert.Empty(f.FindingRows); Assert.Empty(f.TimelineBars);
    }

    // ---------- overview ----------

    [Fact]
    public async Task TheOverviewIsCompactTilesAndNoDetailIsLost()
    {
        var h = await OpenAsync(Recurring);
        Assert.Equal(["Recorded", "Duration", "Protocol", "Status", "Samples", "Data coverage", "Findings", "Incidents"], h.Tiles.Select(t => t.Label));
        var tile = h.Tiles.ToDictionary(t => t.Label);
        Assert.Equal("Complete", tile["Status"].Value);
        Assert.Equal(1800.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), tile["Samples"].Value);
        Assert.Equal(FindingText.Count(h.Findings.Observations.Count), tile["Findings"].Value);
        Assert.Contains("warning", tile["Findings"].Detail);
        Assert.Equal("0", tile["Incidents"].Value);
        Assert.All(h.Tiles, t => Assert.False(string.IsNullOrWhiteSpace(t.AutomationName)));
        // The secondary facts and the identifier are in the details, nothing was removed.
        var facts = h.Facts.Select(f => f.Label).ToArray();
        foreach (var label in new[] { "Start", "Duration", "Data source", "Protocol", "Samples", "Recording ID", "Diagnostic warnings", "Critical findings", "Marked incidents", "Lost samples", "Data coverage", "Recording state" })
            Assert.Contains(label, facts);
        Assert.Contains(h.Facts, f => f.Label == "Recording ID" && f.Value.Length > 0);
    }

    [Fact]
    public async Task TheDataCoverageTileStatesGapsAndTheFindingsTileWaitsForTheAnalysis()
    {
        var h = await OpenAsync(Recurring, skip: i => i is >= 600 and < 660);
        var coverage = h.Tiles.Single(t => t.Label == "Data coverage");
        Assert.Contains("1 gap", coverage.Detail);
        Assert.Contains("missing", coverage.Detail);
        Assert.Equal("Info", coverage.Icon);
        Assert.True(h.Findings.HasAnalysis);
        h.OnPageHidden();
        Assert.Empty(h.Tiles);                                                                      // released with the recording
    }

    // ---------- the page (source level: WPF itself is validated on Windows) ----------

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Xaml => File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views/SessionHistoryView.xaml"));

    [Fact]
    public void ThePageIsBuiltFromTheOverviewTilesTheTimelineAndTheGroupedList()
    {
        var xaml = Xaml;
        foreach (var binding in new[] { "Monitor.History.Tiles", "Monitor.History.Findings.TimelineBars", "Monitor.History.Findings.TimelineSelectedId", "Monitor.History.Findings.TimelineHighlightIds",
                     "ItemsSource=\"{Binding FindingRows}\"", "SelectedItem=\"{Binding SelectedRow}\"", "{Binding DetailGroup}", "{Binding DetailObservation.KeyValue}", "Monitor.History.Gaps" })
            Assert.Contains(binding, xaml);
        Assert.Contains("<ui:FindingsTimeline x:Name=\"PerformanceTimeline\"", xaml);
        // The old list of one line per observation is gone from the page; the lines come from the grouped rows.
        Assert.DoesNotContain("ItemsSource=\"{Binding VisibleObservations}\"", xaml);
        // Tiles come before the timeline, the timeline before the findings, the findings before the range controls and the charts.
        var order = new[] { "Recording overview", "Performance timeline", "Performance findings", "Time range", "Historical charts", "Incident timeline" }.Select(n => xaml.IndexOf($"AutomationProperties.Name=\"{n}\"", StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void EveryExportIsInOneLabelledAreaWithItsOwnNameAndTooltip()
    {
        var xaml = Xaml;
        var area = xaml.IndexOf("AutomationProperties.Name=\"Export\"", StringComparison.Ordinal);
        Assert.True(area > 0);
        var end = xaml.IndexOf("</StackPanel>", area, StringComparison.Ordinal);
        var export = xaml[area..end];
        foreach (var (name, command) in new[] { ("Export findings as CSV", "ExportFindingsCsvCommand"), ("Export findings as JSON", "ExportFindingsJsonCommand"), ("Export incidents as CSV", "ExportIncidentsCommand"), ("Export timeline as CSV", "ExportTimelineCommand") })
        {
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", export);
            Assert.Contains(command, export);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, $"Binding DataContext.Monitor.History.{command}"));       // not duplicated elsewhere on the page
        }
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(export, "ToolTip=\"Optional").Count);
    }

    [Fact]
    public void NoEvidenceThresholdLimitationOrNoteWasRemovedFromThePage()
    {
        var xaml = Xaml;
        foreach (var kept in new[] { "DetailObservation.Detail.Explanation", "DetailObservation.Detail.KeyValues", "DetailObservation.Detail.Facts", "DetailObservation.Detail.Ending", "DetailObservation.Detail.Metrics",
                     "DetailObservation.Detail.Threshold", "DetailObservation.Detail.Limitations", "DetailObservation.Detail.Events", "Header=\"Measurement evidence\"", "Header=\"Recording details\"",
                     "{Binding SnapshotNote}", "{Binding DataQuality}", "{Binding Status}", "{Binding FilterNote}", "{Binding EmptyNote}", "{Binding ExportMessage}", "Monitor.History.PartialNote", "Monitor.History.IssueLines",
                     "Monitor.History.UnavailableNote", "Monitor.History.ZoneNote", "Monitor.History.Facts", "PerformanceCorrelations", "ContextCorrelations", "ActiveCorrelation.Explanation", "QualityGroups",
                     "Header=\"{Binding QualityHeader}\"", "AutomationProperties.Name=\"Severity filter\"", "AutomationProperties.Name=\"Area filter\"", "Show in charts", "Show full recording", "Monitor.History.Charts",
                     "Monitor.History.Events", "Save annotation", "Incident category", "Incident description" })
            Assert.Contains(kept, xaml);
    }

    [Fact]
    public void TheFiltersAreSmallAndComeAfterTheSummaryAndTheExportArea()
    {
        var xaml = Xaml;
        var summary = xaml.IndexOf("AutomationProperties.Name=\"Findings summary\"", StringComparison.Ordinal);
        var export = xaml.IndexOf("AutomationProperties.Name=\"Export\"", StringComparison.Ordinal);
        var severity = xaml.IndexOf("AutomationProperties.Name=\"Severity filter\"", StringComparison.Ordinal);
        var list = xaml.IndexOf("x:Name=\"FindingsList\"", StringComparison.Ordinal);
        Assert.True(summary > 0 && summary < export && export < severity && severity < list);
        Assert.Contains("FontSize=\"{StaticResource MetaFontSize}\" ItemsSource=\"{Binding SeverityFilters}\"", xaml);
    }

    [Fact]
    public async Task EveryLineAndTileHasAnAccessibleNameAndAGroupSaysWhetherItIsOpen()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        Assert.All(f.FindingRows, r => Assert.False(string.IsNullOrWhiteSpace(r.AutomationName)));
        var header = Header(f, "guest.cpu.saturation");
        Assert.Contains("Collapsed", header.AutomationName); Assert.Contains("3 occurrences", header.AutomationName); Assert.Contains("Total affected", header.AutomationName);
        header.IsExpanded = true;
        Assert.Contains("Expanded", header.AutomationName);                                         // the same line object, its name follows its state
        header.IsExpanded = false;
        Assert.Contains("Collapsed", header.AutomationName);
        header.IsExpanded = true;
        Assert.All(f.FindingRows.Where(r => r.IsOccurrence), r => Assert.StartsWith("Occurrence: ", r.AutomationName));
        Assert.All(h.Tiles, t => Assert.Contains(t.Label, t.AutomationName));
    }

    [Fact]
    public async Task TheChevronCommandOpensAndClosesAGroupAndNamesWhatItDoes()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var header = Header(f, "guest.cpu.saturation");
        Assert.StartsWith("Open the 3 occurrences of ", header.ToggleName);
        Assert.True(header.ToggleCommand.CanExecute(null));
        header.ToggleCommand.Execute(null);
        Assert.True(Header(f, "guest.cpu.saturation").IsExpanded);
        Assert.StartsWith("Close the 3 occurrences of ", header.ToggleName);
        header.ToggleCommand.Execute(null);
        Assert.False(Header(f, "guest.cpu.saturation").IsExpanded);
        Assert.False(f.FindingRows.First(r => r.IsSingle).ToggleCommand.CanExecute(null));          // a single line has nothing to open
        Assert.Contains("AutomationProperties.Name=\"{Binding ToggleName}\"", Xaml);
    }

    // ---------- master-detail: preview, details, occurrences ----------

    /// <summary>The recurring recording with one critical CPU episode (99.5 % for the whole episode) among warnings, and a critical round-trip episode later.</summary>
    private static SessionSnapshot Mixed(int i) =>
        HistoryFindingsPresentationTests.S(i, rtt: i is >= 1000 and < 1090 ? 350 : i is >= 250 and < 330 ? 210 : 30,
            cpu: i is >= 200 and < 300 ? 99.5 : i is >= 700 and < 800 or >= 1300 and < 1420 ? 92 : 20, fps: i is >= 1500 and < 1600 ? 7 : 30, dirty: i is >= 1500 and < 1600 ? 28 : 20);

    [Fact]
    public async Task WithNothingSelectedTheMostSevereFindingIsPreviewedAndNothingElseChanges()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        Assert.True(f.IsPreview);
        Assert.False(f.HasSelection); Assert.Null(f.SelectedObservation); Assert.False(f.HasSelectedGroup);
        var first = f.FindingRows[0];
        Assert.True(first.IsPreviewed);
        Assert.Equal(first.RuleId, (f.DetailGroup?.RuleId ?? f.DetailObservation?.Observation.RuleId));
        Assert.True(f.HasDetail);
        // A preview is presentation only: the charts, the timeline, the highlight and the way back stay as they were.
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.False(h.HasEmphasis);
        Assert.Null(f.TimelineSelectedId); Assert.Null(f.HighlightStart); Assert.Null(f.PeakAt); Assert.Empty(f.TimelineHighlightIds);
        Assert.False(f.ShowFullRecordingCommand.CanExecute(null));
        Assert.Equal(1, f.FindingRows.Count(r => r.IsPreviewed));
        Assert.Contains("Nothing is selected", f.PreviewNotice);
    }

    [Fact]
    public async Task ThePreviewIsTheMostSevereFindingNotTheEarliestOne()
    {
        var h = await OpenAsync(Mixed);
        var f = h.Findings;
        var worst = f.Observations.Where(o => o.Observation.Severity == Severity.Critical).ToArray();
        Assert.NotEmpty(worst);
        Assert.Equal("Critical", f.FindingRows[0].SeverityTag);
        Assert.Contains(f.DetailGroup?.RuleId ?? f.DetailObservation!.Observation.RuleId, worst.Select(o => o.Observation.RuleId));
    }

    [Fact]
    public async Task SelectingThePreviewIsARealSelectionThatNavigatesAndClearingBringsThePreviewBack()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var requests = f.ChartsRequest;
        Assert.True(f.SelectPreviewCommand.CanExecute(null));

        f.SelectPreviewCommand.Execute(null);

        Assert.True(f.HasSelection); Assert.False(f.IsPreview);
        Assert.True(f.ChartsRequest > requests);                                                     // the page is asked to bring the charts into view
        Assert.True(h.ViewEnd - h.ViewStart < h.SessionEnd - h.SessionStart);                        // and the charts moved to the finding
        Assert.DoesNotContain(f.FindingRows, r => r.IsPreviewed);
        Assert.True(f.ShowFullRecordingCommand.CanExecute(null));

        f.ShowFull();

        Assert.False(f.HasSelection); Assert.True(f.IsPreview);
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.True(f.FindingRows[0].IsPreviewed);
        Assert.True(f.SelectPreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePreviewFollowsTheFiltersAndDisappearsWhenNothingIsShown()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Graphics);
        Assert.True(f.IsPreview);
        Assert.Equal("gfx.fps.low", f.DetailObservation!.Observation.RuleId);                       // one finding: its observation, not a group
        Assert.Null(f.DetailGroup); Assert.False(f.HasDetailOccurrences);
        f.SelectedSeverity = f.SeverityFilters.Single(c => c.Value == FindingSeverityFilter.Critical);
        Assert.Empty(f.FindingRows);
        Assert.False(f.IsPreview); Assert.False(f.HasDetail); Assert.Null(f.DetailObservation); Assert.Null(f.DetailGroup);
        Assert.False(f.SelectPreviewCommand.CanExecute(null));
        f.SelectedSeverity = f.SeverityFilters[0]; f.SelectedDomain = f.DomainFilters[0];
        Assert.True(f.IsPreview);
    }

    [Fact]
    public async Task ARealSelectionIsNeverReplacedByThePreviewAndAFilterThatHidesItBringsThePreviewBack()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var fps = f.Observations.Single(o => o.Observation.RuleId == "gfx.fps.low");
        f.SelectById(fps.Id);
        Assert.False(f.IsPreview); Assert.Same(fps, f.DetailObservation);
        f.SelectedDomain = f.DomainFilters.Single(c => c.Value == FindingDomainFilter.Connection);   // hides the selected finding
        Assert.False(f.HasSelection);
        Assert.True(f.IsPreview);
        Assert.NotEqual("gfx.fps.low", f.DetailGroup?.RuleId ?? f.DetailObservation!.Observation.RuleId);
    }

    [Fact]
    public async Task TheDetailsListTheIndividualOccurrencesAndEachIsSelectableOnItsOwn()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        f.SelectedRow = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        var occurrences = f.DetailOccurrences;
        Assert.Equal(3, occurrences.Count); Assert.True(f.HasDetailOccurrences); Assert.Equal("Occurrences (3) · UTC time", f.DetailOccurrencesHeader);
        var cpu = f.Observations.Where(o => o.Observation.RuleId == "guest.cpu.saturation").OrderBy(o => o.Observation.Start).ToArray();
        Assert.Equal(cpu.Select(o => o.Id), occurrences.Select(o => o.Item!.Id));                     // the original observations, in time order
        Assert.All(occurrences, o => Assert.Same(f.Observations.Single(x => x.Id == o.Item!.Id), o.Item));
        Assert.Null(f.SelectedDetailOccurrence);                                                     // a group is shown, no occurrence chosen

        f.SelectedDetailOccurrence = occurrences[1];

        Assert.Same(cpu[1], f.SelectedObservation);
        Assert.Null(f.HighlightStart); Assert.Equal(h.SessionStart, h.ViewStart);                    // choosing it moves nothing
        f.ShowInChartsCommand.Execute(null);
        Assert.Equal(cpu[1].Observation.Start, f.HighlightStart); Assert.Equal(cpu[1].Observation.End, f.HighlightEnd);
        Assert.Equal(cpu[1].Id, f.TimelineSelectedId);
        Assert.Equal(cpu[1].Id, f.SelectedDetailOccurrence!.Item!.Id);
        Assert.Equal(3, f.DetailOccurrences.Count);                                                  // the list stays while one of its occurrences is shown
        Assert.True(f.HasDetailObservation); Assert.False(f.HasDetailGroup);
    }

    [Fact]
    public async Task AFindingThatFiredOnceHasNoOccurrenceList()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        f.SelectById(f.Observations.Single(o => o.Observation.RuleId == "gfx.fps.low").Id);
        Assert.Empty(f.DetailOccurrences); Assert.False(f.HasDetailOccurrences); Assert.Null(f.SelectedDetailOccurrence);
    }

    [Fact]
    public async Task AnOccurrenceNamesItsSeverityOnlyWhenItDiffersFromItsGroup()
    {
        var h = await OpenAsync(Mixed);
        var f = h.Findings;
        var header = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        Assert.Equal("Critical", header.SeverityTag);                                               // the worst occurrence
        header.IsExpanded = true;
        var lines = f.FindingRows.Where(r => r.IsOccurrence && r.RuleId == "guest.cpu.saturation").ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.SeverityTag == "Critical" && !l.ShowSeverityWord);
        Assert.Contains(lines, l => l.SeverityTag == "Warning" && l.ShowSeverityWord);
        Assert.All(f.DetailOccurrences, o => Assert.Equal(o.SeverityTag != "Critical", o.ShowSeverityWord));
    }

    [Fact]
    public async Task EveryLineStatesItsKeyMeasurementAndItsAffectedTimeWithTheirNames()
    {
        var h = await OpenAsync(Recurring);
        var f = h.Findings;
        var group = f.FindingRows.Single(r => r.IsGroup && r.RuleId == "guest.cpu.saturation");
        Assert.Equal("Total affected", group.AffectedLabel);
        Assert.Equal(group.TotalText["Total affected ".Length..], group.AffectedValue);
        Assert.Equal($"{group.KeyLabel} {group.KeyMetric}: {group.KeyValue}", group.KeyAutomation);
        var single = f.FindingRows.First(r => r.IsSingle);
        Assert.Equal("Duration", single.AffectedLabel); Assert.Equal(single.Item!.Duration, single.AffectedValue);
        Assert.Equal($"Duration: {single.Item.Duration}", single.AffectedAutomation); Assert.StartsWith("When: ", single.WhenAutomation);
        Assert.StartsWith("First to last: ", group.WhenAutomation);
        Assert.Equal(f.Observations[0].KeyAutomation, $"{f.Observations[0].KeyLabel} {f.Observations[0].KeyMetric}: {f.Observations[0].KeyValue}");
    }

    [Fact]
    public async Task TheTechnicalDetailCarriesTheExactIdentifiersAndSamples()
    {
        var h = await OpenAsync(Recurring);
        var o = h.Findings.Observations.First(x => x.Observation.RuleId == "guest.cpu.saturation");
        var lines = o.Detail.Technical;
        Assert.Contains($"Observation ID: {o.Id}", lines);
        Assert.Contains($"Rule: {o.Observation.RuleId}, version {o.Observation.RuleVersion}", lines);
        Assert.Contains(lines, l => l.StartsWith("Primary metric: ") && l.Contains($"channel {o.Observation.Primary.ChannelId}"));
        Assert.Contains(lines, l => l.StartsWith("Samples: ") && l.Contains(FindingText.Count(o.Observation.MatchingSamples)));
        Assert.Contains(o.Detail.Utc, lines);
        Assert.Equal(6, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("Kind: ") && l.Contains(o.Observation.Status.ToString()));
    }

    // ---------- master-detail layout ----------

    [Theory]
    [InlineData(1093, true)]    // 1366 wide at 100 %
    [InlineData(1647, true)]    // 1920 wide at 100 %
    [InlineData(1253, true)]    // 1920 wide at 125 %
    [InlineData(820, false)]    // 1366 wide at 125 %
    [InlineData(638, false)]    // 1366 wide at 150 %
    [InlineData(899, false)]
    [InlineData(900, true)]
    public void TheListAndTheDetailsAreSideBySideFrom900DipsAndStackedBelow(double width, bool sideBySide)
    {
        var r = MasterDetailLayout.Compute(width, true, MasterDetailOptions.Default);
        Assert.Equal(sideBySide, r.SideBySide);
        if (sideBySide)
        {
            Assert.InRange(r.MasterWidth, 400, 680); Assert.True(r.DetailWidth >= 400);              // neither column is ever too narrow to read
            Assert.Equal(width, r.MasterWidth + r.Gap + r.DetailWidth, 6);                           // the two columns and the gap use the whole width
            Assert.Equal(r.MasterWidth + r.Gap, r.DetailX, 6);
        }
        else { Assert.Equal(width, r.MasterWidth); Assert.Equal(width, r.DetailWidth); Assert.Equal(0, r.DetailX); }
    }

    [Fact]
    public void AWideWindowGivesTheExtraRoomToTheDetailsAndTheListNeverGrowsBeyondItsCap()
    {
        var narrow = MasterDetailLayout.Compute(1093, true, MasterDetailOptions.Default);
        var wide = MasterDetailLayout.Compute(1647, true, MasterDetailOptions.Default);
        var huge = MasterDetailLayout.Compute(2400, true, MasterDetailOptions.Default);
        Assert.True(wide.DetailWidth > narrow.DetailWidth);
        Assert.Equal(680, wide.MasterWidth); Assert.Equal(680, huge.MasterWidth);
    }

    [Fact]
    public void WithoutADetailsPaneTheListTakesTheWholeWidth()
    {
        var r = MasterDetailLayout.Compute(1200, false, MasterDetailOptions.Default);
        Assert.False(r.SideBySide); Assert.Equal(1200, r.MasterWidth); Assert.Equal(0, r.DetailWidth);
        Assert.False(MasterDetailLayout.Compute(double.PositiveInfinity, true, MasterDetailOptions.Default).SideBySide);
    }

    // ---------- the page (source level) ----------

    [Fact]
    public void TheFindingsPageIsAMasterDetailWithQuietExportsAndEverythingTechnicalBehindDisclosures()
    {
        var xaml = Xaml;
        Assert.Contains("<ui:MasterDetailPanel", xaml);
        Assert.DoesNotContain("MinimumColumnWidth=\"500\"", xaml);                                    // the old equal-columns panel is gone from the findings
        Assert.Contains("x:Name=\"DetailOccurrencesList\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Preview notice\"", xaml);
        Assert.Contains("Command=\"{Binding SelectPreviewCommand}\"", xaml);
        // exports are quiet text buttons
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(xaml, "StaticResource LinkButton").Count);
        // four disclosures, all closed until opened; the technical one carries the identifiers
        foreach (var name in new[] { "Measurement evidence", "Threshold and baseline", "Recording limitations and nearby events", "Technical detail" })
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", xaml);
        Assert.DoesNotContain("IsExpanded=\"True\"", xaml);
        Assert.Contains("DetailObservation.Detail.Technical", xaml);
        // the selected row is the table-row treatment of the other lists (accent bar and surface), the preview a quiet marker
        Assert.Contains("ItemContainerStyle=\"{StaticResource FindingLine}\"", xaml);
        Assert.Contains("Binding IsPreviewed", xaml);
    }

    [Fact]
    public void TheDataQualityDisclosureSitsUnderTheListSoTheListColumnIsNeverABlankArea()
    {
        var xaml = Xaml;
        var list = xaml.IndexOf("x:Name=\"FindingsList\"", StringComparison.Ordinal);
        var quality = xaml.IndexOf("AutomationProperties.Name=\"Data quality and evaluation details\"", StringComparison.Ordinal);
        var details = xaml.IndexOf("AutomationProperties.Name=\"Finding details\"", StringComparison.Ordinal);
        Assert.True(list > 0 && list < quality && quality < details, "the data-quality disclosure must follow the list inside the master column, before the details pane");
        // and the occurrence lines of the details name a severity only when it differs from the group's
        Assert.Contains("Visibility=\"{Binding ShowSeverityWord, Converter={StaticResource BoolToVisibility}}\"", xaml);
    }

    // ---------- timeline lanes ----------

    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static FindingBar Bar(string id, double from, double to, Severity severity = Severity.Warning) =>
        new(id, "rule", id, HistoryGroup.Guest, severity, T0.AddSeconds(from), T0.AddSeconds(to), "summary");

    [Fact]
    public void OverlappingFindingsGetDifferentLanesAndSeparateOnesShareTheFirst()
    {
        Func<DateTimeOffset, double> x = t => (t - T0).TotalSeconds;                                // one pixel per second
        var placed = FindingsTimelineLayout.Pack([Bar("b", 50, 150), Bar("a", 0, 100), Bar("c", 200, 260), Bar("d", 120, 130)], x, 3, 1, out var lanes);
        var lane = placed.ToDictionary(p => p.Bar.Id, p => p.Lane);
        Assert.Equal(0, lane["a"]); Assert.Equal(1, lane["b"]); Assert.Equal(0, lane["c"]);
        Assert.Equal(0, lane["d"]);                                                                  // d (120-130) overlaps b in lane 1, but a (0-100) has ended: lane 0 is free
        Assert.Equal(2, lanes);
        // no two bars of one lane overlap
        foreach (var group in placed.GroupBy(p => p.Lane))
            foreach (var pair in group.OrderBy(p => p.Left).Zip(group.OrderBy(p => p.Left).Skip(1)))
                Assert.True(pair.First.Right < pair.Second.Left);
    }

    [Fact]
    public void AVeryShortFindingInALongRecordingIsStillVisibleAndKeepsItsTrueExtent()
    {
        Func<DateTimeOffset, double> x = t => (t - T0).TotalSeconds / 28_800 * 1000;                // 8 hours over 1000 px
        var placed = FindingsTimelineLayout.Pack([Bar("short", 100, 103), Bar("next", 105, 108)], x, 3, 1, out var lanes);
        Assert.All(placed, p => Assert.True(p.Right - p.Left >= 3 - 1e-9));                             // visible
        Assert.Equal(2, lanes);                                                                    // and not hidden behind its neighbour (they would touch at the minimum width)
        Assert.Equal(x(T0.AddSeconds(100)), placed.Single(p => p.Bar.Id == "short").Left);          // the start is the recorded start
        Assert.Equal(T0.AddSeconds(103), placed.Single(p => p.Bar.Id == "short").Bar.End);          // the recorded end is untouched
    }

    [Fact]
    public void LanePlacementIsDeterministicWhateverTheInputOrder()
    {
        Func<DateTimeOffset, double> x = t => (t - T0).TotalSeconds;
        var bars = Enumerable.Range(0, 60).Select(i => Bar("f" + i.ToString("000"), i * 7 % 200, i * 7 % 200 + 20 + i % 13)).ToArray();
        var a = FindingsTimelineLayout.Pack(bars, x, 3, 1, out var lanesA).Select(p => (p.Bar.Id, p.Lane, p.Left, p.Right)).ToArray();
        var b = FindingsTimelineLayout.Pack(bars.Reverse(), x, 3, 1, out var lanesB).Select(p => (p.Bar.Id, p.Lane, p.Left, p.Right)).ToArray();
        Assert.Equal(lanesA, lanesB);
        Assert.Equal(a.OrderBy(p => p.Id), b.OrderBy(p => p.Id));
    }

    [Fact]
    public void ManyOverlappingFindingsStayBoundedAndFast()
    {
        Func<DateTimeOffset, double> x = t => (t - T0).TotalSeconds / 10;
        var bars = Enumerable.Range(0, 500).Select(i => Bar("f" + i, i * 30, i * 30 + 600)).ToArray();     // each overlaps its 20 neighbours
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var placed = FindingsTimelineLayout.Pack(bars, x, 3, 1, out var lanes);
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.Equal(500, placed.Count);
        Assert.InRange(lanes, 20, 25);                                                              // as many lanes as overlap, not one per finding
    }

    [Fact]
    public void TheTracksAreConnectionGraphicsAndGuestInTheOrderOfTheCharts()
    {
        Assert.Equal([HistoryGroup.Connection, HistoryGroup.Graphics, HistoryGroup.Guest], FindingsTimelineLayout.Tracks);
    }
}
