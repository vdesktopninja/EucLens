# Architecture

This page describes how EUC Lens is built today. For what each feature does, start with the [documentation index](../README.md); for how a version changed, see the [changelog](../../CHANGELOG.md).

## Projects and dependency direction

```text
EucLens.App          → Core, Providers, Diagnostics
EucLens.Providers    → Core
EucLens.Diagnostics  → Core
EucLens.Tests        → Core, Providers, Diagnostics (and links the platform-neutral App view models)
EucLens.Core         → .NET libraries only
```

| Project | Responsibility |
|---|---|
| **Core** | Immutable session snapshots with explicit metric availability; the recorder and its streaming storage (`Recording/`); the reader, statistics and incident analysis for saved recordings (`History/`); the deterministic findings analysis (`Anomalies/`); the monitoring coordinator, background settings, notification policy and single-instance guard (`Monitoring/`); application identity, release-version parsing, safe links and the manual update checker (`About/`, `Updates/`). Core has no WPF and no vendor dependency. |
| **Providers** | Measurement sources: `RealSessionMetricsProvider` combines `OmnissaBlastProvider` (Horizon Blast performance counters) with `HostMetricsCollector` (CPU, memory, disk, GPU and display, with optional NVIDIA NVML). `MockMetricsProvider` produces seeded synthetic data and is used only by tests and the `--smoke-test` harness. `CitrixHdxProvider`, `MicrosoftRdpProvider` and `NvidiaGpuProvider` are placeholder adapters that only report *Unavailable*; they are not registered by the application. |
| **Diagnostics** | The live rule engine (`DiagnosticIntelligenceEngine`) with independent Connection, Graphics and VDI Guest domains, a finding tracker (hysteresis and recovery), and the health evaluator. Rules are individually testable and registered, not hard-coded into the engine. |
| **App** | WPF views, view models (MVVM), themes and design tokens, the composition root, desktop services (dialogs, preferences, logging), the tray icon and application lifetime, and the Windows validation harness (`--smoke-test`). |
| **Tests** | Cross-platform xUnit tests. They link the platform-neutral App view models and commands and run without WPF. |

The composition root registers one `IMetricsProvider`, the real session. There is no Demo Mode: synthetic data can reach the application only through an internal hook used by the test harness, and real monitoring discards any sample marked synthetic.

## Collection and UI lifecycle

Startup validates dependency injection, restores local preferences, initializes the provider off the UI thread and starts one cancellable `PeriodicTimer` loop that samples once per second. Collection runs on a worker thread; only the WPF dispatcher thread updates bound properties, chart series and recorder state. Calls never overlap, and a configuration change discards results collected during the change.

A collection failure is logged locally, shown as stale or unavailable values and retried. Shutdown cancels and awaits the loop, finalizes a running recording, then disposes the provider and services. Timestamps are UTC `DateTimeOffset`; elapsed time uses a monotonic stopwatch; chart labels use local time.

Monitoring and recording are separate state machines owned by `MonitoringCoordinator` and the recorder (see [Background monitoring](../BACKGROUND_MONITORING.md)). While the window is hidden in the tray, a presentation gate skips UI work but keeps evaluating rules and recording. Live trend charts keep a rolling window of 120 samples; the Session Recorder's latency chart shows the latest 600.

## Recording and history

A running recording is streamed to disk by `RecordingWriter` as JSON Lines parts (`euclens.recording/1`) with rotation, compression of closed parts, a storage quota and age-based retention. The writer is bounded: when disk or queue limits are hit, samples are counted as lost and the loss is written into the recording, never hidden. A recording without a footer (crash, power loss) is readable up to its last flushed line and listed as *Interrupted*.

Session History reads recordings through `HistoryReader` and `HistoryLoader` (multi-part, compressed, de-duplicated, damaged-line tolerant) with a sample limit and peak-preserving downsampling for charts. The findings analysis in `Core/Anomalies` runs over the loaded recording: fixed rules with versions, thresholds and minimum durations, a per-recording robust baseline, episode detection and cross-metric timing correlations. It is deterministic, independent of the UI and cancellable. JSON and CSV exports of recordings, timelines and findings write to a temporary file and move it into place atomically; text from recordings is treated as untrusted, and CSV cells are protected against spreadsheet formula injection.

Formats and rules are documented in [Background monitoring](../BACKGROUND_MONITORING.md), [Session History](../SESSION_HISTORY.md) and [the analysis rules](../M1_ANOMALY_ENGINE.md).

## Availability and diagnostics

A `Metric` has a nullable numeric value, a unit, a status (Available, Unavailable, Stale, Failed) and an optional reason. Zero is a measurement; no source means Unavailable. Nothing is synthesized from a proxy, and GPU device-level data is not presented as session-level data without confirmed attribution.

Rules evaluate only when their required metrics are available and carry availability separately from Info, Warning and Critical severity. Sustained conditions use monotonic time windows with hysteresis and recovery. A known warning or critical finding determines the health of its domain even if another rule is unavailable; with no alert, a missing rule input makes the domain *Insufficient Data* rather than *Healthy*. Low frame rate is judged only while Blast dirty frames show the content changing at least as often as the threshold, so an idle or static screen never raises it. Details: [Diagnostics](../DIAGNOSTICS.md).

## Integration status

| Source | Status |
|---|---|
| Omnissa Horizon Blast | **Implemented** from local Blast Session and Imaging performance counters; see [Horizon Blast monitoring](../HORIZON_BLAST.md). Open: documented units for jitter, estimated bandwidth, poll/FBC rate and instantaneous byte counters; Encoder Type code mapping; negotiated transport. |
| Windows CPU, memory, disk, GPU, display | **Implemented**, protocol-independent; see [System, GPU and display monitoring](../SYSTEM_GPU_MONITORING.md). NVIDIA NVML is an optional backend, loaded from the driver's own files and never bundled. |
| Citrix HDX | **Not implemented.** Needs research first: supported sources, version and licensing matrix, session-to-client identity, counter definitions and units, permissions, and testing in real VDA sessions. |
| Microsoft RDP | **Not implemented.** Needs documented session, transport and graphics measurement interfaces, session mapping and privilege requirements across supported Windows versions. |

No guessed API signatures, performance-counter names, registry keys or WMI classes are used. A future adapter must implement availability detection, initialization, cancellable collection, error reporting and cleanup, and return explicit *Unavailable* values for anything unsupported.

## Packaging and privacy

The portable profile publishes a self-contained win-x64 single file with native library extraction and no trimming. Targeting Windows 10 build 19041 avoids falling back to legacy .NET Framework chart dependencies. The application registers no telemetry. Its only network client is the manual update checker (one HTTPS request to GitHub's public release list, started only by a button; see [About and updates](../ABOUT_AND_UPDATES.md)). Preferences, logs and recordings live under the standard user's `%LOCALAPPDATA%\EucLens` (the recordings folder is configurable). No elevation is requested.

The Windows CI workflow smoke-tests the published executable: every page, both themes, binding errors, typography, recording, export, tray, Session History and About. Interactive acceptance on real displays and real Horizon sessions is tracked in the [Windows acceptance checklist](../validation/WINDOWS_ACCEPTANCE.md).
