# EUC Lens 0.5 — Intelligent Diagnostics

Status: M1, M2 and M3 are implemented on this branch; M4 (manual Horizon acceptance) has not been done and the release gate below is not met  
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

### M4: Manual Horizon acceptance — not started
- Use a real live Horizon Blast session plus multi-part historical recording.
- Verify evidence points to recorded values, interval selection, gap handling, and no false causal assertions.
- Verify no outbound connections and no dependence on elevated permissions.

## Deferred direction: optional on-device LLM

Only after deterministic findings are reliable and manually accepted: assess an **opt-in** local explanation layer. It must cite the deterministic evidence, never rewrite measurements, work without network access, degrade gracefully when no model is present, and require separate user approval before implementation.

## Release gate

Do not merge, tag or release 0.5 until unit tests, Windows CI, performance/memory checks, independent code review and manual Horizon acceptance pass.
