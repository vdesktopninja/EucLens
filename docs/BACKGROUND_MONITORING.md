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

## Recording format

See the "Recording files" section below once implemented.
