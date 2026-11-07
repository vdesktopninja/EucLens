# Diagnostic intelligence (0.4)

EUC Lens evaluates its measurements with offline, transparent rules. It does not
use AI, cloud services or telemetry, and it never changes Horizon or Windows
configuration. Each conclusion names the measurements, observation window and
decision rule behind it.

It answers three questions:

1. **Is there a measurable problem?** Three independent health domains, each with coverage.
2. **Which component is likely contributing?** Findings and cautious assessments ("suspected", "may be contributing").
3. **What to investigate next?** Recommended actions on every finding.

Correlation is never presented as proven causation. Findings are about the **VDI
guest**, not the hypervisor host: guest measurements cannot prove hypervisor contention.

## Architecture

| Component | Role |
|---|---|
| `MetricsHistory` / `MetricSeries` | Bounded ring buffers (512 entries per metric, no per-sample allocation) fed from the existing snapshot. No new collectors. |
| `WindowStats` | Mean, min/max, P25/P50/P75/P95, standard deviation, trend slope, covered time, valid/stale/failed counts per window. |
| `DiagnosticContext` | Read-only inputs to rules: snapshot, history, thresholds, monotonic time, workload, GPU presence. |
| `IDomainRule` | One self-contained rule: ID, domain, required metrics, evaluation. Add a rule by registering another implementation. |
| `FindingTracker` | Finding lifecycle: hysteresis, escalation, downgrade, recovery and expiry, with a transition event for each change. |
| `HealthEvaluator` | Connection, Graphics and VDI Guest assessments with coverage and evidence. |
| `HypothesisBuilder` | Combines findings into cautious explanations with verification steps. |
| `DiagnosticIntelligenceEngine` | Thread-safe orchestration. Runs on the sampling task, off the WPF UI thread. |

The 0.1–0.3 single-sample rules (`LatencyRule`, `SustainedCpuRule`, …) remain in the
library for API compatibility. The application uses the new engine.

### Time and samples

* Windows use **elapsed monotonic time** (`TimeProvider.GetTimestamp`), not wall-clock
  time or metric timestamps. Clock changes and missing metric timestamps do not
  affect them.
* Each valid sample represents the time since the previous sample, capped at
  `MaximumSampleGapSeconds` (5 s). Irregular intervals are handled correctly, and gaps
  reduce coverage instead of being assumed to be one second.
* Stale, failed and unavailable samples never enter statistics or thresholds. They are
  counted and shown as evidence.
* A rule only concludes when its window has ≥ 70 % coverage and ≥ 10 valid samples.
  Otherwise the result is *Insufficient Data*.

### Hysteresis, recovery and expiry

* **Raise:** a condition must hold for ≥ 70 % of the covered window time. One sample
  never raises a finding.
* **Escalate:** immediately when the critical condition holds.
* **Downgrade:** from Critical, only after the critical condition has not held for the
  30 s recovery window.
* **Recover:** only when values stay below the warning threshold minus a 10 % margin
  (for example RTT P95 < 135 ms with a 150 ms warning) over the 30 s recovery window.
  Values in between keep the finding active, so readings near a threshold do not cause
  detect/recover churn.
* **Expire:** a finding with no usable data for 120 s closes as *Expired*, so old
  findings do not stay active indefinitely.

Recovered and expired findings stay visible (most recent 50) with their end time and reason.

## Health assessment

| Domain | Required (else *Insufficient Data*) | Also evaluated when available |
|---|---|---|
| **Connection Health** | Blast RTT | Uplink packet loss, reconnect count |
| **Graphics Health** | FPS | Dirty FPS (screen activity), out queueing time, guest CPU |
| **VDI Guest Health** | Guest CPU, physical RAM | Available RAM, disk active time; GPU, VRAM and encoder when a hardware GPU exists |

* **State:** Critical or Warning when a finding in the domain is active; otherwise
  Healthy if the required metrics have sufficient data; otherwise **Insufficient Data**.
  Missing measurements are never treated as zero, and short observations never read
  as Healthy.
* **Coverage:**
  * *Full*: every applicable metric was evaluable.
  * *Partial*: some optional metrics were missing, or some were excluded by design
    (for example, jitter units are unverified).
  * *None*: the required metrics were missing.
* GPU findings count towards VDI Guest Health. Data-quality findings are listed but do
  not change any domain state.
* There is no 0–100 score.

## Rules and default thresholds

All values are **initial tunable defaults**: conservative starting points for VDI
troubleshooting, not vendor or industry standards. Every value can be changed (see
*Configuration*). Unless stated otherwise, rules use the 60 s standard window and the
sustained/coverage requirements above.

| Rule ID | Domain | Condition (Warning / Critical) | Rationale |
|---|---|---|---|
| `connection.rtt.sustained` | Connection | RTT ≥ 150 / 300 ms | Interactive remote desktops are commonly comfortable below ~150 ms. 300 ms is clearly degraded. RTT is round-trip, not one-way latency. |
| `connection.rtt.unstable` | Connection | RTT interquartile range (P75 − P25) ≥ 50 ms (Warning) | Detects persistently variable delay. The interquartile range ignores spikes in up to 25 % of samples, so brief spikes do not alert. |
| `connection.packetloss.uplink` | Connection | Mean uplink loss ≥ 1 / 5 % | Sustained loss above ~1 % noticeably affects interactive protocols. **Uplink only**; downstream loss is not inferred. |
| `connection.reconnects` | Connection | Reconnect counter +1 within 120 s (Warning) | A reconnect is a user-visible interruption. Recovers after 120 s without increase. |
| `graphics.fps.low` | Graphics | FPS < 15 for ≥ 70 % of the window (Warning) | Only while Blast dirty frames show the content changing at least as often as the threshold (dirty FPS mean ≥ 15 over the window). Without a dirty-frame counter low FPS is **not judged** (since 0.5.1; see *Screen activity*). A static, idle or slowly changing screen never alerts. |
| `graphics.fps.unstable` | Graphics | FPS coefficient of variation ≥ 0.5 while dirty-FPS variation < 0.25 (Warning) | Frame delivery fluctuates while content changes steadily. Needs measured screen activity. |
| `graphics.queueing` | Graphics | Out queueing time ≥ 50,000 µs (**Information only**) | Counter semantics (per-interval or cumulative) are not documented, so this indicator never changes Graphics Health. |
| `graphics.bottleneck.cpu` / `.gpu` / `.network` | Graphics | Low-FPS condition together with sustained CPU ≥ 90 %, GPU ≥ 90 % (hardware GPU only), or RTT/loss over their warning thresholds | Reported as a *possible* contributor. |
| `guest.cpu.saturation` | Guest | CPU ≥ 90 / 98 % | Evidence includes the logical processor count. With 2 vCPUs, one busy thread is ~50 %. |
| `guest.memory.pressure` | Guest | RAM ≥ 90 / 95 % **or** available ≤ 1.0 / 0.5 GiB | Low available physical memory leads to paging. Physical RAM only. |
| `guest.disk.saturation` | Guest | Disk active time ≥ 90 % (Warning) | Uses active time; throughput alone is never used. Real Session only. |
| `gpu.utilization`, `gpu.memory.pressure`, `gpu.encoder` | GPU | 90/98 %, 90/97 %, 85/95 % | Only with a hardware GPU and real counters. Missing counters are never 0 %. |
| `gpu.software-rendering` | GPU | Information | No hardware GPU is a **supported configuration**, never a warning by itself. |
| `dataquality.measurement-failures` | Data quality | ≥ 50 % of a core metric's samples stale or failed in 30 s (Warning) | Explains why assessments are limited. |
| `dataquality.blast-unavailable` | Data quality | Information | No Blast counters for this session; guest and graphics assessments continue. |
| `dataquality.excluded-metrics` | Data quality | Information | Jitter and estimated bandwidth have undocumented units and are shown as raw evidence only. Bandwidth saturation is not assessed (no verified capacity metric). UDP/TCP byte counts do not prove the negotiated transport. |

### Screen activity (no workload selection since 0.5.1)

Up to 0.5.0 the user selected a workload (Idle, Office, Interactive graphics, Video playback), and the frame-rate rules
ran only for a declared active workload. 0.5.1 removes the selection. The frame-rate rules (`graphics.fps.low`,
`graphics.fps.unstable`, `graphics.bottleneck.*`) now use only a measurement: the Blast dirty-frame counter.

| Measured content change rate (dirty FPS mean over the standard window) | Low FPS and the low-FPS bottleneck rules |
|---|---|
| ≥ `LowFps` (15): the content changes at least as often as the threshold | Evaluated with the unchanged thresholds (FPS < 15 for ≥ 70 % of 60 s, and so on). Fewer delivered frames than the content produces cannot be explained by the content. |
| ≥ `ActivityDirtyFps` (5) and < `LowFps`: slowly changing content (typing, scrolling a document) | Not a problem: the content itself asks for fewer than 15 frames per second; an open finding recovers. |
| < `ActivityDirtyFps`: the screen is mostly static | Not a problem; an open finding recovers ("low FPS is expected"). |
| No dirty-frame counter (or too few samples of it) | **Not judged**: nothing is claimed, an open finding closes, and the assessment says "Not judged — screen activity cannot be measured". |

`graphics.fps.unstable` keeps its own condition (dirty FPS ≥ `ActivityDirtyFps`, steady content, fluctuating delivery).
Session History's `gfx.fps.low` (rule version 2) uses the same gate on the recorded dirty FPS (±5 s mean ≥ 15), so a recording gets the same low-FPS conclusion in both places. The remaining differences (window, adaptive baseline, duration) are listed in [STABLE_RELEASE_0.5.1.md](validation/STABLE_RELEASE_0.5.1.md).
No threshold value was added or changed: the existing `LowFps` threshold now also states how often the content must change for
low FPS to be judged, which replaces the workload a user used to declare. The idle and static-screen handling stays conservative:
low FPS on a static or slowly changing screen is never a finding, and without activity evidence nothing is concluded. Recordings made by 0.5.0 keep their workload value; it is shown
in exports and never used by a rule. See [STABLE_RELEASE_0.5.1.md](validation/STABLE_RELEASE_0.5.1.md) for the audit and the limitations.

## Assessments (hypotheses)

| Situation | Statement |
|---|---|
| Connection findings, guest CPU normal | "Network-related connection degradation is suspected." |
| Low FPS (active) + CPU saturation, RTT normal | "Guest CPU saturation may be contributing to graphics performance degradation." |
| Low FPS without content changing at the threshold rate (static or slowly changing screen, or no dirty-frame counter), CPU and RTT normal | "No graphics performance problem can be inferred from low FPS alone." |
| No hardware GPU, guest and connection healthy | "No GPU-related problem detected. Software rendering is in use." |
| Connection findings + CPU saturation | "…the available evidence cannot distinguish which is primary." (multiple plausible causes, with steps to tell them apart) |
| CPU + memory pressure | Possible guest undersizing |
| Nothing abnormal | "No measurable performance problem detected in the evaluated metrics." (with coverage caveat) |
| All domains lack data | "Not enough valid measurements yet to assess the session." |

## Example output

The following was produced by the engine from **synthetic test data**. It is not a
measurement from any real environment.

**Healthy idle desktop, software rendering**

```
Connection: Healthy | Coverage Partial | RTT P95 36 ms · packet loss (uplink) mean 0 % · reconnects total 0 — within configured thresholds
            jitter and estimated bandwidth units unverified (shown raw, not evaluated); bandwidth saturation not assessed
Graphics:   Healthy | FPS P95 3 FPS · dirty FPS P95 1 FPS · CPU P95 16 % — within configured thresholds | low FPS judged only while Blast dirty frames show the content changing at least as often as the threshold
Guest:      Healthy | CPU P95 16 % · RAM P95 45 % · available RAM mean 4.4 GiB | software rendering — GPU analysis not applicable
Assessment: No graphics performance problem can be inferred from low FPS alone. [Expected — Blast dirty frames show a mostly static screen and CPU/RTT are normal]
Assessment: No GPU-related problem detected. Software rendering is in use. [Supported configuration]
```

**Network degradation**

```
Connection: Warning
Assessment: Network-related connection degradation is suspected. [Suspected — guest CPU is within thresholds]
WARNING — Elevated Blast round-trip time (connection.rtt.sustained)
Blast RTT was at or above 150 ms for 100% of the covered 60 s (mean 196.17 ms, P95 216.05 ms).
  - Time at or above 150 ms: 60 of 60 s (100%)
  - Window min / max: 180 / 219 ms
  - Observation: 60 s window · 60 valid of 60 samples · 100 % covered
Rule: Warning when Blast RTT ≥ 150 ms (Critical ≥ 300 ms) for ≥ 70 % of valid covered time in a 60 s window
      with ≥ 70 % coverage and ≥ 10 valid samples. Recovers when P95 < 135 ms over 30 s.
WARNING — Uplink packet loss (connection.packetloss.uplink)
Blast reported an average uplink packet loss of 2.5 % over 60 s (P95 2.5 %).
```

**CPU-bound video playback (2 vCPUs, software rendering)**

```
Graphics: Warning · Guest: Warning · Connection: Healthy
Assessment: Guest CPU saturation may be contributing to graphics performance degradation. [Possible — RTT remains within thresholds]
WARNING — Sustained guest CPU saturation (guest.cpu.saturation)
  - Window mean: 97 %  · P95: 98 %
  - Logical processors: 2 (One fully busy thread is about 50 % of total CPU on this VM.)
WARNING — Guest CPU saturation may be limiting frame rate (graphics.bottleneck.cpu)
Low FPS during an active workload coincides with sustained guest CPU ≥ 90 %. Blast software encoding uses guest CPU,
so CPU saturation may be contributing. Screen activity confirmed by Blast dirty frames.
```

## Diagnostics page (0.4.1)

The page is a presentation of the engine's report. `DiagnosticsViewModel` formats,
filters and sorts; it draws no conclusions of its own, so the page cannot disagree
with the engine, the recordings or the exports.

From top to bottom:

| Area | Content | Source |
|---|---|---|
| Status line | Whether any Warning/Critical finding is active, in which domain, and the most severe one; the engine's assessments with their "how to verify" steps. | Findings, assessments, hypotheses |
| Health overview | Connection, Graphics and VDI Guest: state, observation window, coverage, key measurements with the statistic (P95, P50, mean, cumulative total) and the configured threshold, active problem count and a one-line explanation. Per-input sample counts and coverage notes are behind *Coverage details*. | `DomainAssessment`, `AssessmentInput` |
| Evidence coverage | Full / Partial / Insufficient evidence, inputs evaluated vs unavailable, stale and failed samples, unverified-unit metrics, and raw collector availability, shown separately from diagnostic coverage. | `AssessmentInput`, latest snapshot |
| Diagnostic trends | Session RTT, FPS, guest CPU and uplink packet loss for the 30 / 60 / 120 s windows (the configured short, standard and long windows) on one shared time axis. | The live samples already collected |
| Findings | Sortable table (Severity, Finding, Category, First detected, Duration, State) with filters and counts: All, Active problems, Critical, Warning, Information, Recovered. | `DiagnosticFinding` |
| Evidence & recommendations | The selected finding: explanation, timeline, observation, evidence table (Measurement · Observed · Threshold), decision rule, possible contributing factors, numbered recommended investigation, data limitations. *Copy* places all of it on the clipboard. | `DiagnosticFinding` |

Presentation rules:

* **Colour is reserved for state.** Green marks Healthy, amber Warning and red Critical.
  Information, recovered findings and Insufficient Data stay neutral. No panel is filled with a status colour.
* **Information is not a problem.** Software rendering, unavailable Blast counters and
  unverified units appear as *Informational*. They never count as active problems and sort below recovered findings.
* **Default order:** active Critical, active Warning, recently recovered, Information.
  Clicking a column header sorts by that column; clicking again reverses it. Ties keep the default order.
* **Thresholds are shown only where the engine compares against one.** For example, RTT P95
  shows "≥ 150 ms warning · ≥ 300 ms critical" while the window mean shows none. Packet
  loss is judged by its mean, so the threshold sits on the mean. Generic labels are
  spelled out: "95th percentile (P95)", "Mean over window", "Latest sample", "cumulative counter".
* **No healthy claim without evidence.** With no active problems, the page says what was
  evaluated. If a domain could not be assessed, it says so ("Not assessed (insufficient data):
  Connection …"), and the status marker stays neutral rather than green.
* **Missing data is a gap.** Unavailable samples are null points, and collection pauses
  longer than `MaximumSampleGapSeconds` break the line. Neither is drawn as zero.
  Dashed reference lines are drawn only at configured thresholds:
  RTT warning/critical, low FPS (judged only while the content changes at least as often as the threshold), CPU warning and the
  packet-loss warning. Each value axis includes the warning line, so headroom is visible.
  A line above the plotted range is hidden rather than drawn outside the chart.
* **Software rendering** is stated once per place: an *Informational* finding (Graphics
  acceleration: Software rendering · Hardware GPU: Not detected · GPU diagnostics: Not
  applicable) and a "GPU: Software rendering, Not applicable" row under VDI Guest.
  Adapter text such as "No hardware GPU: Microsoft Basic Render Driver ×2 (DXGI software
  adapter (Microsoft Basic Render Driver); CPU rendering.)" is shown as "Microsoft Basic Render
  Driver ×2 — CPU rendering". GPU collection itself is unchanged.

Layout and performance:

* Layout is described under *Layout (0.4.2)* below.
* Rows, health tiles, coverage rows and evidence rows are updated in place by key or value.
  Selection, expanded sections and scroll position therefore survive the per-second update, and unchanged rows are not re-rendered.
* Until the administrator selects a finding, the evidence panel follows the most important one.
* Trend history is bounded by the long window (at most 720 points per chart) and uses the
  samples the dashboard already collects. Charts exist only while the page is open, with animations disabled.
* **Chart ownership.** View models hold chart *data* only (`ChartData`: points, axis ranges, reference
  lines). Each chart view builds its own LiveCharts series, axes, sections and point copies
  (`ChartObjects`) and follows the data through weak subscriptions. This applies to every chart in the app.
  * Why: LiveCharts 2.0 keeps every chart that ever drew a series reachable from that series (`Series.subscribedTo`
    is never cleared), and records a per-chart visual on each point. When series were shared, every page visit kept
    the previous page alive and kept updating it.
  * Result: a closed page is collected without any unload hook. A chart that is unloaded and loaded again resyncs from the data.
* LiveCharts configures itself on first use, and that first initialization is not thread-safe.
  `ChartsRuntime.EnsureInitialized` runs it once, under a lock, before any view model creates chart objects.
* The findings table, filters, splitter and evidence pane are keyboard reachable with visible focus (see 0.4.2).

## Layout (0.4.2)

0.4.2 polishes readability and layout only. No diagnostic rule, threshold, severity or conclusion changed, and the
chart memory architecture above is untouched.

**Typography.** Shared tokens in `Themes/Foundations.xaml`; the views contain no font-size numbers (a test enforces it).

| Role | Token | DIP |
|---|---|---|
| Body and explanations | `ReadingFontSize` | 14 |
| Key values (P95, mean, evidence) | `ValueFontSize`, tabular numerals | 15 |
| State, headline, latest chart value | `StateFontSize` | 16 |
| Technical metadata (rule, source, thresholds under values) | `MetaFontSize` | 12 |
| Page section headings | `SectionFontSize`, semibold | 14 |

Muted text keeps the existing theme brushes (about 6:1 on the light and 8:1 on the dark surface).

**Status area.** One line: state marker, headline, counts by level (Critical / Warning / Information), observation
window and evidence coverage. Below it one detail line. The assessment is collapsed; its header shows the engine's
first statement, so the conclusion is visible without expanding ("How to verify" stays one click away).

**Health overview.** Tiles show state and active-problem count, a one-line summary, and compact rows of
label · value · threshold. Required inputs always appear (a missing one reads "Not available", never 0). Optional inputs
without data are not listed as rows. The coverage line names them ("Partial — dirty fps, out queueing time unavailable;
jitter and estimated bandwidth units unverified"), and per-input sample counts stay under *Coverage details*.
Four tiles use 4 columns when each can have 320 DIP, otherwise 2, never an orphan row (`AdaptiveColumnsPanel.BalanceRows`).

**Trends.** Plots are 190 DIP high (116 before), axis and line labels 12 DIP, tooltips 13 DIP, threshold lines 1.5 px dashed.
Four columns from 400 DIP per chart, two columns otherwise. Chart headers share one height, so plots line up.

**Conditional thresholds.** Thresholds are either unconditional or conditional.
* Unconditional (RTT, packet loss, CPU, …): always drawn when configured.
* Low FPS is conditional. The engine evaluates it only while Blast dirty frames show the content changing at least as often
  as the threshold. The engine reports this through `AssessmentInput.ThresholdApplies` (null for every other input). The
  Frame-rate chart draws the line only when it is true, and its header states the condition either way: "Low FPS < 15 —
  applies now (Blast dirty frames ≥ 15 per second)" or "— conditional: applies during Blast dirty frames ≥ 15 per second; line
  hidden". The Graphics tile says "Low-FPS rule inactive / active".
* A workload value (older recordings carry one) never makes the line applicable. A hidden conditional line does not stretch the value axis.
  If the engine supplies no applicability, the line stays hidden.

**Findings and evidence.** `ResponsiveSplitPanel` (rules in the WPF-free, unit-tested `SplitLayout`):
* Side by side from 900 DIP (findings ≥ 520, evidence ≥ 360 DIP); stacked below (findings ≥ 160, evidence ≥ 320 DIP).
  The region fills the visible page height (at least 520 DIP), so the evidence is never pushed below the window; the page scrolls to it,
  and picking a finding with the keyboard or mouse scrolls the region into view.
* The splitter is a draggable, focusable thumb; the arrow keys move it by 24 DIP. Neither pane can go below its minimum.
  It resets when the page is opened again (not persisted).
* Each pane scrolls on its own. At the end of a pane, the mouse wheel continues to the page (`WheelChaining`), so scrolling never sticks.
* The selected finding stays pinned above the scrolling evidence (severity, state, title, Copy). The evidence is a tab stop,
  so PageUp/PageDown and the arrow keys work. Order: explanation, supporting measurements (Measurement · Observed · Threshold),
  decision rule and thresholds, possible contributing factors, recommended investigation, data limitations, technical details.
* Rows are 36 DIP or taller with a selection bar, a tooltip with the description, and wrapped (never truncated) titles.
  Findings are not virtualized: the list is bounded (about 20 active plus the last 50 recovered findings).
* Information and recovered findings keep neutral text; only the text of active warnings and critical findings is coloured. Every row also carries a severity icon (see *Severity icons*).

**Validation.** `DesktopValidation` runs on Windows over 2 themes × 4 sizes (1366 × 768, 1536 × 864, 1920 × 1080, 2560 × 1440) × 3
scales. Beyond the 0.4.1 checks it verifies, for Diagnostics: arrangement matches the width, both panes keep their
minimum size, the evidence can be brought into view, the pane scrolls independently and chains the wheel to the page at its end,
the splitter responds to the keyboard within its limits, the selection survives layout changes, and the page has no horizontal overflow.
The 125 % and 150 % cases **simulate the available DIP space; they do not change Windows DPI.** Real Windows scaling needs manual acceptance.

**Severity icons.** Every place that shows a severity or health state uses one vector icon, the `SeverityIcon` style in
`Themes/Controls.xaml`: the status line, each health tile, the findings table and the evidence header. The shapes are
`SeverityGeometry*` resources in `Themes/Foundations.xaml` on a 16 × 16 grid, drawn 1:1 at 16 DIP with the even-odd rule
(the symbol is a cut-out of the disc, or filled inside a ring). No icon font, emoji or Unicode glyph is involved, so the icons
render the same on every Windows installation. `Presentation/SeverityKind.cs` (WPF-free, unit-tested) maps states to icons.

| Icon | Used for | Shape | Colour token |
|---|---|---|---|
| Information | active information finding | disc with "i" | `AccentBrush` (blue) |
| Warning | active warning, Warning health | triangle with "!" | `WarningBrush` (amber) |
| Critical | active critical finding, Critical health | disc with "×" | `CriticalBrush` (red) |
| Healthy | Healthy state | disc with check | `SuccessBrush` (green) |
| Recovered | recovered finding | ring with check | `MutedBrush` (grey) |
| Insufficient data | no assessment yet | ring with bar | `MutedBrush` (grey) |

Each kind has its own shape, so severity is never carried by colour alone, and the text label ("Critical", "Warning",
"Recovered", ...) always stays beside the icon. The icon is decorative for screen readers because a WPF `Path` has no UI Automation
peer. The colours are the existing theme tokens (at least 3:1 against every surface in both themes, checked by a unit test).
The icon follows the engine's classification and never changes it. A recovered finding keeps its original severity text and
state column, with the Recovered icon.

The earlier 8 × 8 `StateMarker` square was only filled for Healthy, Warning and Critical, so information and recovered findings
showed as an empty square and warning and critical differed by colour only. It has been removed.

Future explanation panels (for example an optional local assistant) can bind to the same
`DiagnosticReport` and `DiagnosticsViewModel` selection without changing rules or layout.
None is included.

## Configuration

Thresholds live in `%LOCALAPPDATA%\EucLens\diagnostics.json`.

* **Settings › Diagnostic thresholds › Restore defaults** writes every value to that file.
* After editing the file, select **Reload from file**.
* Units are part of each property name (`RttWarningMs`, `CpuWarningPercent`,
  `AvailableRamWarningGiB`, `StandardWindowSeconds`, …).
* Omitted properties keep their defaults, and comments are allowed.
* Unknown property names are rejected, so typos are reported rather than ignored.

The file is validated before use:

* ranges for every value;
* warning < critical for each metric;
* short < standard < long window;
* critical available RAM below the warning level.

An invalid file is rejected, the defaults stay in use, and Settings lists the errors.
Applying thresholds clears the diagnostic history, so earlier evidence is not judged
by new rules.

```json
{
  "StandardWindowSeconds": 60,
  "RttWarningMs": 150,
  "RttCriticalMs": 300,
  "CpuWarningPercent": 90,
  "CpuCriticalPercent": 98
}
```

## Recording and export schema (0.4)

Changes are append-only. Every 0.2/0.3 column and JSON property is unchanged.

* **JSON:** each sample gains `Diagnostics`:
  * `Assessments`: domain, state, coverage, evidence, coverage note, window.
  * `ActiveFindings`: `ruleId:Severity`.
  * `Events`: transitions that occurred at this sample (`Detected`, `Escalated`,
    `Downgraded`, `Recovered`, `Expired`). Each event carries the full finding:
    rule ID, domain, severity, title, description, detected / last-confirmed /
    recovered timestamps, recovery reason, observation window, valid and total
    samples, evidence, decision rule, causes, actions and limitations.
* **CSV:** appended columns `diag_connection`, `diag_graphics`, `diag_guest`
  (state labels), `diag_coverage`, `diag_active_findings` and `diag_events`
  (e.g. `Detected connection.rtt.sustained Warning`). Full evidence is in the JSON export.
* Unchanged assessments and finding lists are shared between samples, so recordings do
  not duplicate them.
* 0.4.1 (append-only): each evidence item may carry `Threshold`, the configured limit it
  was compared with; it is `null` where the rule compares none. `DiagnosticReport.Inputs` (per-domain
  statistic, threshold and sample sufficiency) is used for presentation and is **not**
  written to recordings, so 24-hour captures do not grow.
* No user content, keystrokes, screenshots or application data are captured.

## Known limitations

* Thresholds are starting points and need tuning against real sessions. CI tests use
  synthetic data and do **not** prove diagnostic accuracy on a real VDI.
* Screen activity comes only from the Blast dirty-frame counter. Without it (no Horizon Blast counters, or an agent that
  does not publish it) low FPS is not judged at all, so a real frame-rate problem in such a session is not reported.
* Low FPS is judged only when the content changes at least 15 times per second. Slowly changing content delivered at an even
  lower rate (for example 10 changes per second delivered as 3 frames) is not reported. Dirty frames count changed frames,
  not how much of the screen changed: a small fast animation can count as content change.
* Several interpretations rest only on counter names, because Omnissa does not
  document them further:
  * dirty frames are treated as screen changes;
  * out queueing time is treated as outbound queueing (information only).
* Guest metrics cannot reveal hypervisor contention (CPU ready, ballooning, datastore latency).
* Per-process attribution is not part of the findings; use Task Manager or Resource Monitor as recommended.
* The findings/evidence split position is not remembered between visits, and it has no splitter between the status area and the page.
* Page layout, DPI behaviour and chart rendering are validated by the Windows CI layout
  matrix and screenshots: 1366 × 768 to 2560 × 1440, 100–150 % available-space simulation,
  light and dark. Real Windows scaling changes still need manual acceptance.
* Health uses the 60 s standard window. A new or reconnected session shows
  *Insufficient Data* for about 42–60 s.
