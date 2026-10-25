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
* Keyboard: every action is a button; the timeline strip and the chart area are focusable and take the arrow keys; the event list is a normal list (arrow keys, Home and End move the selection and scroll it into view).
* The event list is virtualized: every entry is in the list, but only the rows in view (plus one above and below) have WPF containers, which are recycled
  while scrolling. The list has its own bounded viewport (520 DIP) and scroll viewer; the mouse wheel passes to the page at either end. Filtering or
  opening another recording replaces the list with one collection notification. A selection made in code (marker, Previous/Next incident, filter change)
  is scrolled into view even when its row does not exist yet. Measured on a recording with 5,005 entries: see `history-events-result.txt` in the Windows
  validation artifact (the numbers for the earlier, non-virtualized list were +203.9 MiB managed heap, a 2.8 s slowest frame and 5,005 containers).
* Event icons are vector paths with a text label (never colour alone).

## 6b. Limits and damaged files

A recording file is never trusted for how much it may cost: lines are read through a bounded reader (1 MiB per line), at most 1 GiB is read from a part and 4 GiB from a
recording, at most 100,000 timeline entries and 5,000 incidents are kept, and problems are listed 50 per kind with one summary line carrying the true count. A limit ends
the read; the page then shows **Partial analysis** and says which limit it was. Parts with a damaged header are skipped and reported; an unsupported schema is rejected. See
`HARDENING_0.4.4.md` for the full list of fixes and what was deferred.

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

## 9. Components

| Component | Where | Responsibility |
|---|---|---|
| `RecordingMark.UserIncident`, `IncidentMarker`, `IncidentResult` | Core | The structured marker and the honest outcome: Saved, NoRecording, NotPersistent, StorageFailed. |
| `RecordingWriter.MarkDurableAsync` | Core | Queues the mark in sample order, writes, flushes and fsyncs, and completes the caller only then (or reports why not). |
| `SessionRecorder.MarkIncidentAsync` | Core | UTC time from the injected `TimeProvider`, new id, sequence number; one lock keeps concurrent clicks in order. |
| `MonitoringService.MarkIncidentAsync`, `CanMarkIncident` | Core | The one entry point for the tray menu and the Recorder page; raises `IncidentCompleted` for every outcome. |
| `HistoryReader`, `HistoryLoader`, `LoadedHistory` | Core | One representation per part, damage reporting, event deduplication, compact columns, range queries. |
| `HistoryStatistics`, `PeakPreservingDownsampler` | Core | Valid-only statistics; min/max buckets with gap points and forced boundaries at incidents. |
| `IncidentAnalyzer` | Core | The deterministic description around an instant (see section 6). |
| `RecordingAnnotationStore` | Core | The versioned annotation sidecar. |
| `HistoryExport` | Core | The optional, separate timeline and incident CSV. |
| `SessionHistoryViewModel` and items | App | Opening, range, selection, charts (`ChartData` per metric), cancellation, release. No WPF types. |
| `HistoryTimelineStrip`, `HistoryChartOverlay` | App | Two small elements that draw on the history time axis; pointer and keyboard; no chart and no view model inside. |
| `HistoryLayout` | App | The shared plot margins that make every chart and the overlay agree on every instant. |
| `ChartData.TimeLabel` | App | The one additive hook in the chart classes: UTC ticks on the axis, local text per instant. |

## 10. Validation

**Unit tests** (all synthetic data, no Horizon Agent, no GPU): incident persistence, ids, ordering, rotation, failures and the no-false-success rule; the history reader on multi-part
compressed recordings, duplicates, interruption, damage, missing parts, unsupported formats and files shaped like 0.4.3; statistics, N/A handling and raw units; downsampling (a one second
spike survives an eight hour view, gaps stay gaps, bounded output); annotations; analysis wording; time zones across a clock change; cancellation; bounded memory with an eight hour fixture
(28,800 samples, several compressed parts); repeated open and leave; the view model (range, zoom, incident selection, annotation, release, switching recordings); and one end-to-end path
(start, mark, continue, rotate, stop, reopen, find, inspect).

**Windows smoke test** (`HistoryValidation`, after the background validation, every section isolated and logged to `history-validation.txt`): recordings written through the real writer with
rotation and compression; Analyze from the Session Recorder list; charts on one range with aligned plot areas that match the overlay's mapping; zoom, presets and custom range; real routed mouse
events (hover, drag, Ctrl + wheel, click on an incident marker) with the harness supplying the pointer position; incident selection and annotation; keyboard navigation; both themes and every
event shape; switching recordings; twelve open and leave cycles with a retention check (released views and LiveCharts objects must be collectable, managed heap must not keep growing);
a six hour recording with measured load time and memory. The tray action is exercised in the background validation with the window hidden. Screenshots go to the evidence folder.

## 11. Limits

* The six hour recording in CI is an accelerated fixture written in seconds; it demonstrates loading, bounded memory and peak preservation, not a real six hour run.
* Real mouse input is simulated by routed events, not by a physical pointer; high-DPI pointer accuracy needs the manual acceptance list.
* Histories larger than 250,000 samples (about 69 hours at 1 Hz) are loaded up to that point and say so.
* A failed incident marker is not retried by itself: the user is told and marks again.
* The analysis compares an interval with the same recording; a short or constant recording gives "not enough data" or "nothing stood out", never a cause.
* Tray menu and balloon text are standard Windows UI; the confirmation can be suppressed by Focus Assist, in which case the Session Recorder page still shows the last incident.
