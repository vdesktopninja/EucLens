# System, GPU and display monitoring (Real Session mode)

Milestone 0.3 adds protocol-independent measurements of the Windows machine EUC Lens
runs on. They work the same in Horizon, Citrix, RDP and local console sessions, and are
collected alongside the Horizon Blast counters ([HORIZON_BLAST.md](HORIZON_BLAST.md)).
Everything is local: no WMI polling, no administrator rights, no network access.

## Scope: what a value describes

| Scope | Meaning | Examples |
|---|---|---|
| **This session** | Only the Windows session EUC Lens runs in | Blast counters, display topology, GPU use by this session's processes |
| **Machine-wide** | The whole (virtual) machine, including other users' sessions on multi-session hosts | CPU, RAM, disk, adapter GPU utilization and memory, NVML |

The scope is shown in every metric tooltip, in Advanced Metrics and in exports.

## Sources

| Area | Metric | Source | Notes |
|---|---|---|---|
| CPU | Utilization % | `GetSystemTimes` deltas: (kernel + user − idle) ÷ (kernel + user) | Machine-wide, all logical processors. First sample is *Unavailable (warming up)*; time moving backwards or not advancing is *Stale*. |
| CPU | Model, logical processors, physical cores | Registry `HARDWARE\DESCRIPTION\System\CentralProcessor\0`, `GetLogicalProcessorInformationEx` | Read once. Cores are as presented to the VM. |
| Memory | Total, available, in use (GiB), utilization % | `GlobalMemoryStatusEx` (`ullTotalPhys`, `ullAvailPhys`) | Physical RAM only; commit charge is not used. GiB = 1024³ bytes. |
| Disk | Read / write MB/s | `\PhysicalDisk(_Total)\Disk Read Bytes/sec`, `…\Disk Write Bytes/sec` | MB = 10⁶ bytes. Rate counters need two samples. |
| Disk | Active time % | 100 − `\PhysicalDisk(_Total)\% Idle Time` | Average of physical disks; optional. |
| Windows | Version | Registry `CurrentVersion` (`ProductName`, `DisplayVersion`, build.UBR) | Build ≥ 22000 is reported as Windows 11 (the registry name still says 10). |
| GPU | Adapters | DXGI `IDXGIFactory1::EnumAdapters1` / `GetDesc1` | Description, PCI IDs, LUID, dedicated video memory, shared system memory, remote/software flags. Refreshed every 60 s. |
| GPU | Driver version | Registry display class `{4d36e968-…}`, matched by PCI vendor/device ID | Windows driver version (e.g. 32.0.15.6094); NVIDIA version from NVML when linked. |
| GPU | Utilization % | `\GPU Engine(*)\Utilization Percentage`, one bulk read per second | Per engine, processes are summed; adapter value = busiest engine (the Task Manager method). Engines are never added together. Clamped to 100 %. |
| GPU | This session's utilization % | Same counters, only processes whose `ProcessIdToSessionId` equals this session | Session scope; useful on multi-session hosts. |
| GPU | Video encode / decode % | Busiest engine whose driver-reported type contains `Encode` / `Decode` | N/A when the driver exposes no such engine. Engine type names come from the driver. |
| GPU | Dedicated / shared memory in use | `\GPU Adapter Memory(*)\Dedicated Usage`, `…\Shared Usage` | Kept separate. VRAM % = dedicated usage ÷ DXGI dedicated video memory. |
| NVML | GPU %, memory controller %, framebuffer used/total, NVENC %, NVDEC %, virtualization mode | `nvml.dll` from the NVIDIA driver | Optional; see below. |
| Display | Monitors, resolution, refresh rate, position, scaling, topology | `EnumDisplayMonitors`, `GetMonitorInfo`, `EnumDisplaySettings`, `GetDpiForMonitor`, `GetSystemMetrics(SM_REMOTESESSION)` | Describes **this session's desktop**, which is what the protocol renders. It may differ from the client's physical monitors (client scaling, partial-monitor windows). Cached for 5 s. |

### Which adapter the dashboard shows

DXGI enumeration order is not used to pick the GPU. Adapters are classified first:
NVIDIA (physical/passthrough), NVIDIA vGPU, AMD, Intel integrated, other hardware GPU,
Microsoft Basic Display, Microsoft Basic Render (software), VMware SVGA, and remote or
indirect display adapters. The dashboard uses a hardware GPU (discrete before
integrated, then the most dedicated memory). Every adapter is listed individually in
**Advanced Metrics → GPU**, along with the reason for the selection.

The NVIDIA vGPU classification from the adapter name (e.g. `A16-2Q`, `GRID T4-4B`) is a
heuristic. When NVML is available, its virtualization mode (`vGPU guest`,
`passthrough`, `none`) replaces it.

### Precedence when two sources exist

Windows GPU counters come first. NVML only fills a dashboard field that Windows
cannot supply, for example when a vGPU driver publishes no engine counters, and the
tooltip then names NVML as the source. Both sources always appear separately in
Advanced Metrics. They do not measure the same thing: NVML "GPU utilization" is the
share of time a kernel was running, while the Windows value is the busiest WDDM engine.
NVML "memory utilization" is memory-controller activity, **not** VRAM usage, and is
labelled that way.

## NVIDIA NVML prerequisites

* NVML ships with the NVIDIA display driver (including vGPU guest drivers). EUC Lens
  does **not** bundle it.
* EUC Lens loads it only from `%SystemRoot%\System32\nvml.dll` or
  `%ProgramFiles%\NVIDIA Corporation\NVSMI\nvml.dll` (absolute paths, never from
  the DLL search path).
* No administrator rights are required for the queries used.
* NVML devices are linked to DXGI adapters only when exactly one adapter and one
  device share PCI vendor, device and subsystem IDs. Otherwise they are listed
  unlinked.
* vGPU guests may answer `Not supported` (NVML 3) for some queries, depending on
  the vGPU software version and profile. Such values show *Unavailable* with the
  NVML message, not *Failed*.

## Metrics that may be unavailable

| Metric | When it is N/A |
|---|---|
| GPU utilization, encode/decode | No hardware GPU (VMware SVGA, Basic Display/Render, RDP remote adapter only); driver publishes no `GPU Engine` instances for the adapter (some vGPU/older drivers); Windows older than 10 1709 |
| Encoder / decoder | Driver exposes no `*Encode*` / `*Decode*` engine type; NVML returns *Not supported* |
| VRAM % | Adapter reports 0 dedicated video memory (shared-memory or virtual adapters); no `GPU Adapter Memory` instance |
| NVML values | No NVIDIA driver, no nvml.dll, NVML init failure, query not supported in the vGPU guest |
| Driver version | The display-class registry entry for the adapter's PCI IDs is missing or unreadable |
| Disk active time | `PhysicalDisk` counters disabled or not yet warmed up |
| Display | No interactive desktop (service context) |
| Per-process GPU details | Not shown; only adapter-level and session-level aggregates |
| Hardware encoder type/codec for Blast | Not exposed; see HORIZON_BLAST.md |

## Diagnostics

Health is reported separately as **Connection** (Blast RTT, frame delivery,
bandwidth) and **System / host performance**. An area is *Unavailable* when one of its
rules has no current measurement, so missing data never reads as healthy.

The host rules (`HostDiagnosticThresholds`, defaults below) only report a finding
after a full window, and only when at least 80 % of the available samples cross the
threshold. A short spike does not produce a warning.

| Rule | Default |
|---|---|
| Window | 30 samples (~30 s); at least 80 % of samples must be available |
| Sustained CPU | ≥ 90 % |
| Memory pressure | ≥ 90 % physical RAM in use |
| Sustained GPU | ≥ 90 % busiest engine |
| GPU memory pressure | ≥ 90 % of dedicated memory |
| Encoder utilization | ≥ 85 % |
| Combined graphics bottleneck | *Interactive graphics* workload, FPS < 20 sustained, and CPU or GPU sustained over threshold |

The original single-sample GPU rule applies to Demo Mode only. Sustained windows are
cleared when the mode, demo scenario or workload changes.

## Export schema changes (0.3)

All changes are **append-only**. Every column from 0.2 keeps its name and position.

* CSV: after the `blast_*` columns come `protocol_detection`, then for each host
  metric ID: `host_<id>`, `host_<id>_unit`, `host_<id>_status`, `host_<id>_raw`,
  `host_<id>_scope`, `host_<id>_source`. Text facts (GPU model, topology) appear
  quoted in `host_<id>`. IDs are stable, e.g. `cpu.utilization`,
  `memory.availableGiB`, `disk.readMBps`, `gpu.0.utilization`, `gpu.0.encoder`,
  `nvml.0.memoryUsedGiB`, `display.primary`.
* Existing columns `gpu_percent`, `vram_percent`, `encoder_percent`,
  `decoder_percent`, `cpu_percent`, `ram_percent` and `disk_mbps` are now
  populated in Real Session. `disk_mbps` is read + write throughput.
  `protocol` may be `Unknown`.
* JSON: each snapshot gains `HostMetrics` (`Id`, `Category`, `Name`, `Text`, `Metric`),
  and `Metric` gains `Scope`. `Session` gains `ProtocolDetection`. `Gpu` gains
  `VramUsedTotal` and `AdapterKind`.

Recording memory: a Real Session snapshot holds roughly 60–90 metrics. Unchanged facts
are shared between samples, but very long recordings (many hours at 1 Hz) can still
use several hundred MB of RAM before export.

## Validating against Windows tools

| EUC Lens | Compare with |
|---|---|
| CPU utilization | Task Manager › Performance › CPU *Utilization* (Task Manager uses *% Processor Utility*, which can differ slightly under turbo/power management) or PerfMon `\Processor(_Total)\% Processor Time` |
| RAM in use / available | Task Manager › Memory *In use* / *Available* |
| Disk read/write | Resource Monitor › Disk, or PerfMon `\PhysicalDisk(_Total)\Disk Read Bytes/sec` |
| GPU utilization, encode, decode, dedicated/shared memory | Task Manager › Performance › GPU (same counters and method) |
| NVML values | `nvidia-smi` (`nvidia-smi -q -d UTILIZATION,MEMORY,ENCODER_STATS`) in the guest |
| Display | Settings › System › Display |

Real GPU, vGPU and Horizon results must be confirmed in a VDI lab. CI runners only
show that collectors start and degrade correctly without NVIDIA hardware.
