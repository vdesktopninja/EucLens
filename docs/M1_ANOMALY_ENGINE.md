# EUC Lens 0.5 — M1: deterministic anomaly detection engine

Status: implemented on `feature/euc-lens-0.5-intelligent-diagnostics`; not merged, not released. Manual Horizon acceptance (roadmap M4) is still required.

The engine turns a recorded session (`LoadedHistory`, or any equivalent arrays) into **observations**: bounded intervals in which a *measured* condition held, with the evidence needed to verify each one against the recorded samples, plus conservative **correlations** between observations. It is an analysis core only: no WPF, no LiveCharts, no Dispatcher, no network, no model, no randomness, no clock. The same input and options always give the same result in the same order with the same ids, on any machine and under any culture.

It never says *why* something happened. "Confirmed Observation" means only "this measured condition is supported by recorded values with enough coverage".

## 1. Architecture

All code is in `src/EucLens.Core/Anomalies/` (namespace `EucLens.Core`).

| File | Responsibility |
|---|---|
| `AnomalyModels.cs` | Immutable output records: `AnomalyAnalysis`, `AnomalyObservation`, `AnomalyCorrelation`, `MetricEvidence`, `MetricBaseline`, `ThresholdEvidence`, `MetricCoverage`, `RuleEvaluation`, enums. |
| `AnomalyInput.cs` | The input contract. `AnomalyInput.From(LoadedHistory)` or `AnomalyInput.Create(...)` from arrays. Orders samples, combines duplicates, turns non-finite values into "no value", carries the input's limitations. |
| `AnomalyRules.cs` | `AnomalyRule` (one explicit, versioned rule), `AnomalyRules.Default`, `UnitTable`, `AnomalyOptions` (every other default). All numbers live here. |
| `AnomalyTimeline.cs` | Continuous segments, disconnected stretches, sampling step, coverage. |
| `BaselineCalculator.cs` | Robust baseline (trimmed median and MAD). |
| `EpisodeDetector.cs` | One-pass grouping of matching samples into episodes with their ending. |
| `AnomalyDetectionEngine.cs` | Orchestration: coverage, display activity, per-rule evaluation, evidence, suppression, caps. |
| `CorrelationAnalyzer.cs` | The five correlation patterns. |
| `AnomalyNarrative.cs` | All wording, kept apart from the statistics. Culture-independent. |

One internal change outside the folder: `LoadedHistory.TickArray` (read-only access to the sample times). Recording format, loader and history behaviour are unchanged; `euclens.recording/1` is untouched.

Entry points: `AnomalyDetectionEngine.Analyze(LoadedHistory | AnomalyInput, options?, cancellationToken)` and `AnalyzeAsync(LoadedHistory, ...)` (runs on a worker thread).

### Pipeline for one rule

1. **Applicability.** A Horizon-Blast-only counter on a recording without Horizon Blast evidence is `NotApplicable`.
2. **Availability.** Missing channel, a channel that never had a valid value, an unsupported or unverified unit, or (for display rules) no way to establish display activity → `InsufficientData` with the reason. No observation is produced and nothing is reported as healthy.
3. **Sample classification.** Each sample is *unusable* (no value, non-finite, outside the plausible range, remote session disconnected for connection/graphics metrics, activity unknown), *out of context* (screen static), *normal* or *matching*.
4. **Baseline** (rules that use one). Unreliable → see §3.
5. **Episodes.** Matching samples are grouped (§4), judged against the rule's duration and sample requirements, ranked and capped.
6. **Evidence** is computed for each kept episode from the recorded values.
7. After all rules: **suppression** of duplicates, **global cap**, deterministic **ordering**, **correlations**.

## 2. Rules and thresholds

Units are canonical units; recorded units that convert (see `UnitTable`) are converted for the comparison and **reported in the recorded unit**. Thresholds for RTT, loss, FPS, CPU, memory, queueing and activity are the live diagnostic engine's defaults (`DiagnosticThresholds`). "At or above" for higher-is-worse metrics, "below" for FPS.

| Rule id (v1) | Metric (accepted units) | Criterion | Sustained when | Short kind | Severity |
|---|---|---|---|---|---|
| `conn.rtt.high` | `rtt` (ms, s, µs) | absolute ≥ 150 ms | ≥ 30 s, ≥ 5 samples | — | Warning; Critical if ≥ half of matching samples ≥ 300 ms |
| `conn.rtt.deviation` | `rtt` | absolute ≥ 100 ms **and** baseline (median + 6σ, σ ≥ 5 ms) | ≥ 30 s, ≥ 5 samples | Spike (≥ 1 sample) | Spike: Info, Warning if peak ≥ 300 ms |
| `conn.loss.sustained` | `packetLoss` (%) | absolute ≥ 1 % | ≥ 30 s, ≥ 5 samples | — | Warning; Critical if ≥ half ≥ 5 % |
| `conn.loss.burst` | `packetLoss` | absolute ≥ 3 % | — | Burst (≥ 2 samples) | Info; Warning if peak ≥ 10 % |
| `conn.queueing.increase` | `outQueueing` (µs, ms, s) | absolute ≥ 50 ms **and** baseline (6σ, σ ≥ 5 ms); Blast only | ≥ 15 s, ≥ 5 samples | — | Warning; Critical if ≥ half ≥ 250 ms |
| `conn.bandwidth.pressure` | `bandwidthConsumption` ÷ `availableBandwidth` (Mbps, Kbps, Gbps, bps; both verified) | ratio ≥ 0.85 | ≥ 30 s, ≥ 5 samples | — | Warning; Critical if ≥ half ≥ 1.0 |
| `gfx.fps.low` | `fps` (FPS), **requires display activity** | below 15 FPS **or** baseline (median − 4σ, σ ≥ max(2 FPS, 10 % of the median)) | ≥ 30 s, ≥ 5 samples | — | Warning; Critical if ≥ half < 5 FPS |
| `gfx.dropped.high` | `droppedFrames` (%), **requires display activity** | absolute ≥ 5 % | ≥ 30 s, ≥ 5 samples | — | Warning; Critical if ≥ half ≥ 15 % |
| `guest.cpu.saturation` | `cpu` (%) | absolute ≥ 90 % | ≥ 60 s, ≥ 10 samples | — | Warning; Critical if ≥ half ≥ 98 % |
| `guest.cpu.spike` | `cpu` | absolute ≥ 90 % **and** baseline (6σ, σ ≥ 5 pts) | — | Spike | Info |
| `guest.ram.high` | `ram` (%) | absolute ≥ 90 % | ≥ 120 s, ≥ 10 samples | — | Warning; Critical if ≥ half ≥ 95 % |
| `guest.gpu.saturation` | `gpu` (%) | absolute ≥ 90 % | ≥ 60 s, ≥ 10 samples | — | Warning; Critical if ≥ half ≥ 98 % |
| `guest.disk.throughput-spike` | `disk` (MB/s, KB/s, GB/s, B/s) | absolute ≥ 100 MB/s **and** baseline (8σ, σ ≥ 5 MB/s) | — | Spike | Info (activity, never a disk problem) |
| `guest.disk.stall` | `diskLatency` (ms, s, µs) | absolute ≥ 100 ms | ≥ 15 s, ≥ 3 samples | — | Warning; Critical if ≥ half ≥ 500 ms |

Notes on individual rules:

* **Plausible range.** Values outside it (RTT < 0 or > 1 h, any percentage < 0 or > 100.5, FPS > 1000, …) are counter errors: excluded, counted per metric in `MetricCoverage.OutOfRangeSamples`, never used as evidence.
* **Packet loss direction.** Horizon records only `Packet Loss (Uplink)`. Observations on a Blast recording say "uplink" and state that the other direction was not measured; on other protocols they state that the direction is not recorded.
* **Out-queueing** keeps its recorded unit (µs). It is a Blast counter: the rule is `NotApplicable` without Blast evidence.
* **Memory.** The metric is utilization. The rule is named `high`, and says that utilization does not show paging or allocation failures.
* **Disk.** The only recorded disk metric is throughput (MB/s). A throughput spike is an informational *activity* observation. `guest.disk.stall` needs a metric `diskLatency`, which `LoadedHistory` does not provide today; on real recordings it therefore reports `InsufficientData` ("no disk latency or queue metric was recorded") instead of inferring latency from throughput.
* **Bandwidth.** Horizon's `Estimated Bandwidth` is recorded in an unverified raw unit and is never interpreted; on real Horizon recordings this rule reports `InsufficientData`.
* **GPU.** Used only when the recording has valid GPU utilization.
* **Spike and burst rules are suppressed** by the sustained rule of the same metric when their intervals overlap (within the rule's merge tolerance): one continuing problem is one observation.

## 3. Baseline methodology

Used by rules with a baseline criterion. The aim is "unusual for *this* session", never a population norm.

1. Collect the valid, eligible samples of the metric (disconnected stretches excluded; for FPS only samples with established display activity).
2. **Reliability gate:** at least `MinimumBaselineSamples` = 120 valid samples **and** at least `MinimumBaselineSpan` = 2 minutes between the first and last of them (samples and time: a burst of samples is not a baseline; variable cadences are handled by time).
3. Sort, then **trim the worst tail** (`BaselineTrimFraction` = 10 %: the highest values for higher-is-worse metrics, the lowest for FPS), so anomalies cannot pull their own reference. The trimmed median is therefore deliberately slightly on the healthy side.
4. Baseline = median and **MAD** (median absolute deviation) of what is left; σ = 1.4826 × MAD, never below the rule's minimum scale or `RelativeScaleFloor` (10 %) × the median, so a perfectly flat session does not turn noise into anomalies. P05/P95 of all valid samples are reported for context.
5. Limit = median ± *k* σ (k = 4…8 per rule).

If the gate fails:

* a rule that *needs* the baseline (`Both`) reports `InsufficientData: Insufficient data for an adaptive baseline of <metric>: Only N valid sample(s) over …; a baseline needs at least 120 over 2 min`;
* a rule that can also work from its absolute threshold (`Either`: FPS) uses the absolute threshold **and says so**: `ThresholdEvidence.Basis = Absolute`, the unreliable baseline is attached with `Reliable = false`, and the observation lists "Baseline unavailable (…); only the absolute threshold was applied". It is never presented as adaptive.

A session that is *always* bad (RTT 200 ms throughout) is not a deviation from its own baseline, but is still reported by the absolute rule.

## 4. Time windows

* **Segments.** A segment is a continuous stretch of samples. A new segment starts after a gap (longer than `GapThreshold`, the loader's value: max(5 s, 3 × median step)) and at a recorded disconnect, reconnect, session change, pause, resume or data loss. **An observation never crosses a segment boundary**, and correlations only link observations on the same segment.
* **Disconnected sessions.** Between a recorded `RemoteDisconnected` and the next `RemoteReconnected`/session change, connection and graphics values are not used (stale or zero counter values are not measurements). Guest metrics stay eligible.
* **Grouping.** Matching samples are one episode when at most `MergeTolerance` apart (10 s; 5 s for spike/burst rules; never less than two sampling steps).
* **Duration** = first matching sample to last matching sample + one sampling step (the median step). *Sustained* needs `SustainedAfter`, the minimum matching samples, and matching samples ≥ 50 % of the valid samples in the interval. A longer but sparse episode is `Intermittent` (rules that allow short kinds), a shorter one a `Spike`/`Burst`.
* **Ending.** `Recovered` only when the next valid sample is normal with nothing missing in between (`RecoveredAt` is that sample). Otherwise `CoverageEnded` (gap, missing values, disconnect, end of the recording — "recovery is not confirmed") or `ContextEnded` (display activity stopped; not a recovery). An observation therefore cannot continue indefinitely when measurements disappear.
* **Start.** An interval that begins at the first sample of a segment says the condition may have begun earlier.
* All times are UTC (`DateTimeOffset` with zero offset). Local-time conversion and daylight saving are the presentation layer's business; the engine only sees instants, so a transition cannot shift or reorder anything.

## 5. Display activity (FPS and dropped frames)

Low FPS on a static screen is expected. Activity is established **only** from the recorded `dirtyFps` counter: the mean over a ±5 s window (at least three valid samples, same segment, not disconnected) must be ≥ `ActivityDirtyFps` = 5 (the live engine's default). The user-selected workload type is *not* evidence and is not used. Result per sample: active (evaluated), idle (excluded; `NotApplicable` when the whole recording is idle), unknown (excluded). Without the dirty-frame counter the rule reports `InsufficientData`: "Display activity cannot be established". Observations carry the dirty-FPS evidence as a second metric (role `Display activity`).

## 6. Metric coverage requirements

| Needed for | Requirement |
|---|---|
| any rule | the metric recorded with ≥ 1 valid sample, in a unit the rule interprets (a `raw (…)` unit is never interpreted) |
| Blast-only rules | `Omnissa Horizon Blast` among the recorded protocols (or, when the recording names none, Blast-only counters with values) |
| `gfx.*` | the `dirtyFps` counter (FPS) with enough samples to establish activity |
| rules with a baseline | the reliability gate of §3 |
| `conn.bandwidth.pressure` | both bandwidth metrics in verified units, available > 0 |
| `guest.disk.stall` | a `diskLatency` metric |

`AnomalyAnalysis.Coverage` lists every metric the rules touch: unit, usable or not (with reason), samples, valid, out-of-range, first and last valid time. `AnomalyAnalysis.Rules` lists every rule with `Evaluated`, `InsufficientData` or `NotApplicable`, a reason, and how many samples were eligible or excluded.

Missing values are never zero: NaN, null, failed and unavailable metrics are all "no value". Observations record `Coverage` (usable samples ÷ samples in the interval); below 80 % the observation is only a `QualifiedObservation` and does not take part in correlations.

## 7. Correlation logic

Only `ConfirmedObservation`s take part. Two observations are **linked** when they are on the same segment and their intervals overlap or lie within `CorrelationTolerance` = 5 s of each other, with at least `MinimumOverlapSamples` = 2 timeline samples in the overlap. Linked observations are clustered (union-find), one correlation per cluster, with a deterministic id derived from the pattern and its sorted component ids. The correlation lists its components, so evidence links back to observations and from there to samples.

**Overlap contract.** A correlation reports a common interval (`CommonOverlapStart`/`CommonOverlapEnd`) **only when every component was present in it** (latest start ≤ earliest end; `Timing = CommonOverlap`). Otherwise both are null: two components that do not overlap but lie within the tolerance are `WithinTolerance`; three or more components linked pair by pair (A overlaps B, B overlaps C, A and C do not overlap) are `Chained`. `Links` always lists the linked pairs with their own overlap interval, or the gap between them when they only lie within the tolerance, and the explanation and limitations name the pairs and say that no instant is common to all components. `Start`/`End` are the span of the cluster, not an overlap. Ids depend only on the pattern and the component ids.

| Pattern | Components | Notes |
|---|---|---|
| `RttAndPacketLoss` | an RTT observation (high or deviation) + a loss observation (sustained or burst) | wording: "RTT and packet loss degraded during overlapping intervals"; does not conclude endpoint, network, host or gateway |
| `GraphicsAndConnection` | `gfx.fps.low` or `gfx.dropped.high` + an RTT, loss, queueing or bandwidth observation | the connection side must not be a single-sample spike; display activity is already required by the graphics rules |
| `FpsAndGuestCpu` | `gfx.fps.low` + `guest.cpu.saturation` | sustained CPU only; "does not show that CPU was the cause" |
| `MultipleGuestResources` | at least two different metrics among CPU saturation, memory, and disk *latency* stalls | disk throughput and CPU spikes do not count |
| `AvailabilityOverlap` | one observation next to a gap, disconnect, reconnect, pause, resume, data loss or session change (within 5 s) | Info; states that observations on either side of a gap are not one event |

No correlation text names a cause; a test scans every generated string for causal vocabulary.

## 8. Evidence model and output contract

`AnomalyAnalysis` (one per recording): `RecordingKey`, `EngineVersion`, `From`/`To` (UTC), `SamplesAnalyzed`, `ReducedConfidence`, `Observations`, `Correlations`, `Coverage`, `Rules`, `Limitations`, `OmittedObservations`. Lists are immutable in practice and in deterministic order.

`AnomalyObservation`: `Id` (`obs-` + 16 hex of SHA-256 over recording key, rule id, rule version, start and end ticks), `RuleId`, `RuleVersion`, `Kind` (Sustained / Spike / Burst / Intermittent), `Domain` (Connection / Graphics / Guest), `Severity`, `Status`, `Title`, `Start`, `End`, `Ending`, `RecoveredAt`, `Metrics` (per metric: unit as recorded, valid samples, min, P50, P95, max, time of the worst value, role), `SampleCount`, `MatchingSamples`, `Coverage`, `Threshold` (basis, absolute limit, baseline limit, the baseline itself, a text comparison — all in the recorded unit), `AssociatedEvents` (recorded timeline events within ±5 s: incidents, findings, pauses, disconnects, data loss; at most 10, with the total count), `Explanation`, `Limitations`.

Ordering: observations by start, end, rule id (ordinal), id; correlations by start, end, pattern, id. Ids do not change when unrelated data changes; they change with the recording, rule version or interval.

Culture: every number, percentage, date and span in a generated string is formatted with the invariant culture and the Gregorian calendar (a test runs the engine under de-DE, pl-PL, fr-FR and ar-SA and compares all text).

## 9. Input integrity and limits

| Situation | Behaviour |
|---|---|
| Out-of-order samples | ordered (stable), limitation, reduced confidence |
| Duplicate timestamps | combined per metric (first valid value for that time), limitation, reduced confidence |
| Non-finite, null, failed values | "no value" |
| Truncated / damaged / missing parts, data loss, clock changes | carried from `LoadedHistory.Issues` and `Info` into `Limitations` (and into every observation); reduced confidence |
| Partial history (loader limits) | `Limitations` names the limit and the last examined time; nothing after it is claimed |
| Interrupted or in-progress recording | noted, not treated as reduced confidence |
| Samples cover < 80 % of the recording | limitation, reduced confidence |
| More samples than `AnomalyOptions.MaximumSamples` (= `HistoryLoader.MaximumSamples`, 250,000) | only the first N analyzed; limitation |
| More than 100 episodes per rule / 500 observations | ranked by severity then duration (ties by start), the rest counted in `OmittedObservations` and listed in `Limitations` |
| Cancellation | `OperationCanceledException`, no partial result; checked every 4,096 samples and per rule |

## 10. Performance and memory

Everything is single-pass or sorted-once: baselines sort one pooled array per rule; events are located by binary search; activity uses prefix sums; correlation compares observation pairs (at most 500 observations). Temporary arrays come from `ArrayPool` and are returned. Results hold no reference to the input arrays or to the `LoadedHistory`.

Benchmarks: see §12.

## 11. False-positive controls (summary)

* Idle screen: FPS and dropped frames are judged only with recorded display activity.
* Absolute **and** baseline for short-lived deviations; practical floors (100 ms RTT, 90 % CPU, 100 MB/s disk).
* Sustained conditions need duration, sample count and density.
* Minimum baseline size and duration; trimmed robust statistics; minimum scale.
* Unit and range validation; unverified raw units never interpreted.
* Disconnected stretches and gaps are boundaries, not zeros or continuations.
* Spike/sustained duplicates folded; correlations clustered; weak links (single-sample spike vs long FPS problem, CPU spike vs FPS) excluded; qualified observations excluded.
* Protocol evidence: no "Horizon", "Blast" or "uplink" wording without Horizon Blast evidence.

## 12. Verification results

### Tests

The first delivery added 117 tests (463 existing + 117 = 580, all passing locally in Release). Files: `AnomalyRuleTests` (rules, positive and negative), `AnomalyDataIntegrityTests` (time windows, gaps, disconnects, units, ordering, DST, protocols, limits, cancellation, culture), `AnomalyCorrelationTests`, `AnomalyBaselineTests`, `AnomalyHistoryIntegrationTests` (recordings written by the real writer and read by the real loader, including the 21,300-sample six-hour size and a partial history), `AnomalyPerformanceTests`.

Mapping of the required scenarios:

| # | Scenario | Main test(s) |
|---|---|---|
| 1 | healthy session, no warnings | `AHealthySessionProducesNoObservationsAndNoCorrelations`, `AHealthyRealRecordingHasNoObservations…` |
| 2 | sustained high RTT, one bounded observation | `SustainedHighRttIsOneBoundedObservationWithItsEvidence` |
| 3 | short RTT spike preserved | `AShortRttSpikeIsPreservedAsItsOwnObservation` |
| 4 | sustained packet loss | `SustainedPacketLossIsDetectedAndStatesItsDirection` |
| 5 | packet loss burst | `APacketLossBurstIsDetectedAsABurst` |
| 6 | RTT + loss correlate | `RttAndPacketLossInOverlappingIntervalsCorrelate…` |
| 7 | idle FPS is no false positive | `LowFpsOnAnIdleDesktopIsNotAnAnomaly`, `ARecordingThatIsIdleThroughout…` |
| 8 | FPS degradation with activity | `LowFpsDuringDemonstratedActivityIsDetected…` |
| 9 | CPU spike, no invented correlation | `ACpuSpikeWithoutGraphicsDegradationInventsNoCorrelation` |
| 10 | sustained CPU saturation | `SustainedCpuSaturationIsDetected…` |
| 11 | memory pressure only with valid metrics | `SustainedMemoryUtilization…`, `MemoryPressureIsNotInferredFromMissingOrUnsupportedValues` |
| 12 | disk throughput is not latency | `DiskThroughputAloneIsAnActivityObservation…`, `ADiskStallIsReportedOnlyWhen…` |
| 13 | missing Blast counters | `AMissingBlastAgentAndRdpOrCitrix…`, `ACounterThatNeverHadAValidValue…` |
| 14 | disconnected session, no zero anomalies | `ADisconnectedSessionDoesNotProduceZeroOrStaleValuedAnomalies` |
| 15 | reconnection restores eligibility | `ReconnectionRestoresEligibility…`, `GuestMetricsStayEligible…` |
| 16 | short gaps | `AShortSampleGapInsideAnEpisodeDoesNotSplitIt` |
| 17 | long gaps split | `ALongSampleGapSplitsTheObservation…` |
| 18 | duplicate / out-of-order timestamps | `OutOfOrderSamples…`, `DuplicateTimestamps…`, `WhenDuplicatesDisagree…` |
| 19 | partial recordings | `PartialInputPropagates…`, `APartialHistoryReports…` |
| 20 | baseline and outliers | `AnExtremeOutlierCannotDragTheBaselineWithIt`, `ABaselineSurvivesAQuarter…` |
| 21 | insufficient baseline | `AShortRecordingGivesInsufficientData…`, `EnoughSamplesInTooLittleTime…`, `WhenTheBaselineIsUnreliable…` |
| 22 | no alert storm | `ManyAdjacentBadSamplesAreOneObservation…` |
| 23 | stable ids and ordering | `ResultsAreIdenticalAcrossRepeatedRuns…`, `TheIdOfAnObservation…`, `ResultsDoNotDependOnTheCurrentCulture` |
| 24 | cancellation | `ACancelledTokenStops…`, `CancellationDuringTheAnalysisStopsItCleanly` |
| 25 | DST | `DaylightSavingTransitionsDoNotShiftOrReorderUtcObservations` |
| 26 | unsupported units | `UnsupportedOrUnverifiedUnitsNeverCorruptThresholds`, `SupportedUnitsAreConverted…` |
| 27 | protocol types | `AnRdpOrCitrixRecordingGetsNoHorizonSpecificWording`, `BlastEvidenceComesFrom…` |
| 28 | history limits | `TheAnalyzerRespectsItsSampleLimit…`, `ALargeNumberOfObservations…`, `AnalysisOfAHugeInputStaysWithinTheSampleLimit`, `APartialHistoryReports…` |

### Mutation checks

Each protection below was removed from the source in turn and the anomaly tests were run; every mutation made at least one test fail (18 of 18 killed). The first run of the correlation-across-boundaries mutation survived; the test `ObservationsSeparatedOnlyByASessionBoundaryAreNotCorrelated…` was added and then killed it. (Mutations are applied to a copy and reverted by script; the committed sources contain none of them.)

| Removed protection | Killed by (examples) |
|---|---|
| display-activity gate (all samples treated as active) | idle-FPS, idle-throughout, activity-ended, FPS baseline tests |
| disconnect masking | disconnected-session, reconnection, baseline-after-disconnect tests |
| missing value read as zero | coverage-ending, qualified-observation, recovery tests |
| unit validation | unsupported-unit tests, memory unit test |
| plausible-range validation | counter-error test |
| segment split at gaps | long-gap test |
| robust (median) baseline replaced by the mean | outlier, flat-session, deviation tests |
| minimum baseline samples and span | short-recording, configurable-minimum tests |
| coverage ending reported as recovery | recovery test |
| segment check in correlation | session-boundary correlation test |
| single-sample spike rejected as connection side of FPS correlation | weak-link test |
| qualified observations excluded from correlation | qualified-observation test |
| deterministic ordering | repeat-run/ordering test |
| Horizon Blast gating | RDP/Citrix and Blast-evidence tests |
| duplicate-timestamp combination | duplicate tests |
| spike/sustained suppression | alert-storm and 13 further tests |
| sustained/short distinction | 10 tests |
| ordering of unordered input | out-of-order and DST tests |

### Benchmarks

Synthetic long sessions (an RTT episode with loss, a CPU spike, an FPS drop, a 100 s gap and memory pressure every ~4,000 samples), measured by `AnomalyPerformanceTests` (steady state after one warm-up run; allocations are `GC.GetAllocatedBytesForCurrentThread` around one analysis; input arrays are not counted). Local run, Release, macOS:

| Samples | Time | Allocated | Observations / correlations |
|---|---|---|---|
| 1,200 | 1.5 ms | 0.05 MiB | 0 / 0 |
| 21,300 | 26 ms | 0.64 MiB | 25 / 5 |
| 40,795 | 43 ms | 1.15 MiB | 50 / 10 |
| 250,000 | 92–258 ms (varies with parallel tests) | 6.6 MiB | 310 / 62 |
| 250,000 + 20,000 events, 1,500 candidate episodes | 91 ms | 5.7 MiB | 100 (cap) + 1,400 omitted |

Real recording of 21,300 samples through the writer and loader: 3.5 ms, 0.48 MiB. Run time for 10× the samples grew 3.8× to 5.6× (linear). Heap growth after 25 analyses of 40,795 samples: about 0 KiB. The limits asserted in tests (25 ms/1 MiB, 250 ms/4 MiB, 400 ms/8 MiB, 2 s/32 MiB) are 6–10× above these numbers on purpose: they detect quadratic behaviour or leaks, not machine speed. 
Windows CI (run 40315379847, commit `6fe5bd1`, `windows-2025-vs2026`, Release): 580/580 tests passed, none skipped, no warnings.

| Samples | Time | Allocated |
|---|---|---|
| 1,200 | 1.9 ms | 0.05 MiB |
| 21,300 | 18.8 ms | 0.63 MiB |
| 40,795 | 62 ms | 1.15 MiB |
| 250,000 | 304 ms | 6.60 MiB |
| 250,000 + 20,000 events | 247 ms | 5.71 MiB |

Real 21,300-sample recording through writer and loader: 13 ms, 0.48 MiB. 25,000 → 250,000 samples: 22 ms → 241 ms (ratio 10.9, linear). Heap growth after 25 analyses: 15 KiB. All well inside the asserted limits.

## 13. Known limitations

* **No workload/activity column in `LoadedHistory`.** Display activity is Dirty-FPS-based; Horizon recordings without that counter get `InsufficientData` for graphics rules. The recorded workload type is user-selected and deliberately not used.
* **No disk latency, queue length, per-direction loss, jitter or bandwidth in verified units** in the history channels today. The rules exist and are tested with such channels, but real recordings report `InsufficientData` for them. Jitter and unverified bandwidth are not interpreted by design.
* **Thresholds are defaults from the live engine and engineering judgement**, not calibrated against a fleet. They are configurable (`AnomalyOptions`, `AnomalyRule`) and each rule has a version. Calibration belongs to manual Horizon acceptance (M4).
* **Baseline is per recording**, not per user or per pool; a short recording has no adaptive rules.
* **Tolerance-based correlation is a timing heuristic.** Two unrelated problems that happen to coincide within 5 s are correlated, and the text says so ("correlated timing").
* **Chaining.** Correlation clusters can chain (A overlaps B, B overlaps C). They are reported as `Chained` with the linked pairs and without a common interval; a chain can still join episodes that are minutes apart if a long episode of the other kind bridges them.
* **Merge tolerance and spike/sustained boundaries** are fixed per rule; a condition that flaps faster than the tolerance is one observation, slower is several.
* **One session, one baseline.** A recording that spans a protocol or session change is split into segments for observations, but a metric's baseline is computed over the whole recording.
* **The trimmed median is deliberately slightly low** for higher-is-worse metrics, which makes deviation detection marginally more sensitive; the practical floors compensate.
* **Findings recorded by the live engine** (`FindingDetected`, …) are attached as context events; the offline engine does not compare itself with them.

## 14. M2 integration contract

M2 (Session History panel) should only consume `AnomalyAnalysis`:

1. Load the recording with `HistoryLoader` as today; call `AnomalyDetectionEngine.AnalyzeAsync(loaded, cancellationToken: token)` off the UI thread; discard the result when the selection changes (cancellation is cooperative and cheap).
2. List `Observations` (already ordered) grouped by `Domain`; show `Severity`, `Title`, `Start`–`End` (UTC; format with `HistoryTime` in the viewer's zone), `Status`, and `Explanation`. Always show `Limitations` of an observation, and `Analysis.Limitations` / `ReducedConfidence` for the recording.
3. Show `Rules` with `State == InsufficientData` as "Insufficient data: <reason>" (never as "healthy"), and `Coverage` for the "what could be used" view.
4. Selecting an observation or correlation navigates the existing charts to `[Start, End]` (expand by the context the UI wants). `Metrics[i].ChannelId` equals `HistoryChannel.Id`, so the chart series can be highlighted; `WorstAt` is the time of the extreme value.
5. `AssociatedEvents` are existing `HistoryEvent`s: user incident markers stay independent and unchanged.
6. Identities (`Id`) are stable across reloads of the same recording and can be used for selection and export. Exports must write UTC times, the recorded unit, coverage and evidence, and escape CSV for formula injection (roadmap item 6).
7. Do not reword the explanations into causal statements. Do not hide `Qualified` observations; mark them.
