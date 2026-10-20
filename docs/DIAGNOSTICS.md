# Diagnostic intelligence (0.4)

EUC Lens evaluates its measurements with offline, transparent rules. It does not
use AI, cloud services or telemetry, and it never changes Horizon or Windows
configuration. Each conclusion names the measurements, observation window and
decision rule behind it.

It answers three questions:

1. **Is there a measurable problem?** Three independent health domains, each with coverage.
2. **Which component is likely contributing?** Findings and cautious assessments ("suspected", "may be contributing").
3. **What to investigate next?** Recommended actions on every finding.

Correlation is never presented as proven causation. Findings are about the **VDI
guest**, not the hypervisor host: guest measurements cannot prove hypervisor contention.

## Architecture

| Component | Role |
|---|---|
| `MetricsHistory` / `MetricSeries` | Bounded ring buffers (512 entries per metric, no per-sample allocation) fed from the existing snapshot. No new collectors. |
| `WindowStats` | Mean, min/max, P25/P50/P75/P95, standard deviation, trend slope, covered time, valid/stale/failed counts per window. |
| `DiagnosticContext` | Read-only inputs to rules: snapshot, history, thresholds, monotonic time, workload, GPU presence. |
| `IDomainRule` | One self-contained rule: ID, domain, required metrics, evaluation. Add a rule by registering another implementation. |
| `FindingTracker` | Finding lifecycle: hysteresis, escalation, downgrade, recovery and expiry, with a transition event for each change. |
| `HealthEvaluator` | Connection, Graphics and VDI Guest assessments with coverage and evidence. |
| `HypothesisBuilder` | Combines findings into cautious explanations with verification steps. |
| `DiagnosticIntelligenceEngine` | Thread-safe orchestration. Runs on the sampling task, off the WPF UI thread. |

The 0.1–0.3 single-sample rules (`LatencyRule`, `SustainedCpuRule`, …) remain in the
library for API compatibility. The application uses the new engine.

### Time and samples

* Windows use **elapsed monotonic time** (`TimeProvider.GetTimestamp`), not wall-clock
  time or metric timestamps. Clock changes and missing metric timestamps do not
  affect them.
* Each valid sample represents the time since the previous sample, capped at
  `MaximumSampleGapSeconds` (5 s). Irregular intervals are handled correctly, and gaps
  reduce coverage instead of being assumed to be one second.
* Stale, failed and unavailable samples never enter statistics or thresholds. They are
  counted and shown as evidence.
* A rule only concludes when its window has ≥ 70 % coverage and ≥ 10 valid samples.
  Otherwise the result is *Insufficient Data*.

### Hysteresis, recovery and expiry

* **Raise:** a condition must hold for ≥ 70 % of the covered window time. One sample
  never raises a finding.
* **Escalate:** immediately when the critical condition holds.
* **Downgrade:** from Critical, only after the critical condition has not held for the
  30 s recovery window.
* **Recover:** only when values stay below the warning threshold minus a 10 % margin
  (for example RTT P95 < 135 ms with a 150 ms warning) over the 30 s recovery window.
  Values in between keep the finding active, so readings near a threshold do not cause
  detect/recover churn.
* **Expire:** a finding with no usable data for 120 s closes as *Expired*, so old
  findings do not stay active indefinitely.

Recovered and expired findings stay visible (most recent 50) with their end time and reason.

## Health assessment

| Domain | Required (else *Insufficient Data*) | Also evaluated when available |
|---|---|---|
| **Connection Health** | Blast RTT | Uplink packet loss, reconnect count |
| **Graphics Health** | FPS | Dirty FPS (screen activity), out queueing time, guest CPU |
| **VDI Guest Health** | Guest CPU, physical RAM | Available RAM, disk active time; GPU, VRAM and encoder when a hardware GPU exists |

* **State:** Critical or Warning when a finding in the domain is active; otherwise
  Healthy if the required metrics have sufficient data; otherwise **Insufficient Data**.
  Missing measurements are never treated as zero, and short observations never read
  as Healthy.
* **Coverage:**
  * *Full*: every applicable metric was evaluable.
  * *Partial*: some optional metrics were missing, or some were excluded by design
    (for example, jitter units are unverified).
  * *None*: the required metrics were missing.
* GPU findings count towards VDI Guest Health. Data-quality findings are listed but do
  not change any domain state.
* There is no 0–100 score.

## Rules and default thresholds

All values are **initial tunable defaults**: conservative starting points for VDI
troubleshooting, not vendor or industry standards. Every value can be changed (see
*Configuration*). Unless stated otherwise, rules use the 60 s standard window and the
sustained/coverage requirements above.

| Rule ID | Domain | Condition (Warning / Critical) | Rationale |
|---|---|---|---|
| `connection.rtt.sustained` | Connection | RTT ≥ 150 / 300 ms | Interactive remote desktops are commonly comfortable below ~150 ms. 300 ms is clearly degraded. RTT is round-trip, not one-way latency. |
| `connection.rtt.unstable` | Connection | RTT interquartile range (P75 − P25) ≥ 50 ms (Warning) | Detects persistently variable delay. The interquartile range ignores spikes in up to 25 % of samples, so brief spikes do not alert. |
| `connection.packetloss.uplink` | Connection | Mean uplink loss ≥ 1 / 5 % | Sustained loss above ~1 % noticeably affects interactive protocols. **Uplink only**; downstream loss is not inferred. |
| `connection.reconnects` | Connection | Reconnect counter +1 within 120 s (Warning) | A reconnect is a user-visible interruption. Recovers after 120 s without increase. |
| `graphics.fps.low` | Graphics | FPS < 15 for ≥ 70 % of the window (Warning) | Only for *Interactive graphics* and *Video playback* workloads, and only when the screen is changing (dirty FPS ≥ 5). Without a dirty-frame counter the finding is marked *activity unverified*. A static screen or idle/office workload never alerts. |
| `graphics.fps.unstable` | Graphics | FPS coefficient of variation ≥ 0.5 while dirty-FPS variation < 0.25 (Warning) | Frame delivery fluctuates while content changes steadily. |
| `graphics.queueing` | Graphics | Out queueing time ≥ 50,000 µs (**Information only**) | Counter semantics (per-interval or cumulative) are not documented, so this indicator never changes Graphics Health. |
| `graphics.bottleneck.cpu` / `.gpu` / `.network` | Graphics | Low-FPS condition together with sustained CPU ≥ 90 %, GPU ≥ 90 % (hardware GPU only), or RTT/loss over their warning thresholds | Reported as a *possible* contributor. |
| `guest.cpu.saturation` | Guest | CPU ≥ 90 / 98 % | Evidence includes the logical processor count. With 2 vCPUs, one busy thread is ~50 %. |
| `guest.memory.pressure` | Guest | RAM ≥ 90 / 95 % **or** available ≤ 1.0 / 0.5 GiB | Low available physical memory leads to paging. Physical RAM only. |
| `guest.disk.saturation` | Guest | Disk active time ≥ 90 % (Warning) | Uses active time; throughput alone is never used. Real Session only. |
| `gpu.utilization`, `gpu.memory.pressure`, `gpu.encoder` | GPU | 90/98 %, 90/97 %, 85/95 % | Only with a hardware GPU and real counters. Missing counters are never 0 %. |
| `gpu.software-rendering` | GPU | Information | No hardware GPU is a **supported configuration**, never a warning by itself. |
| `dataquality.measurement-failures` | Data quality | ≥ 50 % of a core metric's samples stale or failed in 30 s (Warning) | Explains why assessments are limited. |
| `dataquality.blast-unavailable` | Data quality | Information | No Blast counters for this session; guest and graphics assessments continue. |
| `dataquality.excluded-metrics` | Data quality | Information | Jitter and estimated bandwidth have undocumented units and are shown as raw evidence only. Bandwidth saturation is not assessed (no verified capacity metric). UDP/TCP byte counts do not prove the negotiated transport. |

### Workload

The workload (Idle, Office, Interactive graphics, Video playback) is **user-selected**.
It does not prove activity. Graphics rules require screen-activity evidence from Blast
dirty frames where available, and mark conclusions as uncertain otherwise.

## Assessments (hypotheses)

| Situation | Statement |
|---|---|
| Connection findings, guest CPU normal | "Network-related connection degradation is suspected." |
| Low FPS (active) + CPU saturation, RTT normal | "Guest CPU saturation may be contributing to graphics performance degradation." |
| Low FPS, idle/office workload, CPU and RTT normal | "No graphics performance problem can be inferred from low FPS alone." |
| No hardware GPU, guest and connection healthy | "No GPU-related problem detected. Software rendering is in use." |
| Connection findings + CPU saturation | "…the available evidence cannot distinguish which is primary." (multiple plausible causes, with steps to tell them apart) |
| CPU + memory pressure | Possible guest undersizing |
| Nothing abnormal | "No measurable performance problem detected in the evaluated metrics." (with coverage caveat) |
| All domains lack data | "Not enough valid measurements yet to assess the session." |

## Example output

The following was produced by the engine from **synthetic test data**. It is not a
measurement from any real environment.

**Healthy idle desktop, software rendering**

```
Connection: Healthy | Coverage Partial | RTT P95 36 ms · packet loss (uplink) mean 0 % · reconnects total 0 — within configured thresholds
            jitter and estimated bandwidth units unverified (shown raw, not evaluated); bandwidth saturation not assessed
Graphics:   Healthy | FPS P95 3 FPS · dirty FPS P95 1 FPS · CPU P95 16 % — within configured thresholds | workload is user-selected (Idle)
Guest:      Healthy | CPU P95 16 % · RAM P95 45 % · available RAM mean 4.4 GiB | software rendering — GPU analysis not applicable
Assessment: No graphics performance problem can be inferred from low FPS alone. [Expected — workload is Idle and CPU/RTT are normal]
Assessment: No GPU-related problem detected. Software rendering is in use. [Supported configuration]
```

**Network degradation**

```
Connection: Warning
Assessment: Network-related connection degradation is suspected. [Suspected — guest CPU is within thresholds]
WARNING — Elevated Blast round-trip time (connection.rtt.sustained)
Blast RTT was at or above 150 ms for 100% of the covered 60 s (mean 196.17 ms, P95 216.05 ms).
  - Time at or above 150 ms: 60 of 60 s (100%)
  - Window min / max: 180 / 219 ms
  - Observation: 60 s window · 60 valid of 60 samples · 100 % covered
Rule: Warning when Blast RTT ≥ 150 ms (Critical ≥ 300 ms) for ≥ 70 % of valid covered time in a 60 s window
      with ≥ 70 % coverage and ≥ 10 valid samples. Recovers when P95 < 135 ms over 30 s.
WARNING — Uplink packet loss (connection.packetloss.uplink)
Blast reported an average uplink packet loss of 2.5 % over 60 s (P95 2.5 %).
```

**CPU-bound video playback (2 vCPUs, software rendering)**

```
Graphics: Warning · Guest: Warning · Connection: Healthy
Assessment: Guest CPU saturation may be contributing to graphics performance degradation. [Possible — RTT remains within thresholds]
WARNING — Sustained guest CPU saturation (guest.cpu.saturation)
  - Window mean: 97 %  · P95: 98 %
  - Logical processors: 2 (One fully busy thread is about 50 % of total CPU on this VM.)
WARNING — Guest CPU saturation may be limiting frame rate (graphics.bottleneck.cpu)
Low FPS during an active workload coincides with sustained guest CPU ≥ 90 %. Blast software encoding uses guest CPU,
so CPU saturation may be contributing. Screen activity confirmed by Blast dirty frames.
```

## Configuration

Thresholds live in `%LOCALAPPDATA%\EucLens\diagnostics.json`.

* **Settings › Diagnostic thresholds › Restore defaults** writes every value to that file.
* After editing the file, select **Reload from file**.
* Units are part of each property name (`RttWarningMs`, `CpuWarningPercent`,
  `AvailableRamWarningGiB`, `StandardWindowSeconds`, …).
* Omitted properties keep their defaults, and comments are allowed.
* Unknown property names are rejected, so typos are reported rather than ignored.

The file is validated before use:

* ranges for every value;
* warning < critical for each metric;
* short < standard < long window;
* critical available RAM below the warning level.

An invalid file is rejected, the defaults stay in use, and Settings lists the errors.
Applying thresholds clears the diagnostic history, so earlier evidence is not judged
by new rules.

```json
{
  "StandardWindowSeconds": 60,
  "RttWarningMs": 150,
  "RttCriticalMs": 300,
  "CpuWarningPercent": 90,
  "CpuCriticalPercent": 98
}
```

## Recording and export schema (0.4)

Changes are append-only. Every 0.2/0.3 column and JSON property is unchanged.

* **JSON:** each sample gains `Diagnostics`:
  * `Assessments`: domain, state, coverage, evidence, coverage note, window.
  * `ActiveFindings`: `ruleId:Severity`.
  * `Events`: transitions that occurred at this sample (`Detected`, `Escalated`,
    `Downgraded`, `Recovered`, `Expired`). Each event carries the full finding:
    rule ID, domain, severity, title, description, detected / last-confirmed /
    recovered timestamps, recovery reason, observation window, valid and total
    samples, evidence, decision rule, causes, actions and limitations.
* **CSV:** appended columns `diag_connection`, `diag_graphics`, `diag_guest`
  (state labels), `diag_coverage`, `diag_active_findings` and `diag_events`
  (e.g. `Detected connection.rtt.sustained Warning`). Full evidence is in the JSON export.
* Unchanged assessments and finding lists are shared between samples, so recordings do
  not duplicate them.
* No user content, keystrokes, screenshots or application data are captured.

## Known limitations

* Thresholds are starting points and need tuning against real sessions. CI tests use
  synthetic data and do **not** prove diagnostic accuracy on a real VDI.
* The workload is user-selected. Without Blast dirty frames, activity cannot be
  verified, and graphics conclusions are marked uncertain.
* Several interpretations rest only on counter names, because Omnissa does not
  document them further:
  * dirty frames are treated as screen changes;
  * out queueing time is treated as outbound queueing (information only).
* Guest metrics cannot reveal hypervisor contention (CPU ready, ballooning, datastore latency).
* Per-process attribution is not part of the findings; use Task Manager or Resource Monitor as recommended.
* Health uses the 60 s standard window. A new or reconnected session shows
  *Insufficient Data* for about 42–60 s.
