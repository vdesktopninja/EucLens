# Horizon Blast monitoring (Real Session mode)

EUC Lens reads Omnissa Horizon Blast metrics from the Windows performance
counters published by the Horizon Agent inside the virtual desktop. Everything
is local: there are no Connection Server, REST, cloud or telemetry calls, and no
administrator rights are required where Windows grants the interactive user
read access to performance counters (the default for local users; otherwise add
the user to *Performance Monitor Users*).

## How collection works

| Stage | Component | Behaviour |
|---|---|---|
| Session context | `WindowsBlastSessionContext` | Uses `Process.GetCurrentProcess().SessionId`, the session ID is never hardcoded. Detects the Horizon Agent from its registry/service keys (Omnissa and legacy VMware paths) and checks via `WTSQuerySessionInformation` that the session is active. |
| Discovery | `OmnissaBlastProvider` | Lists instances of `Horizon Blast Session Counters` and `Horizon Blast Imaging Counters` and keeps only those whose name starts with `Session ID: <n>;` for **exactly** this session (3 never matches 13 or 31). If several instances match, the measurements are refused as ambiguous, so another session's data is never shown. |
| Reading | `WindowsBlastCounterReader` | Keeps one `PerformanceCounter` handle per counter, takes raw samples with `NextSample()`, and computes rate counters from two consecutive samples. Handles are released when discovery changes (reconnect, new instance) and on dispose. |
| Conversion | `BlastCounterCatalog` | Applies only documented unit conversions (see below) and records source path, timestamp, raw value, unit, status and error per metric. |
| Presentation | `MainViewModel` | Samples once per second on a worker thread; the WPF thread only applies the results. |

### Sample status

| Status | Meaning | Displayed |
|---|---|---|
| Available | Counter was read; `0` is a real measurement | value + unit |
| Unavailable | Agent not installed, session inactive, instance or counter missing, or rate counter warming up | N/A + reason |
| Stale | Native counter timestamp did not advance since the previous read | N/A |
| Failed | Read threw (access denied, instance vanished, non-finite value) | N/A + error |

Diagnostics never evaluate thresholds against Stale, Failed or Unavailable data.
The low-FPS rule fires only while the Blast dirty-frame counter shows the content
changing at least 15 times per second (since 0.5.1; there is no workload selection),
so an idle desktop showing 0–2 FPS, or typing in a document, raises no warning.
Without the dirty-frame counter low FPS is not judged.

## Metrics and units

Units are converted only where Omnissa documents the unit, either in the
counter name or in the [Horizon Help Desk session details][helpdesk] (RTT in ms,
packet loss as a percentage). Everything else is shown as the raw counter value,
with a unit label ending in `unverified`.

| Counter set | Counter | EUC Lens key | Shown as |
|---|---|---|---|
| Session | RTT | `rtt` | ms (dashboard RTT + chart) |
| Session | Packet Loss (Uplink) | `packetLoss` | % |
| Session | Jitter (Uplink) | `jitter` | raw (unit unverified) |
| Session | Estimated Bandwidth (Uplink) | `estimatedBandwidth` | raw (unit unverified). Community tools report Kbps, but Omnissa does not document it, so it is not converted and not used as "available bandwidth" |
| Session | Automatic Reconnect Count | `reconnects` | count |
| Session | Cumulative Received/Transmitted Bytes over UDP/TCP | `udp*/tcp*Bytes` | bytes |
| Session | Instantaneous Received/Transmitted Bytes over UDP/TCP | `*Instantaneous` | raw (interval unverified) |
| Imaging | Frames per second | `fps` | FPS (dashboard FPS + chart) |
| Imaging | Dirty frames per second | `dirtyFps` | FPS |
| Imaging | Inbound / Outbound Bandwidth (Kbps) | `imagingInboundMbps` / `imagingOutboundMbps` | Mbps (= Kbps ÷ 1000); outbound feeds "Current bandwidth" |
| Imaging | Out Queueing time (us) | `outQueueingTime` | µs |
| Imaging | Total frames, Total dirty frames | `totalFrames`, `totalDirtyFrames` | frames |
| Imaging | Received/Transmitted Packets, Bytes | `imaging*` | packets / bytes |
| Imaging | Poll Rate, FBC Rate | `pollRate`, `fbcRate` | raw (unit unverified) |
| Imaging | Encoder Type | `encoderType` | `Raw code N`. No codec name is inferred from it |

**Not exposed by these counter sets (always N/A in Real Session):** one-way
network latency, available link capacity, dropped frames, codec name, hardware
encoding, resolution, negotiated transport, client hostname, and GPU, CPU, RAM
and disk utilisation. Other Blast channels (Audio, Clipboard, RTAV, HTML5 MMR,
USB, CDR, FIDO2, RdeServer) are not collected yet.

## Validating against PerfMon

1. In the Horizon desktop, open a command prompt and run `query session` (or
   check Task Manager → Users → Session column). Note your session ID, *N*.
2. Start `perfmon.exe` → *Performance Monitor* → **+** (Add Counters).
3. Expand **Horizon Blast Session Counters**, select `RTT`, `Packet Loss (Uplink)`
   and `Jitter (Uplink)`, choose the instance `Session ID: N; (main)` and click **Add**.
4. Expand **Horizon Blast Imaging Counters**, add `Frames per second`,
   `Dirty frames per second`, `Outbound Bandwidth (Kbps)` and `Encoder Type` for
   `Session ID: N; Channel: Imaging; (main)`.
5. Tick **Show description** in the Add Counters dialog to read each counter's
   help text from the Horizon Agent. This is also how to confirm the unverified
   units on a given agent build.
6. Set PerfMon to a 1-second sample interval (Properties → General), switch the
   view to **Report** (Ctrl+G), and run EUC Lens side by side in Real Session mode.
7. Expected agreement:
   * RTT, packet loss and FPS match within one sampling interval. The two tools
     sample at slightly different moments, so compare trends rather than single values.
   * EUC Lens Mbps × 1000 = PerfMon `Outbound Bandwidth (Kbps)`.
   * Raw values (jitter, estimated bandwidth, encoder type) match PerfMon exactly.
   * Each card's detail line shows the full counter path it read, so you can
     check that it uses your session's instance.
8. For an offline comparison, record in EUC Lens and export CSV, and log the
   same counters in PerfMon with a *Data Collector Set* (CSV format, 1-second
   interval). Align on timestamps (EUC Lens writes UTC ISO-8601) and compare the
   `blast_*` columns with the PerfMon columns.

PowerShell spot check (prints the same values EUC Lens reads):

```powershell
$id = (Get-Process -Id $PID).SessionId
Get-Counter -Counter "\Horizon Blast Session Counters(Session ID: $id; (main))\RTT",
  "\Horizon Blast Imaging Counters(Session ID: $id; Channel: Imaging; (main))\Frames per second" `
  -SampleInterval 1 -MaxSamples 10
```

## Validating against Horizon Performance Tracker

Horizon Performance Tracker (HPT) is installed with the Horizon Agent when the
*Horizon Performance Tracker* feature is selected, and it shows the same Blast
session statistics on the desktop.

1. Launch **Horizon Performance Tracker** from the Start menu inside the session
   and select the Blast view.
2. Launch EUC Lens in Real Session mode and place the windows side by side.
3. Compare **RTT**, **FPS** and **packet loss**. HPT and EUC Lens read the
   same agent statistics but sample on their own timers, so expect the values to
   agree within about one second of drift.
4. Generate load (play a video or drag a window continuously) and check that
   FPS, dirty FPS and imaging outbound bandwidth rise together in both tools,
   then fall back when the desktop goes idle.
5. Disconnect and reconnect the client: while disconnected, EUC Lens shows N/A
   ("session is disconnected or inactive"). After reconnecting it rediscovers
   the instance, and `Automatic Reconnect Count` may change.
6. Record any differences with the agent version (*About Horizon Agent*) and
   the client type. Bandwidth units in HPT are labelled in its UI. Do not use
   them to infer the raw counter units without the counter's help text.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Horizon Agent installation was not detected" | Not running inside a Horizon desktop, or the agent registry keys are absent |
| "No … instance for current Windows session N" | Session is connected over another protocol (PCoIP/RDP), or the Blast session has not started |
| "Warming up: this counter requires two samples" | Normal for rate counters during the first second |
| Failed · `UnauthorizedAccessException` | The user cannot read performance counters; add them to *Performance Monitor Users* |
| Counters missing entirely | Performance counter registration is damaged: `lodctr /R` (admin), then sign out and back in |

[helpdesk]: https://docs.omnissa.com/Horizon-Administration-V2312/SessionDetailsforHorizonHelpDeskTool
