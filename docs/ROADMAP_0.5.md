# EUC Lens 0.5 — Intelligent Diagnostics

Status: **release candidate for 0.5.0.** M1, M2 and M3 are implemented and independently verified by automated tests and Windows CI; M4 (manual Horizon acceptance) is **accepted by the project owner** (see "M4 acceptance" below). Not merged, not tagged, not published.  
Branch: `feature/euc-lens-0.5-intelligent-diagnostics`  
Starting point: merged EUC Lens 0.4.4 (`f13075ad28d18e2e4c432c39003dc5574e4c8031`)

## Product goal

Turn recorded Horizon/VDI metrics into **explainable, evidence-based diagnostic observations**. The user must be able to see what was measured, when it happened, how certain the observation is, and which information is missing.

EUC Lens remains offline, portable, service-free, and usable without any AI model. This release is **not** an autonomous root-cause classifier.

## Scope for the first 0.5 increment

1. **Deterministic anomaly windows.** Scan historical channels for sustained and short-lived RTT spikes, packet loss, frame drops, unusually low FPS during actual activity, Blast queueing, guest CPU saturation, RAM pressure and disk stalls. Treat sample gaps and disconnected sessions as missing information, never zero.
2. **Evidence and coverage.** Each observation carries a UTC interval, source channel(s), sample count, available-data coverage, measured extrema, baseline/threshold, and relevant incident markers. Differentiate observed measurements from hypotheses.
3. **Conservative cross-metric correlation.** Surface co-occurrences such as RTT + packet loss, FPS degradation + queueing, or CPU saturation + low FPS. Describe them as correlated timing, **not proven causation**. Do not claim network, host, endpoint or GPU as root cause from metrics alone.
4. **Baseline-aware interpretation.** Use an explicit baseline from healthy samples where sufficient data exists, with documented robust thresholds and minimum window/sample sizes; otherwise say **Insufficient Data**. Prevent repeated duplicate alerts for one continuing issue.
5. **History-first interface.** Add a plain Windows-native, keyboard-accessible diagnostics summary within Session History. Clicking a finding opens the existing synchronized charts at the evidence window. Keep existing manual incident annotations independent and unchanged.
6. **Safe exports.** Export deterministic findings with UTC time, data coverage and evidence; protect CSV against spreadsheet formula injection and handle locale consistently.

## Non-goals

- No bundled or mandatory LLM, downloads, cloud calls, telemetry or network services.
- No root-cause guarantees, no fabricated metrics, no anomaly scoring from inactive/disconnected Blast data.
- No changes to recording durability or `euclens.recording/1` compatibility unless separately justified and reviewed.
- No redesign of the whole Diagnostics page or "AI dashboard" styling.
- No background service/installer/administrator privileges.

## Implementation order

### M1: Analysis core — implemented (`docs/M1_ANOMALY_ENGINE.md`)
- Pure, testable, cancellation-aware analyzer independent of WPF.
- Explicit rules, baselines, quality gates, evidence model and deterministic output ordering.
- Fixture tests for normal/idle/disconnected recordings, DST changes, missing counters, synthetic spikes, mixed events and no-data cases.

### M2: Session History integration — implemented (`docs/M2_FINDINGS_UX.md`)
- Small findings panel and selection synchronization with existing charts/timeline.
- Reuse current immutable history data and windowing/downsampling.
- Keyboard, screen-reader labels, 100/125/150% DPI and dark/light theme coverage.

### M3: Performance, regression and CI — implemented (`docs/M3_PERFORMANCE_RELIABILITY.md`)

Status: benchmarks of the analyzer up to 250,000 samples, a retention probe with leak-sensitivity tests, lifetime and stress tests of Session History, the optional findings export (CSV and JSON, formula-injection protection, atomic output) and Windows smoke sections for export, layout and lifetime. Item 6 of the scope ("Safe exports") is delivered here. Not yet done: anything in M4.
- Analyze 21,300-sample and 250,000-sample cases with bounded allocations and no UI blocking.
- Maintain existing 0.4.4 tests (463 passing), 5,005-event virtualization, 48-cycle Diagnostics soak, 12-cycle History retention and full Windows smoke.
- Add adversarial and golden-fixture tests for every new rule, including false-positive suppression.

### M4: Manual Horizon acceptance — accepted by the project owner

**Acceptance status.** The project owner reviewed the real-Horizon test results that were available and decided that no further testing is required before the 0.5.0 release. This is an **owner decision**, not a result of independent verification: the maintainers' tooling cannot connect to a Horizon session, and the detailed acceptance records are held by the owner, not in this repository. Nothing below should be read as "every item of the M4 checklist was demonstrated by the project's own tests".

What *was* verified independently by automated means (unit tests, Windows CI and the WPF smoke run): evidence values come from recorded samples, interval selection and chart navigation, gap and disconnect handling, wording that makes no causal claims, export safety and memory retention. For the third M4 item, a source search finds no HTTP, socket or web-client API in `src`, and the manifest requests `asInvoker`; no network capture was taken, so "no outbound connections" rests on that code search and the owner's acceptance. The original M4 intent is kept here for reference:

- Use a real live Horizon Blast session plus multi-part historical recording.
- Verify evidence points to recorded values, interval selection, gap handling, and no false causal assertions.
- Verify no outbound connections and no dependence on elevated permissions.

**Known limitations carried into 0.5.0** (also listed in `CHANGELOG.md`):

- **FPS thresholds are not calibrated.** The low-FPS rule (below 15 FPS, or below the recording's own baseline, only while display activity is established) uses the live engine's defaults and engineering judgement; no fleet data supports them.
- **Irregular sampling under heavy CPU load.** Collection runs once per second but can stretch when the machine is saturated. The analyzer treats a step longer than `max(5 s, 3 × median step)` as a gap (missing information, a new segment), and a stretched cadence reduces the samples a rule can use, so a rule may report "Insufficient Data" or end an observation as "recovery is not confirmed". Irregular sampling is tested with synthetic recordings only.
- **Recovery behaviour is not verified on real recordings.** `Recovered` requires the next valid sample to be normal with nothing missing in between; all coverage, disconnect and end-of-recording endings are reported as "not confirmed". This rule is covered by synthetic tests, not by observed real-world recoveries.
- **DPI.** The 100/125/150 % layouts are verified by simulating the effective width, not by switching the process to a real per-monitor DPI; no screen-reader pass was done.
- **Spreadsheets.** CSV formula protection is verified by rules and an RFC 4180 reader, not by opening the files in Excel or LibreOffice.
- **Baselines** are per recording; a short recording has no adaptive rules.

## Deferred direction: optional on-device LLM

Only after deterministic findings are reliable and manually accepted: assess an **opt-in** local explanation layer. It must cite the deterministic evidence, never rewrite measurements, work without network access, degrade gracefully when no model is present, and require separate user approval before implementation.

## Release gate

| Gate | Status |
|---|---|
| Unit tests | Passing (see the release candidate report; Windows CI is the record) |
| Windows CI and WPF smoke | Passing on the release candidate commit |
| Performance and memory checks | Done in M3 (`docs/M3_PERFORMANCE_RELIABILITY.md`) |
| Independent code review | Done in M3; no Critical findings, High findings fixed |
| Manual Horizon acceptance | **Accepted by the project owner**; not independently verified (see M4) |
| Merge, tag and release | **Not done.** Requires explicit owner authorization. |

## Release strategy

`v0.4.4` is the commit `f13075a` (the merge of PR #1 into `feature/background-monitoring-tray`). `feature/euc-lens-0.5-intelligent-diagnostics` is a linear, merge-free descendant of it, and contains every other remote branch. The repository has no `main` branch; its default branch `feature/euc-lens-mvp` is an ancestor of `v0.4.4`, 60 commits behind it. The release is therefore made from the 0.5 branch itself: the release commit is tagged `v0.5.0`, and the older branches can only be fast-forwarded to it (no force-push, no history rewrite, no commit lost).
