# Session history and incident timeline (0.4.4)

An administrator can mark a performance issue while a persistent recording runs (also with the window hidden in the notification area) and
later open the saved recording, see every incident on a timeline and inspect the measurements around it. Everything is offline: no cloud,
database, service, telemetry or LLM. The format stays `euclens.recording/1`; every change below is additive.

## 1. Audit of 0.4.3 (what existed)

| Area | Finding | Consequence |
|---|---|---|
| Writer | `RecordingWriter` owns one bounded channel; marks and samples travel in it in enqueue order. `WriteMarkAsync` writes `{"t":"mark","at","kind","detail","gapSeconds"}` and flushes. Marks are fire-and-forget; a failed write is only counted. | A user incident needs a **confirmed** write (no false "saved"), so it gets an awaitable, fsync-ed path next to the fire-and-forget one. |
| Mark kinds | `RecordingMarkKind` is written by name (`Enum.ToString`). Nothing in 0.4.3 parses marks back; the catalog reads `header`, `s`, `event`, `footer`. | A new kind `UserIncident` and extra optional fields (`id`, `seq`) are invisible to a 0.4.3 reader. No schema bump. |
| Events | `event` lines are written from `sample.Diagnostics.Events` just before the sample line, so the same transition exists twice in a file (standalone line and inside the sample body). | The history reader must deduplicate by (rule, transition, timestamp). |
| Reader | `RecordingFormat.ReadSamples` yields samples only, and ends a part silently at **any** unparsable line. `ReadLines` is the generic scan the catalog uses. | A timeline reader is new. It distinguishes the supported recovery (a truncated *last* line of an interrupted part) from corruption in the middle of a file, and reports the latter. |
| Parts | A part exists as `.jsonl`, and for a moment also as `.jsonl.gz` while it is compressed (`.gz.partial` is renamed first, then the plain file is deleted). The catalog prefers the plain file. | One representation per part number, plain first, fallback to `.gz` if the plain file vanishes while reading. Never both. |
| Identity | File names carry only the 8 hex digit short id; the full id is in the header. | The annotation sidecar is keyed by short id in its name and by the full id inside. |
| Catalog | `RecordingSummary` has warnings/criticals/lost but no incident count. Interrupted recordings are scanned from the first part not covered by the index. | `Incidents` is added (optional trailing parameter) to footer, meta, summary and the scan. |
| Charts | `ChartData` (no LiveCharts types) + `ChartObjects` (own series/axes per chart view, weak subscriptions) + `LiveChart.Data`. Axis labels assume local `DateTime` ticks. | History charts use these classes unchanged except for one additive hook: a time formatter, so the axis can run on UTC ticks and still show correct local time across a DST change. |
| Navigation | `MainViewModel.Pages` drives the list box and `DataTemplate`s in `App.xaml`. The smoke harness cycles every page. | Session History is a seventh page; the existing matrix therefore also lays it out. |

## 2. Smallest compatible data model extension

```
{"t":"mark","at":"2026-10-23T12:23:18.4120000+00:00","kind":"UserIncident","detail":"Performance issue marked by the user.","id":"<guid>","seq":3}
```

* `RecordingMarkKind.UserIncident` (appended; names are serialized, ordinals never are).
* `RecordingMark` gains optional `IncidentId` and `Sequence`.
* `footer` and `meta.json` gain an optional `incidents` count. Absent means 0 (all 0.4.3 files).
* Annotations never rewrite a recording: sidecar `EucLens-<stamp>-<id8>.annotations.json`, schema `euclens.annotations/1`
  (`recordingId`, per incident id: category, description ≤ 500 characters, `updatedUtc`). Written atomically (temp file, rename). It is
  part of the recording's file group, so retention removes it together with the recording and nothing else.
* Nothing personal is added: the detail text is constant; no user name, address, clipboard or window title.

## 3. Incident persistence path

`Tray menu / Recorder page → MonitoringService.MarkIncidentAsync → SessionRecorder.MarkIncidentAsync → RecordingWriter.MarkDurableAsync`

1. Under one lock the recorder takes the UTC time from its `TimeProvider`, creates the id and sequence number and **enqueues** the mark. The lock keeps
   concurrent clicks in timestamp order.
2. The writer task writes the line in channel order (so it sits correctly between samples, also across a part rotation), flushes and
   `fsync`s, then completes the caller's task.
3. Result: `Saved`, `NoRecording`, `NotPersistent` (in-memory recording) or `StorageFailed` with the reason. A storage fault in progress,
   a full queue that does not drain, or a write exception is `StorageFailed`; the UI never shows a success message in that case.
   The marker is not retried silently: the administrator is told to mark again.

## 4. Reading history

`HistoryReader` streams a recording's parts once and yields `HistoryRecord`s (header, session change, sample, event, mark, footer) with
**one** representation per part. `HistoryLoader` consumes the stream on a worker thread and builds an immutable `LoadedHistory`:

* `Timeline`: events from standalone `event` lines plus transitions found only inside sample snapshots, deduplicated; marks;
  recording start/end; session changes (never with identity details). Sorted by UTC time, file order as tie-break.
* `Incidents`: user incidents with the recorded diagnostic state (assessments, active finding ids) and the nearest samples before and after.
* Columnar series for the metric channels (UTC ticks plus one `double[]` per channel, `NaN` = no valid value). A channel that never had a
  valid value is not offered. Raw counters keep their unit string and are flagged "unit unverified"; nothing is converted.
* Issues: damaged lines, missing parts, truncated tail (informational), unreadable compressed data, the 250,000 sample cap. Incompatible
  or unreadable recordings raise `RecordingFormatException` with a plain message.
* Memory: about 8 bytes × channels per sample (8 h at 1 Hz ≈ 4 MB). Raw lines are never kept.

## 5. Statistics and bounded visualization

* Statistics over a time range use valid values only: count, missing count, minimum, P50, P95, maximum. Percentiles interpolate linearly between closest
  ranks, like the diagnostic engine. Missing and unavailable samples are counted, never averaged.
* Downsampling (`PeakPreservingDownsampler`): if the range holds at most `maxPoints` valid samples they are all shown. Otherwise time buckets emit the **minimum and
  maximum** (with their own timestamps, in time order), so a one-second RTT spike survives an eight hour view. Every missing-data run and every
  interval longer than the gap threshold ends the current bucket and inserts a gap point (shown as a break, not as zero). Incident times are
  forced bucket boundaries. Output is at most about 2 × buckets + gaps points.
* Zoom re-queries the in-memory columns for the visible range, so the resolution rises as the range shrinks. No file is rewritten.

## 6. UI and interaction model

Session History is page 7 (after Session Recorder). "Analyze recording" on each saved recording opens it and navigates there.

```
Recording picker · Refresh · Open folder
Overview: state (Complete / Interrupted / Recording in progress), start, end, duration, source, protocol, samples, parts, warnings,
          criticals, incidents, lost samples, data coverage
Range bar: Entire session | Last 60 min | Last 15 min | Custom   Zoom − / + / Reset   ◀ Incident ▶   Context ± 2 min
Timeline strip: whole session, event ticks (distinct shapes), viewport, drag to select, ←/→ pan, +/− zoom, Home reset
Charts (shared time axis, aligned plot areas): Connection · Graphics · VDI Guest; one row per metric that exists, stats P50/P95/Max/available/missing
Events (list, filter) │ Selected incident: description + category, recorded state, observed changes, evidence (expandable)
```

* Selecting an incident centres the viewport on it with the configured context (default ±2 min).
* Observed changes are deterministic and labelled as such: per channel, the maximum (or minimum for FPS) inside the context window is compared with
  that channel's own session P95 (or P5) and median; only a clear excursion is reported. If nothing stands out the text says so. It never names a
  cause: "correlation is not proof of causation".
* Recorded findings are taken from the file. Nothing is re-evaluated with newer thresholds.
* Keyboard: every action is a button; the timeline strip and the chart area are focusable and take the arrow keys; the event list is a normal list.
* Event icons are vector paths with a text label (never colour alone).

## 7. Lifecycle, threading and memory

* One `CancellationTokenSource` per open; opening another recording or leaving the page cancels it. The load runs under `Task.Run`; only the finished
  immutable result is handed to the UI thread. Tests assert cancellation completes promptly and no task is left running.
* The history view model owns `ChartData` instances built on demand. Charts create their own LiveCharts objects (`LiveChart.Data`) exactly as before; no
  static event subscription is added; the page and charts are collectable after navigation (the existing soak style check is repeated for the history page).
* History never touches the live collection charts, the sampling loop or the recorder. Opening a recording that is still being written reads what is on disk.

## 8. Risks

| Risk | Handling |
|---|---|
| Marking while storage fails | `StorageFailed` is shown honestly; the recording's own data-loss accounting continues. |
| Two writers: reader sees a half-written last line of an active recording | Truncated tail is the supported recovery; shown as "recording in progress" or "interrupted". |
| Duplicate plain/gz part | One representation per part number. |
| Duplicate events | Dedup key rule + transition + timestamp. |
| Timestamps across DST | Everything is stored and plotted in UTC; local text is converted per instant and shows the UTC offset where a local time is ambiguous. |
| Old recordings | No new required field. A recording without incidents is valid. Tests build 0.4.3-shaped files. |
| Large recordings | Sample cap with a visible warning; columnar storage; peak-preserving buckets. |
| LiveCharts retention | Unchanged ownership model; history charts are plain `ChartData` consumers. |
| Misleading analysis | Only recorded findings and clearly labelled statistics; "no abnormality" is reported as such. |

## 9. Validation

Unit tests cover incident persistence, ordering, rotation, failures, the history reader (parts, gzip, interrupted, duplicates, 0.4.3 files, corruption),
statistics, downsampling, annotations, cancellation, memory and repeated navigation, and one end-to-end path (start, mark, rotate, stop, reopen, find, inspect).
The Windows smoke test opens saved recordings, switches between them, zooms and selects ranges and incidents, checks that every chart shares the time range, runs both themes
and keyboard navigation, repeats open/close and checks retained memory, and saves screenshots.
