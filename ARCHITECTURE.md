# Architecture

## Projects and dependency direction

```text
EucLens.App          → Core, Providers, Diagnostics
EucLens.Providers    → Core
EucLens.Diagnostics  → Core
EucLens.Tests        → Core, Providers, Diagnostics
EucLens.Core         → .NET libraries only
```

`Core` contains immutable session snapshots, explicit metric availability,
provider lifecycle and demo configuration contracts, diagnostic contracts and
an in-memory local recorder with atomic JSON/CSV exports. `Providers` contains
the seeded mock simulator and four unavailable vendor adapter boundaries.
`Diagnostics` contains the engine and individually testable rules.
`App` owns WPF views, viewmodels, resource themes, desktop file dialogs,
preferences, logging, charts and the composition root. `Tests` is cross-platform. It links the actual platform-neutral app viewmodel,
commands and desktop contracts for export-race regression tests, referencing
chart DTOs without executing WPF or native rendering.
There are no vendor SDK dependencies in views or viewmodels.

The app's DI composition root chooses `IDemoMetricsProvider` for this milestone.
Future production collection should depend on `IMetricsProvider`; demo-specific
protocol/scenario controls stay specific to the demo workspace. The diagnostic
engine accepts `IEnumerable<IDiagnosticRule>`; new rules require registration,
not changes to the engine. `ISessionRecorder` permits replacement with a streaming
implementation later. Desktop services isolate platform dialogs/preferences
from viewmodel logic.

## Collection and UI lifecycle

Startup validates DI, restores local preferences, checks provider availability,
initializes it off the UI thread, then starts a cancellable `PeriodicTimer`
collection loop. Initialization and collection run on a worker thread even when
a provider returns synchronous work inside its task. Await resumes on the WPF
dispatcher; only that thread updates bound properties, series and recording
state. Export completion records the generation and captured sample count so an
old export cannot mark a new recording as saved; concurrent successful exports
only advance the current recording’s saved watermark. No synchronous dispatcher wait or `.Result` is used. Calls never overlap.
Configuration has a generation number; results collected during a configuration
change are discarded. Protocol changes reset the simulated connected time.
Live charts reset when protocol, scenario or workload changes; recordings keep
individually identified samples across changes.

Collection failures log locally, report stale values and set health Unavailable;
the loop retries. Shutdown cancels and awaits collection, then asynchronously
disposes provider/DI resources. Closing confirms possible loss of unexported
samples. Recording elapsed time uses a monotonic stopwatch, not wall-clock
differences; samples use UTC `DateTimeOffset` timestamps. Chart labels use local
time. Live buffers are limited to 120 points; recorded chart to 600; recorder to
86,400 samples. Export takes a stable copy before async I/O and atomically
replaces the destination after writing a same-directory temporary file.

The recorder's mutation contract is single-owner (the dispatcher); it is not a
concurrent queue. Providers own any vendor-specific concurrency/resources.
The mock provider serializes configuration/collection/disposal with a lock.

## Availability and diagnostics

A `Metric` has a nullable numeric value, unit and optional missing-data reason.
Zero is an available measurement; no source means Unavailable. No unavailable
vendor value is synthesized from a system proxy. `SessionInfo.IsDemo` and the
persistent UI banner identify synthetic snapshots; JSON exports preserve this
flag and the optional demo scenario.

Rules evaluate only when required metrics are available; bandwidth ratios also
require positive capacity. Rule results carry availability separately from
Info/Warning/Critical severity. A known critical/warning finding determines
health even if another rule is unavailable; absent any alert, any missing rule
input makes overall health Unavailable rather than Healthy. An empty engine is
Unavailable. Low FPS alerts require interactive graphics, FPS below 20 and
frame drops at least 3%. Static/office content alone does not alert.

Thresholds are illustrative. The architecture can add CPU bottleneck,
hardware-encoding, GPU-memory, bandwidth and network-instability rules, but the
MVP intentionally ships four rules. Sustained-window and configurable thresholds
are future work; individual snapshots cannot prove causality.

## Integration research gates

| Adapter | Research before implementation |
| --- | --- |
| Citrix HDX | Supported SDK/telemetry sources, version/licensing matrix, session-to-client identity, counter definitions, units, transport and permissions; test inside actual VDA sessions. |
| Omnissa Blast | **Implemented (0.2)** from local Horizon Blast Session/Imaging performance counters; see [docs/HORIZON_BLAST.md](docs/HORIZON_BLAST.md). Open: documented units for jitter, estimated bandwidth, poll/FBC rate and instantaneous byte counters; Encoder Type code mapping; negotiated transport; other Blast channels. |
| Microsoft RDP | Documented Windows session/transport and graphics measurement interfaces, local/remote session mapping, counter availability and privileges across supported Windows versions. |
| NVIDIA NVML | Official redistribution/license terms, supported driver/API loading, vGPU vs physical-device mapping, per-session attribution and non-admin availability. |

No guessed API signatures, performance-counter names, registry keys or WMI
classes are included. Each future adapter must implement availability detection,
initialization, cancellable collection, error reporting and cleanup, and return
explicit Unavailable values for unsupported measurements. GPU device-level data
must not be presented as session-level data without confirmed attribution.

## Packaging and privacy

The portable profile publishes self-contained win-x64, single-file, with native
library extraction and no trimming. Windows 10 19041 targeting avoids falling
back to legacy .NET Framework chart dependencies. No telemetry or network client
is registered. Local logs/preferences use the standard user's LocalAppData;
recording paths are selected by the user. No elevation is requested. The Windows
workflow smoke-tests the actual published EXE and loads every page, checks binding
errors, changes theme, records and exports. Interactive Windows acceptance is
still required, especially in VDI with restricted or virtual graphics devices.
