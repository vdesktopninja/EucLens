# Background monitoring, system tray and long-term recording (0.4.3)

EUC Lens can keep monitoring, evaluating diagnostic rules and recording to disk while its window is hidden in the
Windows notification area. This is a normal user-mode desktop application: no service, no elevation, no network, no
database. It is **not** a hidden agent: the tray icon always shows what the application is really doing, and the
process ends at logoff.

## Audit of 0.4.2 (what existed before this milestone)

| Question | Finding |
|---|---|
| Who owns the sampling loop? | `App.OnStartup` creates the cancellation token and starts `MainViewModel.RunAsync`. The loop never looked at the window, but the same tick also refreshed every card, chart and findings list. |
| Is anything tied to window visibility? | Only presentation work. `ShutdownMode` is already `OnExplicitShutdown`. The window `Closing` handler ended the process. |
| Recorder | `SessionRecorder` keeps every sample in memory (limit 86,400 snapshots, about 15 KB each). Exports run from that list. Fine for minutes, not for hours. |
| Blast reconnect | `OmnissaBlastProvider` rediscovers the session's counter instances on every sample and resets its handles when the instance set changes. A disconnect therefore already yields `Unavailable` metrics with a reason, and a reconnect resumes by itself. What was missing is recording that timeline explicitly. |
| Icons | None. The EXE, window and (no) tray had the default icon. |

## Components

| Component | Where | Responsibility |
|---|---|---|
| `MonitoringCoordinator` | Core | The single owner of the sampling schedule and of the *monitoring* state machine (Stopped, Running, Paused). One instance per process. |
| `TrayStateResolver` | Core | Pure function: monitoring state, recording state and active problems to one `TrayState` plus tooltip text. |
| `RecordingWriter` / `FileRecordingSink` | Core | Streaming, bounded, asynchronous JSON Lines writer with rotation, quota, retention, failure handling and explicit loss markers. Plugged into the existing `SessionRecorder`. |
| `RecordingCatalog` / `RecordingReader` | Core | Lists recordings (also interrupted ones), replays them as `SessionSnapshot`s for the existing JSON/CSV exports. |
| `NotificationPolicy` | Core | Turns *engine-confirmed* findings and storage problems into at most a few privacy-safe notifications (deduplicated, cooldown). |
| `BackgroundSettings` / store | Core + App | Tray, notification and recording options; validated; `%LOCALAPPDATA%\EucLens\background.json`. |
| `TrayIconService` | App | `NotifyIcon` plus context menu; reflects real state only. |
| `ApplicationLifetimeService` | App | Minimize/close to tray, restore, explicit exit, single instance, session lock/logoff events. |
| `MainViewModel` | App | Still the presenter. Gained a *presentation gate*: while the window is hidden it skips UI work and keeps evaluating and recording. |

## State machines

**Monitoring** (owned by the coordinator)

| From | Event | To | Effect |
|---|---|---|---|
| Stopped | Start | Running | sampling begins |
| Running | Pause | Paused | no samples are collected; an open recording gets a `paused` mark |
| Paused | Resume | Running | sampling resumes immediately; the recording gets a `gap` entry with the exact duration |
| Running / Paused | Stop | Stopped | an open recording is finalized first |

**Recording** (owned by `SessionRecorder` and its sink)

| From | Event | To |
|---|---|---|
| Idle | Start (only while Running) | Recording |
| Recording | Stop, mode switch, storage failure, exit, logoff | Idle (file finalized with a footer stating the reason) |

While monitoring is Paused an open recording stays open but receives nothing. The tray shows *Paused*, never
*Recording*.

**Tray state priority** (highest wins)

| # | Condition | Tray icon | Badge shape |
|---|---|---|---|
| 1 | Monitoring paused | Paused (grey) | two bars |
| 2 | Recording active | Recording (red); with an active warning an amber triangle is added in the opposite corner | filled circle |
| 3 | Active warning or critical finding | Warning (amber) | triangle |
| 4 | Monitoring running | Monitoring (green) | play triangle |
| 5 | Monitoring stopped | Idle (blue brand icon) | none |

Every state differs by shape as well as colour; the tooltip repeats the state in words.

## Risks and how they are handled

| Risk | Handling |
|---|---|
| Hours of samples in RAM | Streaming writer; only a bounded tail (600 samples) stays in memory. Queue capacity is bounded. |
| Silent data loss | A full queue or a failed write increments a loss counter that is written as a `loss` line (count, time range) as soon as writing works again, and is shown on the Recorder page. After one minute of failed writes the recording stops with reason *storage error* and a notification. |
| Disk full / access denied / share gone | Classified, surfaced, retried; never throws into the sampling loop. |
| Crash or kill | Lines are flushed at a fixed interval; a recording without footer is listed as *interrupted* and stays readable up to the last complete line. |
| Retention deleting the wrong files | Only files that match the recording naming pattern in the configured recording directory, oldest first, never the active recording. |
| Hidden monitoring the user forgot | The tray icon is always visible while the process runs; the first hide shows an explanation; the context menu has *Exit*; defaults are Off. |
| UI work while hidden | Presentation gate. Charts are not fed while hidden; after restore the live trends continue with a visible gap. |
| Duplicate collectors | One coordinator, one loop. A second process signals the first through a session-local event and exits. |

## Recording files

A recording is a set of **parts** in the recordings folder (default `%LOCALAPPDATA%\EucLens\Recordings`, configurable):

| File | Content |
|---|---|
| `EucLens-<yyyyMMdd-HHmmss>-<id8>-part001.jsonl` | one part: UTF-8 JSON Lines, schema `euclens.recording/1`. Closed parts are gzip-compressed to `.jsonl.gz` (any gzip tool opens them). |
| `EucLens-<yyyyMMdd-HHmmss>-<id8>.meta.json` | small index (counts, end reason) so the Session Recorder page lists recordings without reading them |

Line types (`t`): `header`, `session` (written only when it changes), `str` (string table: counter paths and reasons once per part),
`s` (sample), `event` (finding detected, escalated, downgraded, recovered, expired), `mark` (paused, resumed with `gapSeconds`, availability
change, session locked/unlocked, remote disconnected/reconnected, data loss with count and gap) and `footer` (end reason and totals).
Metrics are `{"v":value,"u":unit,"k":source,"x":reason,"e":error,"t":ticks since the sample,"r":raw,"z":scope,"q":status}`; a missing value
is simply absent, never `0`, and unverified raw-unit counters keep their raw value. A part without footer was interrupted; it is read
up to its last complete line and listed as *Interrupted*. About 1.5 to 4 KB per sample before compression.

JSON and CSV exports keep their schema 0.4 and are byte-identical to the exports of the same samples held in memory.

### Failure handling

| Situation | Behaviour |
|---|---|
| Folder unusable at start (access denied, share gone, disk below 256 MiB free) | The recording does not start; the reason is shown. |
| Write fails (disk full, access denied, share lost) | Samples are counted as lost; every 5 s a new part is tried; on success a `DataLoss` mark states the count and the gap. After 60 s of failures the recording ends with reason *storage error* and a notification. |
| Disk too slow (queue of 900 samples full) | Samples are dropped, counted, announced in the file and on the Recorder page. |
| Free space falls below 256 MiB during a recording | It stops before filling the volume. |
| Quota reached | Older EUC Lens recordings are removed first; if the recording alone outgrows the quota it stops (*quota reached*) and its data stays. |
| Process killed, power loss | No footer; readable up to the last flushed line (flush interval 5 s). |

Retention only touches files that match the naming pattern above in the configured folder, oldest recording first, never the active one.

## Settings

Settings, Background monitoring (`%LOCALAPPDATA%\EucLens\background.json`): minimize to tray, close button minimizes to tray, notifications,
monitoring on start (default on, as always), recording on start (opt-in), recordings folder, quota (default 1024 MiB), retention days (default 14).
Both tray options are off by default. Nothing starts with Windows.

## Notifications

Optional (off by default). Only engine-confirmed warning or critical findings (detected or escalated), an unexpected recording stop and
storage problems qualify. The same problem is announced at most once per 30 minutes, any two notifications are at least 60 seconds apart,
nothing is shown while the Windows session is locked, and the text is generic ("Sustained high round-trip time. Open EUC Lens for details."):
no user, host, address or measurement.

## Horizon session lifecycle

* Disconnect: Windows host metrics keep being collected. Blast counters become `Unavailable` with their reason (the provider re-discovers the
  session's counter instances on every sample); the recording gets an `Availability` mark that states this alone is not evidence of a network
  fault, plus `RemoteDisconnected` / `RemoteReconnected` marks from Windows session events. Nothing is invented.
* Reconnect: counters are found again automatically and sampling continues on the same timeline.
* Lock/unlock: monitoring continues; marks are written; notifications are suppressed while locked.
* Logoff or shutdown: the recording is finalized, then the process ends. The portable process does not survive logoff or restart.

## Single instance

`Local\EucLens-<user SID>` mutex and activation event. A second start signals the first and exits. Another user, or the same user in another RDS/VDI
session, is unaffected. No elevation.

## Validation

Unit tests (state machine, tray priority, notification policy, settings, recording format, failures, quota and retention, export identity,
hide/restore of the view model, an accelerated 8 h simulation on a fake clock) and the Windows smoke test (`BackgroundValidation`): icon of the
executable, tray state mapping for all six icons through the real services, minimize and close to tray, a recording while hidden, ten
minimize/restore cycles with one sampler, single-instance probe in a second process, exit with an active recording, and a measured 40 s visible
versus 40 s hidden comparison (CPU, working set, heap, lost samples, dispatcher latency) in `background-soak-result.txt`.

**Limits.** The accelerated simulation is not an eight-hour Windows soak; the CI comparison is 40 s on a shared runner. Tray menu and taskbar screenshots
depend on the runner's shell. The tray menu uses the standard Windows menu (no dark theme). Real Horizon disconnect/reconnect, DPI scaling of the tray icon
on a real display and a long unattended run on a VDI need manual acceptance (see [the Windows acceptance checklist](validation/WINDOWS_ACCEPTANCE.md)).
