# EUC Lens

A free, MIT-licensed portable Windows desktop workspace for understanding remote
desktop session performance. EUC Lens uses original code and design; it does not
include Remote Display Analyzer code. Milestone 0.2 adds **real Omnissa Horizon
Blast monitoring** from local Windows performance counters, alongside the
original **Demo Mode**, whose identifiers and measurements are entirely synthetic.
The active data source is always shown in the header banner and sidebar.

## Current functionality

- Five working WPF pages: Dashboard, Diagnostics, Session Recorder, System
  Information and Settings, with a navigation sidebar and dark/light themes.
- **Real Session** mode (default): reads the `Horizon Blast Session Counters` and
  `Horizon Blast Imaging Counters` instances that belong to the Windows session
  EUC Lens runs in. See [Horizon Blast monitoring](docs/HORIZON_BLAST.md).
- **Demo Mode**: switch between simulated Citrix HDX, Omnissa Blast and Microsoft
  RDP sessions. Demo and real samples are never mixed; switching mode clears the
  live charts and stops an active recording.
- Healthy, high latency, low FPS, GPU bottleneck and bandwidth congestion scenarios.
- One-second asynchronous collection, metric cards, session health and LiveCharts2
  RTT/FPS charts with a rolling 120-sample window.
- Four sample diagnostic rules: latency, workload-aware FPS/drop correlation,
  GPU/VRAM pressure and bandwidth saturation. Results include severity, evidence,
  cause and recommended action. Missing measurements are explicitly Unavailable.
- Start/stop timestamped recording, elapsed time, sample count and a historical
  RTT chart (latest 600 samples). JSON and CSV export to a chosen local file.
- Local preferences, bounded application logs, no authentication, telemetry,
  service, database, cloud dependency or runtime internet requirement.

**Real Citrix, RDP and NVIDIA collection is still Unavailable.** Those adapter
classes intentionally return no vendor metrics. In Real Session mode, GPU, CPU,
RAM and disk cards show N/A because the Blast counter sets do not expose them. See [ARCHITECTURE.md](ARCHITECTURE.md)
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
