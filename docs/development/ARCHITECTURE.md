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

The composition root registers one `IMetricsProvider`, the real session. There is no Demo Mode: synthetic data can reach the application only through an internal hook used by the test harness, and real monitoring discards any sample marked synthetic. Views and view models have no vendor SDK dependency, and desktop services (file dialogs, preferences, logging) keep platform code out of view-model logic. The diagnostic engine takes its rules as `IEnumerable<IDiagnosticRule>`, so a new rule is registered, not wired into the engine. Providers own any vendor-specific concurrency and resources.

## Collection and UI lifecycle

Startup validates dependency injection, restores local preferences, checks provider availability, initializes the provider off the UI thread and starts one cancellable loop that samples once per second. Each sample is collected on a worker thread; its result is applied on the WPF dispatcher thread, which updates bound properties and chart series. Collections never overlap, a configuration change discards results collected during the change, and no code waits synchronously on the dispatcher or blocks on `.Result`. Recording files are written by a bounded background writer, not by the UI thread.

A collection failure is logged locally, shown as stale or unavailable values and retried. Shutdown cancels and awaits the loop, finalizes a running recording, then disposes the provider and services. Exiting the application while a recording is running asks for confirmation. Timestamps are UTC `DateTimeOffset`; elapsed time uses a monotonic stopwatch; chart labels use local time.

Monitoring and recording are separate state machines owned by `MonitoringCoordinator` and the recorder (see [Background monitoring](../BACKGROUND_MONITORING.md)). While the window is hidden in the tray, a presentation gate skips UI work but keeps evaluating rules and recording. Live trend charts keep a rolling window of 120 samples; the Session Recorder's latency chart shows the latest 600.

## Recording and history

A running recording is streamed to disk by `RecordingWriter` as JSON Lines parts (`euclens.recording/1`) with rotation, compression of closed parts, a storage quota and age-based retention. The writer is bounded: when the disk or the queue cannot keep up, samples are counted as lost, shown on the Session Recorder page and written into the recording as soon as writing works again. If writing keeps failing, the recording ends with the reason *storage error* and what was saved stays readable. A recording without a footer (crash, power loss) is readable up to its last flushed line and listed as *Interrupted*.

Session History reads recordings through `HistoryReader` and `HistoryLoader` (multi-part, compressed, de-duplicated, damaged-line tolerant) with a sample limit and peak-preserving downsampling for charts. The findings analysis in `Core/Anomalies` runs over the loaded recording: fixed rules with versions, thresholds and minimum durations, a per-recording robust baseline, episode detection and cross-metric timing correlations. It is deterministic, independent of the UI and cancellable. JSON and CSV exports of recordings, timelines and findings take a stable copy of the data, write to a temporary file in the destination folder and move it into place atomically, so a failed export never leaves a half-written file; text from recordings is treated as untrusted, and CSV cells are protected against spreadsheet formula injection.

Each sample carries a `SessionInfo.IsDemo` flag that marks synthetic data. Recordings made by 0.5.0 and earlier, which had a Demo Mode, keep that flag (and, for them, the historical workload name) and Session History labels them as demo data; current recordings are always real. Formats and rules are documented in [Background monitoring](../BACKGROUND_MONITORING.md), [Session History](../SESSION_HISTORY.md) and [the analysis rules](../M1_ANOMALY_ENGINE.md).

## Availability and diagnostics

A `Metric` has a nullable numeric value, a unit, a status (Available, Unavailable, Stale, Failed) and an optional reason. Zero is a measurement; no source means Unavailable. Nothing is synthesized from a proxy, and GPU device-level data is not presented as session-level data without confirmed attribution.

Rules evaluate only when their required metrics are available and carry availability separately from Info, Warning and Critical severity. Sustained conditions use monotonic time windows with hysteresis and recovery. A known warning or critical finding determines the health of its domain even if another rule is unavailable; with no alert, a missing rule input makes the domain *Insufficient Data* rather than *Healthy*. Low frame rate is judged only while Blast dirty frames show the content changing at least as often as the threshold, so an idle or static screen never raises it. Details: [Diagnostics](../DIAGNOSTICS.md).

## Integration status

| Source | Status |
|---|---|
| Omnissa Horizon Blast | **Implemented** from local Blast Session and Imaging performance counters; see [Horizon Blast monitoring](../HORIZON_BLAST.md). Open: documented units for jitter, estimated bandwidth, poll/FBC rate and instantaneous byte counters; Encoder Type code mapping; negotiated transport; other Blast channels. |
| Windows CPU, memory, disk, GPU, display | **Implemented**, protocol-independent; see [System, GPU and display monitoring](../SYSTEM_GPU_MONITORING.md). NVIDIA NVML is an optional backend, loaded only from the driver's own files and never bundled; NVML devices are linked to DXGI adapters only by a unique PCI match. Open: per-process vGPU attribution and validation across vGPU software versions. |
| Citrix HDX | **Not implemented.** Needs research first: supported sources, version and licensing matrix, session-to-client identity, counter definitions and units, permissions, and testing in real VDA sessions. |
| Microsoft RDP | **Not implemented.** Needs documented session, transport and graphics measurement interfaces, session mapping and privilege requirements across supported Windows versions. |

No guessed API signatures, performance-counter names, registry keys or WMI classes are used. A future adapter must implement availability detection, initialization, cancellable collection, error reporting and cleanup, and return explicit *Unavailable* values for anything unsupported.

## Packaging and privacy

The portable profile publishes a self-contained win-x64 single file with native library extraction and no trimming. Targeting Windows 10 build 19041 avoids falling back to legacy .NET Framework chart dependencies. The application registers no telemetry. Its only network client is the manual update checker (one HTTPS request to GitHub's public release list, started only by a button; see [About and updates](../ABOUT_AND_UPDATES.md)). Preferences, logs and recordings live under the standard user's `%LOCALAPPDATA%\EucLens` (the recordings folder is configurable). No elevation is requested.

Known platform limits: Windows x64 only (no ARM64, x86 or Server Core build), and real behavior in VDI with restricted or virtual graphics devices needs interactive acceptance. The Windows CI workflow smoke-tests the published executable: every page, both themes, binding errors, typography, recording, export, tray, Session History and About. Interactive acceptance on real displays and real Horizon sessions is tracked in the [Windows acceptance checklist](../validation/WINDOWS_ACCEPTANCE.md).
