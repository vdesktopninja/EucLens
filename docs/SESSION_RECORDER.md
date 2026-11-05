# Session Recorder (0.5.1 presentation)

The Session Recorder page was redesigned to match Session History. **This is presentation only.** The recording engine, the recording format (`euclens.recording/1`), the sampling rate, quota and retention, the exports,
monitoring, the tray and Session History are unchanged, and no setting, file location or privacy behavior moved.

## Layout, top to bottom

1. **Title and one sentence.**
2. **Monitoring and recording** (one card):
   * Three tiles: **Recording** (Ready, Recording, Finalizing, Completed, or an error), **Duration** (`HH:MM:SS`, tabular figures) and **Samples** (the recorder's own count; samples the writer could not save are counted as "n lost" with a warning shape).
   * One line for monitoring, built from the existing detection: `Monitoring active · Real Session · Horizon Blast` (or `Windows metrics only` when the session has no Blast counters, `Monitoring paused`, `Monitoring stopped`). Nothing about the protocol is typed on the page.
   * One sentence of status and what to do next (a live region).
   * Actions: **Start recording** and **Stop recording** (two buttons that never trade places, so a double click on Start cannot reach Stop; the one that applies is enabled and prominent), one monitoring action that reads **Start monitoring**, **Pause monitoring** or **Resume monitoring**
     (the existing commands with their own conditions), **Mark performance issue**, and the two exports of the *current* recording.
3. **Live connection latency** (*Round-trip time (RTT) · latest 600 samples*): the same chart and data as before, 200 DIPs high instead of a large empty panel, with an empty state: *No RTT samples available yet.* before anything is recorded, and *RTT is not available in this session* when samples exist but carry no RTT
   (no Horizon counters) — which is a different situation from an empty recording. Nothing is drawn for a missing value: gaps stay gaps.
4. **Saved recordings**: a table with *Recorded* (local time), *Duration*, *Samples*, *Size* and *Status* (Complete, In progress, Interrupted, with a shape beside the word and the full explanation in the tooltip and the accessible name), a status filter, and **one** action bar for the selected recording:
   **Analyze recording** (primary), **Export JSON**, **Export CSV**, **Show file**, **Open folder**. The rows have no buttons.
5. **Storage and recording details** (collapsed): the recordings folder with an *Open folder* action, the storage status of the current recording, the quota and retention in effect, how recordings are stored and what an unavailable value means, how pausing is recorded, and the note for non-persistent VDI desktops.

## Behavior

* **Monitoring and recording are two things.** Pausing monitoring during a recording does not stop it: the page keeps saying *Recording*, adds *Monitoring paused* with a warning shape, and explains that the pause is recorded as a gap. There is no recording pause: the recorder has none, and none was added.
* **States are the recorder's own.** Ready; Recording; Finalizing (the recording was stopped and its end is still being written: a new recording waits for it, as before); Completed; Error (the storage sink faulted, or the recording ended by itself because of a storage problem or the quota, with the reason).
  The page never invents a state: a *Stopping* state of its own does not exist in the engine, so none is shown beyond Finalizing.
* **A repeated Start is one Start.** The button is disabled as soon as a recording runs, the command itself refuses a second request (it would otherwise raise an exception in the recorder), and a start that cannot happen reports why instead of failing. A recording is never replaced by a repeated click.
* **Selecting a recording only selects it.** It does not open a file or folder, does not navigate to Session History, does not analyze anything and does not scroll the page. There is no double-click action and no context menu: the action bar is the only place, and it is reachable by keyboard.
* **Actions explain themselves.** With nothing selected every action is disabled and the page says *Select a recording to analyze, export or find it in Explorer.* A recording without a readable file cannot be shown in Explorer, and says so. Each action carries its name and its explanation as accessible help text.
* **Sorting.** Newest first by default; the headers *Recorded*, *Duration* and *Size* sort (a second press reverses). Equal values keep a fixed order (then newest, then the recording's id), so rows never swap between refreshes.
* **Selection survives a refresh** while the recording still exists and is visible; a recording deleted outside EUC Lens disappears at the next refresh and takes the selection with it. Damaged or unreadable recordings are handled by the existing catalog (listed as interrupted, or skipped); an export of a recording whose files are gone reports the problem and the page stays usable.
* **Hundreds of recordings.** The list is built from the catalog's small summaries only (no recording content is read, no analysis runs), replaced as a whole with one notification, sorted and filtered in memory, and shown in a list of bounded height with its own scroll viewer and virtual rows, so the cost is the rows in view. The mouse wheel scrolls the list while it can and the page when the list is at its end.
* **Navigation and the tray.** The view models are created once with the application. Leaving the page, returning, hiding the window to the tray and restoring it add no subscription, start no second provider and do not touch the recording; a restore refreshes the status from the recorder. Tests count the event subscriptions before and after repeated navigation.

## What did not change, and one hardening

Monitoring providers, Blast collection, the recorder, the writer, the catalog, the exports, quota, retention, formats, file locations, the tray, Session History and its charts, About and the update check are not touched. The existing commands (`StartRecordingCommand`, `StopRecordingCommand`, the monitoring commands, `MarkIncidentCommand`, the exports, `RefreshRecordingsCommand`, the recordings' own commands) are the ones that run; their enabled conditions are unchanged.

Two presentation-level hardenings in the view models, with tests: `StartRecording` returns when a recording is already running (the recorder would throw), and it reports an `InvalidOperationException` from the recorder (a previous recording that did not finish in time) as a status message instead of letting it escape. The saved-recordings list is now replaced with one notification instead of one per recording.

## Known limitations

* The list refreshes when the page is shown, on *Refresh*, when a recording ends by itself and once when a stopped recording has been finalized; a recording that is still in progress is listed as In progress after the next refresh.
* There is no recording-level pause, no *Stopping* state of its own, and no per-recording incident or finding count in the table: counting findings needs the analysis, which the list deliberately does not run (the incident count the catalog already has is in the row's accessible name).
* The latency chart has a fixed height (200 DIPs) rather than growing with the window.
* Real Windows display scaling, a screen reader and real Horizon counters are not covered by the automated validation; the scale cases of the smoke test simulate available space.

## Validation

`RecorderWorkspaceTests` (unit, VM level, real recorder and real writer on in-memory storage): states, tiles, the monitoring line and action, repeated Start and Stop, paused-during-recording, error and finalizing states, the chart's empty state and gaps, the storage texts, sorting, ties, filter, selection, action availability and routing, exports, refresh and external deletion, damaged recordings, 1,500 recordings, subscriptions after repeated navigation and tray hide/restore, a restart.
The Windows smoke test (`RecorderValidation`, `recorder-validation.txt` in the CI log) records through the page's own buttons (pressed through UI Automation), checks every state with the typography checks, selection without scrolling or navigation, sorting through the real headers, the keyboard, the collapsed details, the narrowest real window (760 DIPs) and screenshots in both themes.
