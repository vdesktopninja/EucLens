# EUC Lens 0.5.1 — Session History as a diagnostic workspace

Branch `release/euc-lens-0.5.1`. Presentation only: the metric collection, the diagnostic and anomaly rules and thresholds, the recording format, the identifiers of findings and correlations, data retention, and privacy and security behaviour are unchanged. Demo Mode, workload selection and AI are not part of this branch.

The 0.5.0 page was a long column of text blocks: a recording summary, twelve facts, then a three-line row for every finding, each in its own bordered panel. A recording in which one rule fired five times produced five nearly identical rows, nothing showed when the findings happened in relation to each other, and the filters and export buttons took as much room as the findings. This document describes what replaced it. The interface over the findings (`M2_FINDINGS_UX.md`) is otherwise as before.

## 1. Page, top to bottom

1. **Recording selection**: the saved-recording list with *Refresh list* and *Open folder* beside it, then one status line.
2. **Recording overview**: eight tiles in one row where the page is wide enough (two rows of four below that): *Recorded* (date and start time, until when), *Duration*, *Protocol*, *Status* (with its icon), *Samples*, *Data coverage* (gaps and missing time), *Findings* (critical, warning and informational counts) and *Incidents*. Notices that matter (partial analysis, reading problems) stay visible under the tiles. A collapsed **Recording details** expander holds everything else: the twelve facts of 0.5.0 plus the **Recording ID**, the state explanation, the one-line summary, the time-zone note and the list of measurements that are not in the recording.
3. **Performance timeline** (new, section 2).
4. **Performance findings** (section 4): a master list and a details pane. One line of title, summary and quiet export links; one line of filters with *Show full recording*; one line of context; then the list grouped by rule (section 3) beside the details of the selected, or previewed, finding. The performance correlations, the availability context and the data-quality section follow as before.
5. Time range controls and the session strip, the synchronized charts, the incident timeline and annotations: unchanged.

At 1366×768 and 100 % the overview and the whole timeline fit the first screen. Below 1000 DIPs of page width the list and the selected finding stack instead of sitting side by side.

## 2. Performance timeline

One track each for **Connection**, **Graphics** and **VDI Guest**, in the order of the charts. Every finding is a bar over exactly its recorded interval (`AnomalyObservation.Start`…`End`, UTC instants from the recording). Nothing is interpolated, smoothed or merged, and a finding that was not detected is not drawn. Rows of data the recording does not have are not filled in: a missing counter shows as the finding's own "ended when data stopped" status, never as a bar.

* **Severity** is the colour and the height of the bar (critical full, warning three quarters, info half), so it never depends on colour alone. A legend sits above the timeline.
* **Overlapping findings** go to separate lanes of their track. The lanes are assigned in start order, each finding to the first lane where it does not touch the previous one (`FindingsTimelineLayout.Pack`): deterministic, identical for the same findings whatever their order. A track grows to hold its lanes (up to a bounded height that shrinks the lanes) instead of drawing bars over each other.
* **Long recordings**: a finding shorter than four pixels is drawn four pixels wide so that it can be seen and clicked; its recorded start and end are untouched. Two such findings that would touch go to different lanes. Five hundred findings that each overlap twenty others pack into about twenty lanes, not five hundred (a unit test also bounds the time).
* **Recording gaps** are a separate row, "Gaps", drawn from the same `HistoryGap` list as the strip below and the charts. They are never mixed into the finding tracks.
* The **range the charts show** is outlined across the tracks; dragging selects a range like the strip, Ctrl + wheel zooms, Home resets.
* The **filters** (severity and area) apply to the timeline and the list alike.

### Selection stays in sync

| Action | What follows |
|---|---|
| Click a bar | The finding is selected; its group opens in the list and the line scrolls into view; every chart moves to its interval with context, the interval is highlighted and its extreme marked (exactly what choosing it in the list does). |
| Choose an occurrence in the list | The bar is outlined on the timeline; the charts follow. |
| Choose a correlation | The bars of its components are outlined with a dashed line. |
| Ctrl + Left, Ctrl + Right on the timeline | The previous or next visible finding in time order. |
| Show full recording, Escape in the list | Selection, outline and highlight are cleared; the charts show the whole recording. |

## 3. Recurring findings are grouped

A rule that fired once is one line. A rule that fired two or more times is one **group header** with a count, for example *Sustained high guest CPU utilization · 3 occurrences*, that opens to its occurrences.

| Shown | How it is computed |
|---|---|
| **Severity** | The worst severity of any occurrence (the occurrences keep their own). |
| **Occurrences** | The number of observations of the rule that the filters let through. |
| **Key value** | The most extreme measured value of any occurrence in its recorded unit: the highest, or the lowest for frame rate. No value is converted. |
| **Span** | Start of the first to end of the last occurrence. |
| **Total affected** | The length of the **union** of the occurrences' intervals (`FindingGrouping.AffectedTime`): time covered by two overlapping occurrences counts once, and the time between occurrences does not count. |
| **Occurrences (opened)** | Each with its own time, duration, severity, key value and status badge, in time order. |

* **The grouping exists only in the list.** The analysis, the observations, their identifiers (`AnomalyObservation.Id`), the evidence, the correlations and every export are the ones the engine produced. The engine was not changed to merge anything. Each occurrence is selected, traced to the charts and exported on its own; the findings CSV and JSON contain one row per observation, grouped or not (`CurrentAnalysis` is what is exported).
* A **group header** is selectable: it selects the group (no observation), shows its summary on the right and moves the charts to the span of all occurrences without highlighting an interval, because the occurrences do not share one. It is never opened or closed by selecting it, so moving through the list with the arrow keys cannot open a group.
* Open and close with a **click** on the header line, **Right**, **Left**, **Enter** or **Space**, or the button *Open or close the occurrences* in the group's summary. Opening and closing inserts or removes only the occurrence lines: the header, the selection and the keyboard focus stay where they are.
* Choosing an occurrence from the timeline, a correlation or a marker opens its group.
* Groups stay open when a filter changes and start closed for a new recording.

## 4. Findings: master and details (0.5.1, second refinement)

**The list.** Each line reads, from the left: the severity icon, the title (with the count of occurrences on a group), a quiet line with the severity word, the measured metric and the time, then two right-aligned figures: the **key measurement** (the highest value, or the lowest for frame rate, in the recorded unit, in the value font) and the **affected duration** (*Duration* for one finding, *Total affected* for a group). An occurrence under a group is one tighter line: its own time, then the same two figures; it names its severity only when that differs from its group's. Lines are separated by a hairline; the selected line has the accent bar and surface of the other lists; a previewed line (below) has only a quiet grey bar.

**The details.** Beside the list from 900 DIPs of page width, under it below that (`MasterDetailPanel`; the list is 46 % of the width but never narrower than 400 or wider than 680, the details never narrower than 400). From the top: the severity icon and title; the severity word and area; three key-figure tiles (the key measurement and its metric, the duration or total affected, when); the exact key-values line (the worst value and when it was recorded, matching and valid samples, coverage), the UTC interval and how the finding ended; **What was observed**, the engine's own deterministic sentence, unchanged; a compact **Occurrences (n)** list when the rule fired more than once, each line selectable on its own with its own interval, duration and value; *Show in charts*. Four disclosures, all closed until opened: **Measurement evidence** (every metric with min, P50, P95, max, time of the worst value and valid samples), **Threshold and baseline**, **Recording limitations and nearby events** and **Technical detail** (observation ID, rule and version, kind, status and ending, primary metric channel and recorded unit, samples). Nothing is summarized, rounded or generated.

**No empty pane: a preview.** While nothing is selected the details show the most severe finding (the first line of the list) under a notice, "Preview of the most severe finding. Nothing is selected and the charts show the whole recording." A preview is presentation only: no selection, no change of the chart range, the timeline or the highlight, and *Show full recording* stays disabled. The button *Select and show in charts* turns it into a real selection (the charts move, the page brings them into view); *Show full recording*, or Escape in a list, clears a selection and the preview comes back. Filters change what is previewed, and when they hide every finding the details pane goes away and the list says so. A group is previewed as its summary with its occurrence list.

**Filters and exports** stay as small as before: *Severity* and *Area* segmented controls on one line, the selected one filled with the accent, *Show full recording* at the right of that line. The four exports are quiet text buttons labelled *Export* on the title line; their names, tooltips, files and keyboard stops are unchanged.

### Hierarchy of the first refinement

* The findings list rows are two lines: the title with the severity icon, the severity word, time and duration beneath, and the measured value on the right with the word *Highest* or *Lowest*. Rows are separated by a hairline, not by nested borders. The evidence of the selected finding is on the right of the list: the measured value first, then the explanation (verbatim from the engine), the key values, and collapsed **Measurement evidence** (metrics, threshold, baseline) and **Recording limitations and nearby events** sections.
* **Severity** and **Area** are small segmented controls on one line under the summary. They keep their counts and their automation names.
* **Export** is one labelled area: *Findings CSV*, *Findings JSON*, *Incidents CSV*, *Timeline CSV*. The files and their columns are the ones of 0.5.0. Each button has its own accessible name (for example "Export findings as CSV"), the same tooltip, and a keyboard stop.

## 5. Keyboard and screen readers

* Tab reaches the timeline; Ctrl + Left and Ctrl + Right step through findings; the plain arrow keys, + and −, Home, Page Up and Page Down keep their meaning for the range and the incidents (as on the strip).
* A WPF `Path` or a drawn bar has no UI Automation element of its own. The timeline therefore announces itself with a summary ("Performance timeline: 7 findings on three tracks …, press Control and Left or Right to move between findings"), a bar has a tooltip with its severity, title, measured value and time, and the **list is the accessible twin of the timeline**: every finding is a list line with a complete name (severity, title, value, time, duration, status), and a group says whether it is open and how to change that.
* Every tile has a name that reads label, value and qualifier. Severity is always the icon and its word together.

## 6. Validation

* Unit tests (`HistoryWorkspaceTests`): grouping, ordering, severity, key value, total affected (including overlapping intervals), single-line rules, selection and sync (list ↔ timeline ↔ charts), keyboard stepping, header behaviour, in-place open and close, filters, the timeline's bars equal the observations' intervals one to one, correlations outlined, lane packing (overlap, minimum width, determinism, 500 findings), the tiles, and the page's structure (bindings, order, one Export area with four named buttons, no evidence binding removed).
* Windows smoke test (`HistoryUxValidation`): four recordings written through the real writer — a short one with findings in all three areas, one with the same rule recurring, an 8-hour one with a pause and overlapping findings, one with gaps and unavailable counters — opened through *Analyze*, drawn in both themes at 1366×768 and 1920×1080 with the available space of 100 %, 125 % and 150 %. It checks, in every state, that the timeline's bars lie inside their tracks and axis and never overlap in a lane, that no text runs out of the overview, timeline or findings panel, that every button is visible and reachable and the list is bounded, and on a 100 % display that the overview and the timeline fit the first screen. It performs a real click on a bar, Ctrl + Left, and the header gestures (select, Right, Left, Enter, click), and checks that the list, the timeline and the charts agree.
* **Scale cases are simulated.** They change the space WPF lays the page out in (pixels divided by the scale); they do not change the Windows DPI of the machine. Real per-monitor DPI needs a manual check on a scaled display.

## 7. Limitations

* Grouping is by rule (`RuleId`). Two rules about the same resource (for example guest CPU saturation and a guest CPU spike) are two groups.
* *Total affected* is the union of the occurrences' recorded intervals. An occurrence that ended because data stopped contributes only up to that point, as its interval says.
* The timeline draws what the analysis found, up to the engine's own caps (500 observations in all, 100 per rule); a recording that reaches them says so in its limitations as before.
* A bar narrower than four pixels is drawn four pixels wide. At very long recordings its drawn width therefore overstates the finding's length; its tooltip and its line state the recorded times.
* The bars have no UI Automation element of their own (see section 5).
