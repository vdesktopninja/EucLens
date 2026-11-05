# EUC Lens 0.5.1 — stable non-AI release: real monitoring only

Branch `release/euc-lens-0.5.1`, made from the `v0.5.0` commit `6e3ded2`. It contains none of the 0.6 AI work. Nothing is
tagged or released; publishing needs the owner's approval.

## What changed

**Demo Mode is gone from the application.** The Real Session / Demo Mode selector, the demo protocol selector and the demo
scenario selector are removed from Dashboard and Settings. The application has one data source, the real session: Horizon
Blast counters when the Windows session has them, and protocol-independent Windows metrics (CPU, memory, disk, GPU, display)
in every session.

* **Nothing can bring synthetic data back.** `preferences.json` now holds the theme only. A file written by 0.5.0 or earlier
  may also hold `Mode` (Demo Mode), `Protocol`, `Scenario` and `Workload`; those properties are ignored when read and dropped
  when the preferences are saved again. Invalid old values (for example `99`) are ignored too, instead of failing the load.
  There is no command-line option for a data source.
* **Real monitoring rejects synthetic samples.** A sample marked `IsDemo` that reaches real monitoring is discarded before it
  is shown, evaluated or recorded, and the status says so. Only the automated-validation hook
  `MainViewModel.UseSyntheticSource` (internal, not reachable from any page, preference or command-line option of normal use)
  accepts them.
* **The synthetic generator stays for automated testing only.** `MockMetricsProvider` is no longer registered in the
  application's services. It is created only by the unit tests and by the `--smoke-test` harness (`App.UseSyntheticDataAsync`,
  `BackgroundValidation`, `HistoryValidation`). The smoke harness needs it because CI runners have no Horizon Agent, and
  findings, live charts and recordings must still be exercised. No automated test was removed. Tests that switched modes now
  switch the source through the same hook.

**There is no workload selection.** The workload selectors on Dashboard, Diagnostics and Settings are removed. The frame-rate
rules use a measurement instead: how often the content changes, from the Blast dirty-frame counter.

**Indicators.** Dashboard and Settings show the *Detected protocol* (only what the session's own data shows, "Not detected"
otherwise) and the *Data availability*. The header banner reads "REAL SESSION · Horizon Blast counters + Windows metrics" or
"REAL SESSION · Windows metrics only · No Horizon Blast counters for this session". The 0.5.0 banner claimed Blast counters
in every Real Session, including sessions without them.

**Version** 0.5.1 (`Directory.Build.props`). The recording format is unchanged (`euclens.recording/1`).

## Audit of everything that depended on the selected workload

| Where | 0.5.0 | 0.5.1 |
|---|---|---|
| `DiagnosticContext.Workload`, `IsActiveWorkload` | The selected workload; *Interactive graphics* or *Video playback* = active | Removed. `ScreenActivity` (dirty FPS ≥ 5) and `FrameDemand` (dirty FPS ≥ `LowFps`, 15) are measured. |
| `graphics.fps.low` | Judged only for a declared active workload; without a dirty-frame counter raised as *activity unverified* | Judged only when the content changes at least as often as the threshold. Without the counter, not judged. Evidence lists *Dirty FPS mean* instead of *Workload (user-selected)*. |
| `graphics.bottleneck.cpu` / `.gpu` / `.network` | Built on the low-FPS condition, so on the workload | The same measured condition. Evidence lists *Dirty FPS mean*. |
| `graphics.fps.unstable` | Not applicable unless a workload was declared | Evaluated whenever the screen is changing (dirty FPS ≥ 5), its own condition, unchanged. |
| Assessment `idle-low-fps` | "Expected — workload is Idle …" | "Expected — static screen" / "Expected — the content changes fewer times per second than the threshold" / "Not judged — screen activity cannot be measured". |
| Graphics domain note | "workload is user-selected (Idle)" | "low FPS judged only while Blast dirty frames show the content changing at least as often as the threshold". |
| Frame-rate chart line and Graphics tile | Conditional on the declared workload and dirty frames | Conditional on the measured content change rate; the threshold text is always shown with *rule active / inactive*. |
| Settings → thresholds row | "Low FPS (active workloads)" | "Low FPS (while the content is changing)" |
| Tray notification text | "Low frame rate during an active workload" | "Low frame rate while the content is changing" |
| Live sampling | Overwrote each real sample's workload with the selection | Removed. The Blast provider no longer sets *Idle*; new samples carry no workload. |
| Recording format and exports | `Workload` in every sample; CSV column `workload` | `Workload` is optional: null (omitted) in new samples, kept in older recordings. The CSV column stays in place and reads `Unavailable` for new samples and the historical value for older ones. |
| Preferences | `Workload` saved and restored | Ignored. |
| Session History analysis `gfx.fps.low` | Never used the workload; judged whenever recorded dirty FPS ≥ 5, so it could report typing or slowly changing content that live Diagnostics (with Idle selected) called expected | Rule version 2: judged only while dirty FPS ≥ 15, the same gate as live Diagnostics (see "One policy for live Diagnostics and Session History"). |
| Session History analysis `gfx.dropped.high` | Never used the workload; dirty FPS ≥ 5 | Unchanged. |
| 0.3 rules `HostRules.GraphicsBottleneckRule`, `DiagnosticEngine.FrameRateRule` | Not used by the application since 0.4 | Unchanged library code, kept with their tests; documented as unused. |
| `MockMetricsProvider` | Workload shapes its synthetic FPS | Unchanged; test data only. |

No threshold value was added or changed. The existing `LowFps` threshold (15 FPS) now also states how often the content must
change for low FPS to be judged. This replaces the workload an administrator used to declare. It is not an arbitrary new
number: if the content changes fewer than 15 times per second, fewer than 15 delivered frames per second are explained by the
content itself. `ActivityDirtyFps` (5) still separates a static screen from a changing one.

## How the frame-rate rules behave now

| Measured content change rate (dirty FPS mean, 60 s window) | Result |
|---|---|
| ≥ 15: video, animation, 3D, fast scrolling | Low FPS (< 15 for ≥ 70 % of the window) is a warning; the bottleneck rules can add a possible contributor. |
| 5–15: typing, scrolling a document | Not judged against the threshold: low FPS is expected. An open finding recovers. |
| < 5: static or idle screen | Not a problem. An open finding recovers. |
| No dirty-frame counter | Not judged; nothing is claimed. An open finding closes. |

Compared with 0.5.0 in its default state (Real Session defaulted to *Idle / static content*, so frame-rate rules never ran
unless the administrator selected a workload), 0.5.1 judges low FPS by default whenever the content changes at least 15 times
per second. Compared with 0.5.0 with *Interactive graphics* selected, 0.5.1 no longer warns for slowly changing content, or
without a dirty-frame counter.

## One policy for live Diagnostics and Session History

The first 0.5.1 candidate left Session History's `gfx.fps.low` on its 0.5.0 gate (dirty FPS ≥ 5) while live Diagnostics needed
dirty FPS ≥ 15. For the same measurements the two disagreed: typing at 8 content changes and 8 frames per second, 12 changes and
6 frames, or 14 changes and 9 frames were "expected" live and a low-FPS observation in Session History (reproduced by a test
before the change). Both now judge low FPS only while the content changes at least as often as the frame-rate threshold.

| Measurements | Live Diagnostics | Session History |
|---|---|---|
| Dirty FPS ≥ 15, FPS below 15 | Warning (below 15 for ≥ 70 % of 60 s) | Observation (below 15, or below the recording's own baseline, for ≥ 30 s) |
| Dirty FPS 5 to below 15 (typing, slow scrolling) | Not judged, finding recovers | Not judged, excluded like a static screen |
| Dirty FPS below 5 | Not judged | Not judged (`NotApplicable` if the whole recording) |
| No dirty-frame counter | Not judged, finding closes | `InsufficientData`, "Display activity cannot be established" |

Why 15 and not a new number: it is the low-FPS threshold itself. Fewer than 15 content changes per second cannot fill 15 frames
per second, so a lower frame rate is explained by the content; 5 (`ActivityDirtyFps`) only separates a static from a changing
screen and stays the gate for dropped frames and for frame-rate instability. Neither threshold was changed. Tests
(`LowFpsPolicyConsistencyTests`) feed identical measurements to both engines: video, exactly 15, 14, typing, slow scrolling, a
static screen, no counter and a healthy video.

Differences that remain, on purpose:

* **Window.** Live uses the trailing 60 s (a finding must be raised as it happens, and recovers when the window clears).
  History smooths dirty FPS over ±5 s around each sample, so it can place the start and end of an episode within a recording.
  A short change in content can therefore be judged in one and not the other at the edges of an episode.
* **Adaptive baseline.** History also compares with the recording's own baseline (median − 4σ), so a drop from 60 FPS to 30
  during demanding content is an observation although 30 is above 15. Live has no baseline.
* **Dropped frames.** History has `gfx.dropped.high` (dirty FPS ≥ 5); live has no dropped-frame rule.
* **Duration and severity.** History needs 30 s and 5 samples and reports Critical when half the samples are below 5 FPS; live
  needs 70 % of 60 s and has its own severity rules.

The rule is version 2 (`gfx.fps.low`), recorded in every observation. Recordings are not changed; opening an old recording now
analyses it under the new gate, so a recording that showed a low-FPS observation under 0.5.0 for slowly changing content shows
none under 0.5.1.

## Limitations caused by removing the workload selection

* **No dirty-frame counter, no frame-rate judgement.** Sessions without Horizon Blast counters have no FPS measurement at
  all (as before). A Blast agent that does not publish the dirty-frame counter gets no low-FPS finding. In 0.5.0 a declared
  active workload produced an *activity unverified* warning there.
* **Slow delivery of slowly changing content is not reported.** Content changing 10 times per second and delivered as
  3 frames per second is a real degradation, but it is below the content-change gate. Reporting it would need a new ratio
  threshold between delivered and changed frames, which has not been validated, so none was added.
* **Dirty frames count changes, not their size.** A small fast animation (a spinner, a ticker) can count as content change.
  The interpretation of the counter rests on its name (see [DIAGNOSTICS.md](DIAGNOSTICS.md)).
* **One threshold for every kind of content.** A declared *Video playback* used to say that 24–30 FPS mattered; now 15 FPS is
  the line for all content that changes at least that often.
* **Slow delivery of slowly changing content is not reported in Session History either** (same gate, same reason).

## Compatibility

* Recordings made by 0.4.3, 0.4.4 and 0.5.0 open. A 0.5.0 Demo Mode recording still shows *Demo data* as its source.
  Historical workload values are read and exported unchanged (tests below).
* JSON and CSV exports keep their structure and column order. `workload` and `demo_scenario` stay in the CSV.
* Opening a 0.5.1 recording in 0.5.0 was not tested. Its samples omit `Workload`; 0.5.0 then reads the default (*Idle*).

## Validation

Unit tests (`StableReleaseSourceTests` and updated diagnostics tests):

* Preferences of earlier versions, including Demo Mode, unknown values and string enums, restore the theme only, start in
  Real Session, and save back as `{"Dark":true}`.
* A synthetic generator wired in as the application's source is rejected: no sample shown, evaluated or recorded.
* No view contains a data-source, demo, scenario, protocol or workload selection, and the view model has none. Dashboard and
  Settings bind the protocol and availability indicators.
* The application registers only `RealSessionMetricsProvider`. Only the smoke harness and the tests create synthetic data.
* A 0.5.0 Demo Mode recording with a *Video playback* workload opens, keeps its workload and exports it. A new recording
  carries none and exports `Unavailable` in the same column.
* Low FPS warns while the content changes 25 times per second. It does not warn for typing (8), a static screen, or without
  a dirty-frame counter, whatever workload value a sample carries. Findings recover or close when the condition goes away.
* A real session without Blast counters shows "Windows metrics only" and still measures CPU.

The Windows smoke test (published EXE, `--smoke-test`) checks the following:

* It starts in Real Session with "Windows metrics only".
* No page offers a selector, at every size, scale and theme of the layout matrix.
* Dashboard and Settings show the protocol and availability indicators.
* Changing to synthetic data and back stops a recording and never mixes samples.

The other existing sections still run: recording, history, incident markers, exports, background monitoring and the layout
matrix in both themes.

## Privacy: one optional network request

EUC Lens needs no network. The About page has a **Check for updates** button that asks GitHub's public release list when, and only when, the user presses it
(details and security limits in [ABOUT_AND_UPDATES.md](ABOUT_AND_UPDATES.md)). Nothing is sent about the computer, the user, the session or the recordings, nothing is downloaded or replaced,
and monitoring and recording never depend on it.

## Release-candidate validation and known limitations

What the automated validation covers: the unit and regression tests on Windows, the WPF design contract, the published EXE's
smoke test (pages, layout matrix, themes, recording, history interaction, typography, background monitoring), a normal launch
check, the EXE version/size/SHA-256 check and a credential scan. The whole-process memory tests run in one serial collection
so that parallel tests cannot distort a measurement; their limits are unchanged.

What it does not cover, and must be checked by a person:

* A real multi-hour Windows soak test. The automated memory tests are bounded stress tests, not proof that no leak exists.
* Real display scaling. The 125 % and 150 % cases simulate the available space; they do not change the Windows DPI.
* The Segoe UI fallback on a machine without Segoe UI Variable, and any screen reader.
* Windows *Text size* (not honoured by WPF) and real Horizon Blast counters, GPU counters and recordings from a Horizon desktop.

CI results, the tested commit and the EXE SHA-256 are in the release-candidate report.
