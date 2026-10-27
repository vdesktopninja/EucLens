# EUC Lens

A free, MIT-licensed portable Windows desktop workspace for understanding remote
desktop session performance. EUC Lens uses original code and design; it does not
include Remote Display Analyzer code. **Real Session** mode combines Omnissa Horizon
Blast counters (0.2) with protocol-independent Windows CPU, memory, disk, GPU and
display measurements (0.3). The original **Demo Mode**, whose identifiers and
measurements are entirely synthetic, is still available.
The active data source is always shown in the header banner and sidebar.

## Current functionality

- Five working WPF pages: Dashboard, Diagnostics, Session Recorder, System
  Information and Settings, with a navigation sidebar and dark/light themes.
- **Real Session** mode (default): reads the `Horizon Blast Session Counters` and
  `Horizon Blast Imaging Counters` instances that belong to the Windows session
  EUC Lens runs in. See [Horizon Blast monitoring](docs/HORIZON_BLAST.md).
- Protocol-independent host monitoring in Real Session mode: machine-wide CPU
  (with history chart), physical RAM, disk throughput, GPU adapters (DXGI) with
  utilization, encode/decode and dedicated/shared memory from Windows GPU
  counters, optional NVIDIA NVML, and the session's display topology. See
  [System, GPU and display monitoring](docs/SYSTEM_GPU_MONITORING.md).
- The detected protocol is shown in Real Session ("Not detected" with the reason
  when no Blast session exists). Demo protocol selection only appears in Demo Mode.
- **Advanced Metrics** page: every raw measurement with category filter, search,
  value, unit, scope (machine-wide or this session), status, source and sample time.
- **Demo Mode**: switch between simulated Citrix HDX, Omnissa Blast and Microsoft
  RDP sessions. Demo and real samples are never mixed; switching mode clears the
  live charts and stops an active recording.
- Healthy, high latency, low FPS, GPU bottleneck and bandwidth congestion scenarios.
- One-second asynchronous collection, metric cards, session health and LiveCharts2
  RTT/FPS charts with a rolling 120-sample window.
- Offline, rule-based **diagnostic intelligence** with independent **Connection**,
  **Graphics** and **VDI Guest** health (Healthy, Warning, Critical or Insufficient Data,
  each with coverage). Findings carry evidence, the observation window, the decision rule,
  possible causes, recommended actions and limitations. Sustained conditions use
  monotonic-time windows with hysteresis and recovery; brief spikes, idle desktops and
  software rendering do not raise warnings. Thresholds are configurable and validated.
  The Diagnostics page (0.4.1) shows:
  * a status line and health overview with key measurements and thresholds;
  * an evidence-coverage indicator;
  * RTT, FPS, CPU and packet-loss trends for 30/60/120 s;
  * a sortable, filterable findings table beside a resizable evidence and recommendations panel (copyable), each pane scrolling on its own.

  0.4.2 refines readability only: a typography hierarchy, a compact status line, taller trend charts, low-FPS thresholds drawn only while the engine verifies they apply, and one vector severity icon (information, warning, critical, healthy, recovered) beside every severity text.

  0.4.3 adds background operation: a notification-area icon showing what EUC Lens is really doing (idle, monitoring, recording, warning, paused),
  optional minimize/close to tray, monitoring and recording that continue while the window is hidden, long recordings streamed to disk with rotation,
  quota and retention, a single instance per user session, optional privacy-safe notifications and the new application icon. See
  [Background monitoring](docs/BACKGROUND_MONITORING.md).

  See [Diagnostic intelligence](docs/DIAGNOSTICS.md).
- **Session history and incident timeline (0.4.4)**: *Mark Performance Issue* in the tray menu (one click, no window needed) or on the Session Recorder page saves a
  timestamped incident marker into the running recording, confirmed only once it is on disk. Later, *Analyze recording* opens the saved session on the new
  **Session History** page: overview (complete, interrupted or in progress), a timeline of every recorded event with its own icon and label, synchronized charts of
  connection, graphics and guest metrics on one time axis (zoom, ranges, previous/next incident, P50/P95/maximum, valid and missing counts), peak-preserving
  reduction for multi-hour recordings, and for each incident what the recording shows around it, described deterministically and without naming a cause.
  Administrators can annotate incidents (stored beside the recording; the recording is never rewritten). See [Session history](docs/SESSION_HISTORY.md).

- **Intelligent findings (0.5.0)**: when a saved recording is opened in Session History, a deterministic, offline analysis lists what the recording shows:
  sustained and short-lived RTT, packet-loss, frame-rate, queueing, CPU, memory, GPU and disk observations, each with its UTC interval, the measured values, the threshold
  or baseline used, the data coverage and what is not known, plus conservative "correlated timing" links between observations. Selecting a finding opens the synchronized
  charts at its evidence window and marks the peak. Missing measurements are never treated as zero. Findings can be exported to CSV or JSON (`euclens.findings/1`) with
  spreadsheet formula protection. The findings do not name a root cause. See the [changelog](CHANGELOG.md), [the analysis rules](docs/M1_ANOMALY_ENGINE.md) and
  [the roadmap and known limitations](docs/ROADMAP_0.5.md).

- Start/stop timestamped recording, elapsed time, sample count and a historical
  RTT chart (latest 600 samples). JSON and CSV export to a chosen local file.
- Local preferences, bounded application logs, no authentication, telemetry,
  service, database, cloud dependency or runtime internet requirement.

**Citrix HDX and Microsoft RDP protocol metrics are not implemented.** Host
CPU, memory, disk, GPU and display metrics work in those sessions too, but the
protocol is reported as "Not detected". See [ARCHITECTURE.md](ARCHITECTURE.md)
for integration research requirements.

## Platforms

Windows x64 with a graphical desktop: Windows 11, Windows 10 Enterprise/LTSC
build 19041 or later, and Windows Server 2022/2025 with Desktop Experience are the
intended targets. The chart dependencies require the Windows 10 19041 targeting
pack. Earlier Windows builds, Server Core, ARM64 and x86 are outside this MVP.
Actual compatibility and graphics behavior require Windows acceptance testing;
compilation alone is not a certification for all these environments.

Development uses .NET 10 and WPF, MVVM, Microsoft dependency injection/logging,
LiveCharts2 and xUnit. Linux/macOS can run business tests and cross-compile; WPF
execution requires Windows.

## Build and run

Install the .NET 10 SDK. The SDK selection is in `global.json`.

```sh
dotnet restore EucLens.sln
dotnet build EucLens.sln -c Release
dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release
```

On Windows, run the source application:

```sh
dotnet run --project src/EucLens.App/EucLens.App.csproj
```

Publish the portable Windows x64 application (also works as a Linux cross-publish):

```sh
dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64
```

Copy `artifacts/win-x64/EucLens.exe` to a writable folder on the target Windows
computer and launch it inside the VDI session as a standard user. No installer
or separately installed .NET runtime is needed. The self-contained EXE bundles
WPF and native chart libraries; native files extract to the user's temporary
.NET bundle directory on first launch. It is portable distribution, not a
zero-write executable. Trimming is disabled for WPF/reflection compatibility.
See Microsoft's [single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
and the [LiveCharts2 WPF installation guide](https://livecharts.dev/docs/wpf/2.0.0/overview.installation).

The Windows GitHub Actions workflow restores, builds with warnings as errors,
tests, publishes, asserts a single EXE, runs a WPF startup/binding smoke test
and uploads the application plus test evidence. A CI artifact is created only
when the workflow runs; local cross-publishing cannot verify Windows execution. Windows CI has now
validated startup, all five views, sampling, themes, recording and exports;
see [validation evidence](docs/VALIDATION.md).

## Using EUC Lens

1. On Dashboard, choose the data source (Real Session or Demo Mode) and the
   workload. In Demo Mode, also choose a protocol and scenario. The first sample
   arrives during startup; updates continue once per second on every page.
2. Open Diagnostics to inspect the four sample rules. Idle/static content and
   office workloads do not generate low-FPS alerts merely for low FPS.
3. Open Session Recorder, start recording, exercise scenarios, then stop and
   export JSON or CSV. Each sample retains its timestamp, data source, protocol,
   demo scenario and workload. Real samples also carry every Blast counter's
   value, unit, status, raw value and counter path. Export during recording saves a stable snapshot of samples so far.
4. Use Settings to change the theme immediately and save preferences for the
   next launch, including the data source.

## Storage and limitations

- Recording samples are in memory until exported; closing discards them, with a
  confirmation when there are unexported samples. A new recording requires
  confirmation before replacing previous samples. No automatic disk journal or
  recording import/playback exists in this milestone.
- Recording stops at 86,400 samples (24 hours at nominal 1 Hz). Export contains
  all samples; the historical chart shows only the latest 600.
- Preferences and capped logs live under `%LOCALAPPDATA%\EucLens`. Logs use one
  approximately 1 MB file plus a previous file. Export paths are user-selected.
- JSON retains complete snapshots and metric status/reasons, plus per-counter
  source, timestamp, raw value and error for real samples. CSV exports all
  numeric metrics, a `data_source` column and `blast_*` value/unit/status/raw/source
  columns, uses invariant decimal formatting and explicit
  `Unavailable`/`Stale`/`Failed` cells, and escapes string fields. It is not a JSON
  round-trip format.
- Diagnostics are single-sample illustrative thresholds, not vendor guidance or
  definitive root-cause analysis. CPU/encoding/network-instability rules and
  sustained-window evaluation remain planned. Workload context is user-selected.
- Published builds are unsigned. DPI, chart rendering, file dialogs and remote
  GPU behavior need the [Windows acceptance checklist](docs/WINDOWS_ACCEPTANCE.md).

## Roadmap

1. Validate the EXE/UI in Windows VDI sessions, including 100–200% DPI,
   software rendering, theme contrast, exports and accessibility.
2. Add recording import/playback and optional streaming storage for long captures.
3. Research each remaining vendor's (Citrix, RDP, NVIDIA) documented, supported,
   non-admin measurement sources; implement one adapter at a time.
4. Add configurable, time-window diagnostics for CPU, encoding, dropped frames
   and network instability with measured workload context.
5. Expand packaging/platform coverage, performance benchmarks and release signing.

See [CONTRIBUTING.md](CONTRIBUTING.md), [ARCHITECTURE.md](ARCHITECTURE.md) and
[VALIDATION.md](docs/VALIDATION.md) for development and validation details.
