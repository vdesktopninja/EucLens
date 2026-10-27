# EUC Lens 0.5 — M2: findings experience in Session History

Status: implemented in 0.5.0 (release candidate on `feature/euc-lens-0.5-intelligent-diagnostics`; not yet published). Manual Horizon acceptance (roadmap M4) was accepted by the project owner; it was not independently verified by the project's own tests. CSV/JSON export of findings was added in M3.

This document describes the interface over the M1 deterministic anomaly engine (`docs/M1_ANOMALY_ENGINE.md`). The engine, its rules, thresholds and baselines are unchanged; nothing here interprets a measurement differently from M1. A finding is a **measured condition**, never a confirmed cause.

## 1. Page layout (top to bottom)

1. **Recording selection and status** (unchanged).
2. **Compact recording summary.** State, then one line: `state · duration · samples · protocol · data coverage`. The notes that matter (in progress, interrupted, partial, damaged parts, time zone) stay visible. The twelve facts moved into a collapsed **Recording details** expander.
3. **Performance findings.** Summary line, snapshot notice, data-coverage line, filters, the findings list.
4. **Selected finding evidence** (below the list, only while something is selected).
5. **Performance correlations**, and a collapsed **Recording and availability context** section.
6. **Data quality and what could not be evaluated** (collapsed).
7. Existing time-range controls, timeline strip, synchronized charts, incident timeline and annotations (behaviour unchanged).

Everything uses the existing design system: `Panel`, `Reading`/`Meta`/`ValueText` text styles, `SeverityIcon` (distinct shapes, severity is never colour alone), `StatusText`, the `Segmented` list style for filters, `TableList`/`TableRow`, the font and spacing tokens, the dynamic theme brushes. No gradients, glass, emoji or decorative panels. No fixed widths for measurement text: every text wraps; lists scroll inside their own bounded viewport (`HistoryLayout.FindingsListMaxHeight`, `CorrelationListMaxHeight`) and hand the mouse wheel to the page at either end.

## 2. Findings list

Summary, from the actual analysis: `6 observations · 1 warning · 5 informational · 2 performance correlations` (critical and availability notes appear only when there are any; "1 warning", "2 warnings"; "0 observations" is stated, not hidden).

Filters (segmented controls with counts, independent of each other): **Severity** All / Critical / Warning / Info, **Area** All areas / Connection / Graphics / VDI Guest. A note says `Showing 3 of 6 findings` when a filter hides some. A filter that hides the selected finding clears the selection and the emphasis; one that keeps it keeps it selected. Order: critical, warning, info, then time.

Row (three lines, scannable):

```
Warning  Sustained packet loss
Packet loss: P95 4.2% · max 7.1%
18:58:21–19:00:24 · 2 min 03 s · Connection
```

* The measurement line uses the primary `MetricEvidence` in the **recorded unit**: P95 and maximum for higher-is-worse metrics, lowest and P50 for frame rate. No value is converted; a metric without valid values says so instead of showing a number.
* Times use `HistoryTime` in the selected zone; the detail also shows the UTC interval.
* A status line appears only when the evidence is limited: `Qualified: 33.3% usable samples`, `Ended when data stopped`, `Ended when display activity stopped`.
* The row's automation name carries severity, title, measurement, time, duration and status.

## 3. Selected finding

* **What was observed**: the M1 explanation, verbatim (it already ends with "The recording does not establish the underlying cause").
* **Key values, always visible**: highest (or lowest) value and when it was recorded, matching of valid samples, coverage; the UTC interval; how it ended (`Recovered`, or "Recovery is not confirmed").
* **Measurement evidence** (collapsed): per metric, unit, min, P50, P95, max, time of the worst value, valid samples; the criterion, its basis (fixed, baseline, both) and the session baseline (median, robust deviation, samples and span) or why it was not used.
* **Recording limitations and nearby events** (collapsed, counted): the observation's limitations (uplink-only loss, coverage ending, unverified baseline, reduced-confidence input, protocol evidence) and the recorded events within five seconds.
* Buttons: **Show in charts** and **Show full recording**.

## 4. Navigation

Selecting a finding (mouse, or arrow keys in the list):

1. The shared range goes to the finding's real interval (recorded UTC instants) with context on both sides: half the finding's length, at least 15 s, at most the incident context the user chose (±1…10 min). The range is clamped into the session as before and all charts, the strip and the overlay follow the same `SetRange`.
2. The chart whose id equals the primary `MetricEvidence.ChannelId` is **emphasized** (accent border and background, label "Evidence for the selected finding"); other metrics of the observation, such as the Dirty FPS that established display activity, are labelled "Supporting measurement". Only the chart rows' presentation changes; their data, range, LiveCharts objects and subscriptions are untouched (no animation, nothing is created per selection).
3. The finding's interval is **shaded** on the charts overlay and the timeline strip (`HighlightStart/End`), and `MetricEvidence.WorstAt` is marked on both with a dotted line and a diamond (`PeakAt`). The pointer readouts show the recorded value of every chart at that instant.
4. A click on a finding, or **Show in charts**, scrolls the page just far enough to show the emphasized chart. **Moving with the arrow keys never scrolls the page** (the list keeps the keyboard focus).
5. **Show full recording** (button, or Escape in a findings list) clears the selection, the emphasis and the highlight and shows the whole recording.

Correlations:

* Performance correlations (RTT + packet loss, graphics + connection, FPS + guest CPU, several guest resources) are listed with their components, e.g. `Round-trip time + Packet loss`. Selecting one shows its timing, the M1 explanation, how the components are linked, and the components as a list; selecting a component inspects that observation (a filter that hides it is reset first).
* **Timing is shown as M1 defines it and never more**: `CommonOverlap` → "Present together hh:mm:ss–hh:mm:ss", highlighted and navigated to; `WithinTolerance` → "Close together in time, not overlapping" with the gap; `Chained` → "Linked in a chain: no single time interval contains every component", each pair listed from `CorrelationLink` (its own overlap or its gap). Only a true common overlap is shaded; chained and tolerance-linked correlations navigate to the span of everything linked and shade nothing.
* Availability-only correlations (an observation next to a gap, disconnect, pause or session change) are in a separate collapsed section, with a count, still selectable and navigable. They are not in the performance list and not in the performance count.

## 5. Data quality

* `Data coverage: 98.5% · 1 gap · 14.8 s missing` (from `HistoryInfo`, plus lost samples when there are any).
* A **snapshot notice** for a recording that is in progress ("Snapshot of a recording that is still being written … an observation that is still open ends where the data ends, not where the problem ended"), interrupted, or partial. An observation that ends because data stops is never called recovered.
* A collapsed section, grouped by what each state means (each with its own heading, note and count; empty groups are not shown):
  * **Recording incomplete** — in progress, interrupted, partial, reduced confidence, the analysis limitations;
  * **No usable measurement** — never recorded or never valid (not shown as zero);
  * **Unverified or unsupported units** — shown as recorded, never judged;
  * **Partly missing measurements** — e.g. `Round-trip time: 96.7% of samples had a value`;
  * **Insufficient evidence** — rules that could not run, with the reason;
  * **Rule not applicable** — e.g. a Horizon-only counter on another protocol, a static screen;
  * **No anomaly detected** — rules that ran on usable data and matched nothing, with the note that this is not proof the session was healthy.

## 6. Lifetime and threading

Unchanged from the first M2 integration and re-verified: the analysis runs on a worker (`AnomalyDetectionEngine.AnalyzeAsync`) with a cancellation token; a result is shown only if its token is live **and** its history is still the open one; opening another recording or leaving the page cancels, then `Findings.Clear()` drops every item, selection, highlight and emphasis; the loaded history, chart points and rows are released (a test proves the history is collectible after the page is left). The findings model holds the immutable M1 records only (at most 500 observations), never the history, and mutates nothing recorded. Repeated open, filter, navigate and leave cycles are covered by a heap-growth test.

## 7. Unknown protocol: investigation

A real Horizon recording listed `Omnissa Horizon Blast, Unknown protocol`.

* **Source.** `OmnissaBlastProvider.Collect` sets `SessionInfo.Protocol` to `OmnissaBlast` only when this session's own Blast counter instance is found, and to `Unknown` otherwise (agent absent, Windows session inactive or disconnected, instance not yet present, ambiguous instances, counter-set error); `ProtocolDetection` carries the reason. The recorder writes a session record whenever `SessionInfo` changes; the loader turns each record into a protocol name and lists the names in order of first appearance.
* **Meaning.** "Unknown protocol" is a **detection state** (no Blast counters found at that moment), not a second protocol. In the real recording it follows Blast, i.e. a temporary loss of detection (consistent with the 14.8 s gap), not startup metadata. The provider is deliberately conservative ("the protocol is only claimed when evidence exists"); that is correct and unchanged.
* **Defects found and corrected (presentation and analysis labels only; no recorder, loader or format change):**
  1. The overview listed it as a protocol. It now reads `Omnissa Horizon Blast (not detected during part of the recording)` with an explanatory note; a recording that has nothing but Unknown says `Not detected`.
  2. The M1 input treated `[Blast, Unknown]` as a mix of protocols and so dropped the "Blast"/"uplink" wording. `AnomalyInput.From` now leaves "Unknown protocol" out when another protocol was detected (a recording with only Unknown keeps it). Segments still split at the recorded session changes, as before.
* Tests: `UnknownProtocolIsADetectionStateNotASecondProtocol`, `ARecordingThatIsOnlyUnknownClaimsNoProtocol`, `TheAnalysisTreatsBlastWithUnknownGapsAsOneBlastSessionButAnUnknownOnlyRecordingKeepsItsLabel`.

## 8. Tests

`HistoryFindingsPresentationTests` (35 tests, recordings through the real writer and loader): filters and counts, summary, row content, evidence detail, navigation to the real UTC interval (also in a DST zone: same instants, different presentation), emphasis, `WorstAt` equals the recorded extreme, shared chart ranges, correlation navigation (common, chained, tolerance, availability), qualified observations, missing counters never zero, snapshot of an in-progress recording, stale selection and stale analysis, release of the history, existing incident selection, repeated use, bounded lists, Unknown protocol.

Windows smoke (`HistoryValidation.FindingsExperience`, in the CI evidence): summary and data coverage bound, collapsed sections, filters through the real controls, selecting a finding through the list, all charts on the same axis range, interval and peak marked on overlay and strip, emphasized chart background and label, key values visible, Show in charts scrolls the emphasized chart into the viewport, arrow keys do not scroll the page, Escape returns to the full recording, light and dark screenshots, no text leaving the panel at 520 and 760 DIPs (the width left by 150 % scaling on 1366×768 and 125 % on 1920×1080).

## 9. Known limitations

* A correlation component list is not itself a chart: selecting a correlation emphasizes every component's chart, and the highlighted interval exists only for a true common overlap.
* The highlighted band and peak marker are drawn on the shared overlay (all charts), not per chart; the emphasized chart is identified by its border, background and label.
* "Show in charts" uses the page scroll viewer's minimal scroll; with the findings list and the charts on different parts of a tall page, the list scrolls out of view (use Show full recording or scroll back).
* Findings for a recording still being written are a snapshot: reopening the recording re-analyzes it.
* The DPI behaviour is verified at the widths that 125 % and 150 % scaling produce, not by switching the process DPI.
* CSV/JSON export of findings was not part of this iteration; it was added in M3 (`docs/M3_PERFORMANCE_RELIABILITY.md`).

## 10. Manual Horizon acceptance checklist

1. Open a real multi-part Horizon recording (and one still being written): the summary line and data coverage match the recording; "in progress" is shown as a snapshot.
2. Select each finding with the mouse and with the arrow keys: the charts move to the interval you expect, the right chart is emphasized, the extreme is where the chart shows it; the page does not jump with the keys.
3. Check the values of two findings against the raw recording (peak and time).
4. Select a chained and a common-overlap correlation: the text never claims a shared interval that the charts do not show.
5. Open the availability section and the data-quality section: gaps, disconnects and unavailable counters are explained, nothing appears as zero.
6. Confirm that `Unknown protocol` no longer appears as a protocol, and that its note matches what happened on the VDI (disconnect or counters absent).
7. Switch recordings quickly and leave the page during analysis: no stale finding appears; memory returns to its previous level after several cycles.
8. 100 %, 125 % and 150 % scaling on 1366×768 and 1920×1080, light and dark: no clipped text, all controls reachable with the keyboard, a screen reader announces the summary, rows and detail sections.
9. No outbound connections and no elevated permissions (as for M1).
