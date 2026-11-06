<div align="center">

# EUC Lens

**See what's really happening inside your remote desktop session.**

Open-source Windows diagnostics for EUC and VDI environments.

## [⬇ Download EUC Lens v0.5.1](https://github.com/vdesktopninja/EucLens/releases/tag/v0.5.1)

Portable · no installer · no server · no cloud · no telemetry

[![Latest release](https://img.shields.io/github/v/release/vdesktopninja/EucLens?label=release&color=2b6cb0)](https://github.com/vdesktopninja/EucLens/releases/latest)
![Platform: Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078d4)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
![Status: stable](https://img.shields.io/badge/status-stable-2f855a)

</div>

EUC Lens runs **inside** the Windows session you want to investigate and shows how that session is really performing: connection quality, display delivery and the health of the guest system. It combines live monitoring, evidence-based diagnostics, session recording and historical analysis, so an administrator can reproduce a problem, record it, and then see what the measurements actually show.

Everything happens locally on the machine where EUC Lens runs. There is nothing to deploy on a server, no account to create and no data leaves the computer.

> **Not code-signed yet.** The executable is not digitally signed, so Windows SmartScreen may show an *unknown publisher* warning the first time you start it. Compare the file's SHA-256 with the value on the release page before you run it. See [Privacy and security](#privacy-and-security).

<!--
SCREENSHOTS (pending real captures, see docs/images/README.md).
When the four sanitized screenshots exist in docs/images/, replace this comment with the block below.

## Screenshots

| | |
|---|---|
| ![EUC Lens Dashboard showing live round-trip time, frame rate, CPU, memory and session health](docs/images/dashboard.png) | ![EUC Lens Diagnostics page with Connection, Graphics and VDI Guest health and a findings list](docs/images/diagnostics.png) |
| **Dashboard.** Live session health at a glance. | **Diagnostics.** Findings with the evidence behind them. |
| ![EUC Lens Session Recorder with recording status, a latency chart and a table of saved recordings](docs/images/session-recorder.png) | ![EUC Lens Session History with synchronized charts, an incident timeline and grouped findings](docs/images/session-history.png) |
| **Session Recorder.** Record while you reproduce an issue. | **Session History.** Analyze a recording afterwards. |
-->

## Key features

### Live session monitoring

- Omnissa Horizon **Blast** session and imaging metrics where the session provides them: round-trip time, packet loss, frame rate, dropped frames, output queueing and bandwidth.
- Windows metrics in **every** session: CPU, memory, disk, GPU and display.
- An honest distinction between what was measured and what is not available. A missing value is shown as unavailable, never as zero, and the page says why.

### Evidence-based diagnostics

- Independent health for **Connection**, **Graphics** and **VDI Guest**, each with its data coverage.
- Findings backed by the actual measurements, with the observation window, the rule and its threshold, what is not known, and possible causes to check.
- Thresholds are visible and configurable. Short spikes, idle screens and missing data do not raise false alarms, and EUC Lens does not claim a root cause it cannot show.

### Session recording

- Record performance data while you reproduce an issue, and **mark incidents** the moment they happen, from the app or the tray menu.
- Optionally keep EUC Lens in the **system tray**: monitoring and recording continue while the window is hidden, and the tray icon always shows what the application is doing. Tray options are off by default.
- Recordings are streamed to disk as the session runs, with a bounded storage quota and retention. After a crash, a recording stays readable up to its last flushed second (flushes happen every few seconds).

### Session history and analysis

- Open any saved recording, including an interrupted one.
- Synchronized charts for connection, graphics and guest metrics on one time axis, with zoom, ranges and previous/next incident.
- An incident timeline and a performance timeline, with findings grouped by rule and each occurrence backed by its measured evidence.
- Export recordings and findings as **JSON** or **CSV**.

### Portable and privacy-conscious

- One self-contained Windows x64 executable. No installer and no separate .NET runtime.
- No administrator rights in normal supported use.
- No account, no telemetry and no cloud service. No AI.
- Update checking is manual and opt-in.

## How it works

1. **Start EUC Lens in the session you want to examine** (for example the Horizon virtual desktop). It samples once per second.
2. **Watch the Dashboard and Diagnostics.** Blast counters appear when the Horizon Agent publishes them for your session; Windows metrics are always available.
3. **Record while you reproduce the problem.** Press *Start recording*, use *Mark performance issue* when it happens, then stop.
4. **Analyze the recording in Session History.** Open it from the Session Recorder page and look at the findings, the timelines and the synchronized charts. Export what you need.

The application has these pages:

| Page | What it is for |
|---|---|
| **Dashboard** | Session health, the detected protocol and data availability, key measurements and live trends. |
| **Diagnostics** | Connection, Graphics and VDI Guest health, findings with evidence and recommended checks. |
| **Session Recorder** | Start and stop recording, mark incidents, and manage saved recordings. |
| **Session History** | Analyze a saved recording: charts, timelines, findings and exports. |
| **System Information** | The session, its graphics and display, system resources and the data sources in use. |
| **Advanced Metrics** | Every raw measurement with its unit, scope, status and source. |
| **Settings** | Theme, diagnostic thresholds, tray behavior, notifications, and recording folder, quota and retention. |
| **About** | Version, license, acknowledgments and the optional update check. |

## Quick start

1. Download `EucLens-win-x64.exe` from the [latest release](https://github.com/vdesktopninja/EucLens/releases/latest).
2. Verify it. In PowerShell: `Get-FileHash .\EucLens-win-x64.exe -Algorithm SHA256`, and compare the result with `SHA256SUMS.txt` on the release page.
3. Copy it to a writable folder in the session you want to monitor and run it as a **standard user**. If Windows warns about an unknown publisher, that is the missing code signature mentioned above.
4. Open **Session Recorder**, press **Start recording**, reproduce the problem, press **Mark performance issue** when it happens, and **Stop recording**.
5. Choose the recording in *Saved recordings* and press **Analyze recording**.

The first start unpacks its native libraries into the user's temporary .NET directory, so the executable is portable but not a zero-write program. Settings, logs and recordings are stored under `%LOCALAPPDATA%\EucLens`.

## Supported environments and known limitations

**Supported**

- **Windows x64 with a graphical desktop**: Windows 11, Windows 10 version 2004 (build 19041) or later, and Windows Server 2022/2025 with Desktop Experience are the intended targets.
- **Omnissa Horizon (Blast)** sessions: metrics come from the performance counters that the Horizon Agent publishes inside the virtual desktop.
- **Any other Windows session**: CPU, memory, disk, GPU and display metrics work, and the protocol is reported as *Not detected*.

**What has been tested.** Every release is built and tested on Windows Server 2025 in CI (automated tests plus a launch-and-drive test of the published executable). The Horizon Blast behavior has been checked by the maintainer on real Horizon sessions; Horizon Agent and client versions were not recorded, so no version matrix is claimed. Real per-monitor display scaling at 125% and 150% and screen readers have not been tested on real hardware.

**Known limitations**

- **Native Citrix HDX and Microsoft RDP protocol counters are not supported.** In those sessions EUC Lens shows Windows metrics only. It has not been validated in Citrix or RDP environments.
- No Windows ARM64, x86 or Server Core build.
- Thresholds are engineering defaults, not calibrated on fleet data. Slowly changing content that is also delivered slowly is not reported.
- Findings show what a recording measured and when. They do not identify a root cause.
- Some Blast counters have no documented unit and are shown as raw values (labelled *unverified*). Findings that need a counter the Horizon Agent does not publish report *Insufficient Data*.
- The executable is not code-signed.

See the [Horizon Blast notes](docs/HORIZON_BLAST.md) for the exact counters and their status, and the [release notes](https://github.com/vdesktopninja/EucLens/releases/latest) for what was and was not verified.

## Privacy and security

- **Local only.** Monitoring, recording and analysis run on your computer. EUC Lens has no server component, no account and no telemetry.
- **No network requests unless you ask.** The only network code is the *Check for updates* button on the About page. It makes one HTTPS request to GitHub's public release list, sends no identifier and nothing from your session, downloads nothing and replaces nothing. *Download update* opens the release page in your browser.
- **Your data stays with you.** Recordings, settings and logs are files in `%LOCALAPPDATA%\EucLens` (recordings can be moved to another folder in Settings). Recordings contain performance measurements and the session identifiers Windows and Horizon report, so treat them as diagnostic data before sharing them.
- **No elevation.** EUC Lens runs as a standard user. Windows lets local users read performance counters by default; if your environment restricts them, add the user to *Performance Monitor Users*.
- **Verify your download.** Each release lists the SHA-256 of the executable. A checksum proves the file is the one that was published; it does not prove who published it, because the executable is not yet code-signed.
- To report a security concern, open a GitHub issue that does not include sensitive details, or get in touch through [vdesktop.ninja](https://vdesktop.ninja).

## Documentation

| Topic | Document |
|---|---|
| All documentation | [docs/README.md](docs/README.md) |
| Session Recorder | [docs/SESSION_RECORDER.md](docs/SESSION_RECORDER.md) |
| Session History and incident timeline | [docs/SESSION_HISTORY.md](docs/SESSION_HISTORY.md) |
| Background monitoring, tray and recording files | [docs/BACKGROUND_MONITORING.md](docs/BACKGROUND_MONITORING.md) |
| Diagnostics and findings | [docs/DIAGNOSTICS.md](docs/DIAGNOSTICS.md) and [analysis rules](docs/M1_ANOMALY_ENGINE.md) |
| Horizon Blast metrics | [docs/HORIZON_BLAST.md](docs/HORIZON_BLAST.md) |
| System, GPU and display monitoring | [docs/SYSTEM_GPU_MONITORING.md](docs/SYSTEM_GPU_MONITORING.md) |
| About, updates and release naming | [docs/ABOUT_AND_UPDATES.md](docs/ABOUT_AND_UPDATES.md) |
| Architecture | [docs/development/ARCHITECTURE.md](docs/development/ARCHITECTURE.md) |
| Validation and manual acceptance | [docs/validation/](docs/validation/) |
| Changelog | [CHANGELOG.md](CHANGELOG.md) |

## Building from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the version is pinned in `global.json`). The application is WPF, so running it requires Windows; building and the platform-neutral tests also work on Linux and macOS.

```sh
dotnet restore EucLens.sln
dotnet build EucLens.sln -c Release
dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release
```

Run from source on Windows:

```sh
dotnet run --project src/EucLens.App/EucLens.App.csproj
```

Publish the portable Windows x64 executable (this also works as a cross-publish from Linux or macOS):

```sh
dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64
```

The result is `artifacts/win-x64/EucLens.exe`: self-contained, single file, trimming disabled for WPF compatibility. A local build records no commit in its About page, and builds are not byte-for-byte reproducible, so your executable's hash will differ from the release's. The Windows [GitHub Actions workflow](.github/workflows/windows.yml) builds with warnings as errors, runs the tests, publishes, and launches the published executable through every page, both themes, recording, export, tray and Session History checks.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first, work on a branch and open a pull request. EUC Lens never invents measurements: new metrics need a documented source, a unit and an explicit way to say *unavailable*.

## License and credits

EUC Lens is released under the [MIT License](LICENSE) and uses original code and design.

Created and maintained by **Dominik Jakubowski**, [VDI Ninja](https://vdesktop.ninja).

The executable bundles the .NET runtime and open-source libraries (LiveCharts2, SkiaSharp, HarfBuzzSharp, OpenTK and others), each under its own license. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); the verbatim license texts are in [`licenses/`](licenses/) and in the application's About page. Omnissa and Horizon are trademarks of their respective owners; EUC Lens is an independent project and is not affiliated with or endorsed by them.
