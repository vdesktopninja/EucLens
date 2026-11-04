# Changelog

All notable changes to EUC Lens are listed here. The application version is set in one place, `Directory.Build.props`.

## 0.5.1 — Real monitoring only

Status: release candidate, not released. Stable non-AI line (from 0.5.0); none of the 0.6 work. The recording format is unchanged (`euclens.recording/1`). Details and the audit: [docs/STABLE_RELEASE_0.5.1.md](docs/STABLE_RELEASE_0.5.1.md).

- **No Demo Mode.** The data-source, demo protocol and demo scenario selectors are removed. EUC Lens monitors the real session only: Horizon Blast counters where present, protocol-independent Windows metrics everywhere. Preferences saved by earlier versions can no longer select Demo Mode (only the theme is restored), and real monitoring discards any synthetic sample. The synthetic generator is used only by the automated tests and the `--smoke-test` harness.
- **No workload selection.** The frame-rate rules follow a measurement: low FPS is judged only while Blast dirty frames show the content changing at least as often as the low-FPS threshold (15 per second). A static, idle or slowly changing screen is never a finding, and without the dirty-frame counter low FPS is not judged. No threshold value was added or changed.
- **One low-FPS policy.** Session History's low-frame-rate observation (`gfx.fps.low`, now rule version 2) uses the same gate as live Diagnostics: the content must change at least 15 times per second. Before, the same recording could be a Session History warning for typing or slowly changing content that live Diagnostics called expected. Dropped frames are unchanged. See [docs/STABLE_RELEASE_0.5.1.md](docs/STABLE_RELEASE_0.5.1.md).
- **Flaky test fixed.** The Session History test that reopened a recording waited a fixed 200 ms; it now waits for the reload itself.
- **Session History as a diagnostic workspace.** Presentation only (no change to metrics, rules, thresholds, recording format, identifiers, retention, privacy or security). A compact overview of eight tiles (date, duration, protocol, status, samples, data coverage, findings, incidents) replaces the large metadata blocks; secondary facts and the recording ID are in a collapsed details area. A new **performance timeline** draws every finding on a Connection, Graphics or VDI Guest track at its recorded interval, with severity, separate lanes for overlapping findings, gaps in a row of their own, and click and Ctrl+arrow selection that stays in sync with the list and the charts. Findings of one rule are **grouped** under one expandable line (severity, count, key value, total affected time as the union of the intervals, every occurrence separately selectable and exported); the engine and the identifiers are untouched. Filters are small, exports are one labelled area. See [docs/SESSION_HISTORY_WORKSPACE.md](docs/SESSION_HISTORY_WORKSPACE.md).
- **Performance findings as master and details.** Presentation only. Each list line shows the title and severity, the key measurement in the value font and the affected duration; repeated findings stay grouped under one line, every occurrence still its own observation. The details pane shows the key measurement, the duration and the time as tiles, the engine's own explanation and exact key values, a compact list of the individual occurrences, and four closed disclosures for evidence, threshold and baseline, limitations and technical detail (identifiers, rule and version). While nothing is selected the pane previews the most severe finding, clearly labelled and without moving the charts; one button makes it a selection. Side by side from 900 DIPs, stacked below. Exports are quiet links. The leak-probe self-test no longer fails when something else releases memory mid-measurement.
- **Clear indicators.** Dashboard and Settings show the detected protocol and the data availability; the banner says "Windows metrics only" when the session has no Horizon Blast counters, instead of claiming them.
- **Compatibility.** Recordings and exports of 0.5.0 and earlier open and keep their historical workload and demo values; new samples export `Unavailable` in the unchanged `workload` column.
- **CI.** Pushes to `release/**` are validated. The portable EXE is uploaded only for manual runs (3 days); the launch check, the credential scan and the job-log summary from the 0.6 line are included.

## 0.5.0 — Intelligent findings in Session History

Status: release candidate. The recording format is unchanged (`euclens.recording/1`): recordings made by 0.4.3 and 0.4.4 open in 0.5.0 (covered by tests), and 0.5.0 writes the same format. Opening a 0.5.0 recording in an older version was not tested. The existing JSON and CSV exports of the Session Recorder and of the Session History timeline keep their columns and structure.

0.5.0 adds an offline analysis of a saved recording. It reports what was measured, when, how complete the data was and what is missing. It does not name a root cause. It needs no network, no model, no service and no administrator rights.

### Deterministic anomaly detection

- The analyzer scans a saved recording and reports **observations**: RTT, packet loss, dropped frames, low FPS during real display activity, Blast output queueing, bandwidth pressure, guest CPU, memory, GPU and disk (throughput and latency). Fourteen rules, each with a version, a fixed threshold and a minimum duration and sample count.
- Rules that can use a baseline compare against the recording's own robust baseline (trimmed median and median absolute deviation). A recording too short or too noisy for a baseline gets the absolute threshold only and says so.
- Low FPS and dropped frames are judged only while display activity is established from the recorded dirty-FPS counter. A static screen is not a finding.
- The same recording always produces the same findings, in the same order, with the same identifiers. The analysis is cancellable and independent of the user interface.

### Intelligent findings

- Each observation carries a UTC interval, the source metrics, the number of valid and matching samples, coverage, the measured minimum, P50, P95 and maximum, the time of the worst value, the threshold or baseline that applied, and its limitations.
- A short peak is a *spike*, a repeated condition an *intermittent* one, and a sustained condition *sustained*. One continuing condition is one observation, not a series of alerts.
- An observation ends as **Recovered** only when the next valid sample is normal with nothing missing in between. A gap, missing values, a disconnect or the end of the recording end it as "recovery is not confirmed".

### Cross-metric timing correlations

- Five patterns link observations that overlap in time: RTT with packet loss, graphics with connection, low FPS with guest CPU, several guest resources, and an observation next to an availability event.
- A correlation states that the intervals coincided ("correlated timing"). It does not state a cause, and tests check every generated sentence for causal wording. A common interval is shown only when every component was present in it.

### Evidence-first Session History

- A findings panel in Session History: summary, data coverage, filters by severity and domain, and a list of findings with their key values visible. A detail pane shows the evidence, the rule, what is not known, and the supporting metrics.
- Keyboard operation and screen-reader names for the findings controls, in light and dark themes. Manual incident annotations stay separate and unchanged.

### Chart navigation and peak highlighting

- Selecting a finding opens the existing synchronized charts at its evidence window, marks the interval and the worst value, and emphasizes the metric's chart. *Show full recording* (or Escape) returns to the whole recording.

### Data coverage and missing measurements

- A missing value is never read as zero. Samples outside a plausible range (for example a negative RTT or a percentage above 100) are excluded and counted.
- Gaps and disconnected stretches split a recording into segments, and an observation never crosses a segment boundary. Connection and graphics values are not used between a recorded disconnect and the next reconnect.
- A metric with too little data is reported as **Insufficient Data** instead of being judged.

### CSV and JSON findings export

- Two optional, user-initiated buttons: **Export findings (CSV)** and **Export findings (JSON)**, schema `euclens.findings/1`. They are separate from the existing exports.
- CSV: one row per observation and per correlation, a fixed set of 44 columns, UTC ISO 8601 times, the recorded units, UTF-8 with a byte-order mark, comma separated. Unavailable values are empty cells, never 0, NaN or Infinity.
- JSON: a versioned document with observations, correlations, coverage and rule evaluations. Unavailable values are `null`.
- Output is deterministic and contains no machine or folder names.

### Security and reliability

- **CSV formula injection.** A text cell that begins with `=`, `+`, `-` or `@` (also in full-width forms, or after whitespace, control or invisible characters, or beginning with a tab or line break) gets a leading apostrophe. Generated numbers are never prefixed. The existing Session History timeline CSV now uses the same guard; its columns are unchanged.
- **Safe file output.** An export is written to a temporary file in the target folder, flushed to disk and moved into place, so a failure or a cancel leaves the old file or none. Export refuses to write onto a recording's own files (parts, `.meta.json`, `.annotations.json`).
- **Untrusted text.** Recorded strings are treated as untrusted: control characters are replaced, unpaired surrogates become U+FFFD, and a text cell is cut at 32,000 characters.
- Chart refresh, incident analysis and the export no longer fail silently: errors are logged and reported in the panel. Export buttons are disabled while a new analysis runs.

### Performance improvements

- The analyzer is linear in the number of samples. Measured on a Windows runner (Release): about 8 ms for 21,300 samples, 16 ms for 40,795 and 111 ms for 250,000, retaining nothing between runs. Nine adversarial 250,000-sample recordings (mostly missing data, irregular sampling, thousands of events or gaps, many spikes) finish in under 115 ms, and a cancel is honoured within about 21 ms. The numbers are from `docs/M3_PERFORMANCE_RELIABILITY.md` and depend on the machine.
- Opening and leaving Session History repeatedly releases the page, its findings and its chart objects: in a 12-visit Windows run the heap stayed flat (10.6 MiB before, 10.4 MiB after).
- The memory-retention test was replaced by a stronger one that is shown to detect planted leaks of 16 KiB per run.

### Version

- The application version is 0.5.0 (assembly, executable file properties and the version stamped into new recordings). Windows CI checks that the executable carries the version from `Directory.Build.props`.

### Known limitations

- **FPS thresholds are not calibrated.** The low-FPS rule (below 15 FPS, or below the recording's own baseline, during display activity) uses defaults chosen by engineering judgement, not fleet data. All thresholds are defaults.
- **Irregular sampling under heavy CPU load.** If collection stretches because the machine is saturated, steps longer than the gap threshold become gaps, rules may have too few samples, and some observations end as "recovery is not confirmed". Irregular sampling is tested with synthetic recordings only.
- **Recovery has not been verified on real recordings.** The recovery rule is covered by synthetic tests.
- **Real-DPI testing is limited.** The 100, 125 and 150 % layouts were verified by simulating the effective window width, not by changing the process to a real per-monitor DPI. No screen-reader pass was done.
- **Spreadsheet behaviour** of the exported CSV was checked against rules and an RFC 4180 reader, not in Excel or LibreOffice.
- **Horizon acceptance.** Manual acceptance with real Horizon sessions was accepted by the project owner. It was not independently verified by this project's automated tests, and its detailed records are not part of the repository.
- Recordings made by Horizon without the dirty-FPS counter give "Insufficient Data" for graphics rules. Disk latency, queue length, per-direction loss and jitter are not in the recorded channels today, so their rules report "Insufficient Data" on real recordings.
- The baseline is per recording: a short recording has no adaptive rules. A tolerance-based correlation can link two unrelated problems that happened to coincide, and its text says so.
- A recording still being written is analyzed as a snapshot; reopen it to analyze it again.

## 0.4.4

Session History and incident timeline, manual incident markers, recording hardening. See `docs/SESSION_HISTORY.md` and `docs/HARDENING_0.4.4.md`.
