# EUC Lens 0.4.4 — release hardening after the independent review (historical)

> **Historical document.** This records the review of version 0.4.4 and the fixes made for it. It is kept for the project's engineering history; later versions may differ. For what EUC Lens does today, see the [README](../../README.md) and the [documentation index](../README.md).

The review of commit `4df78ac` (see the comment on Draft PR #1) found defects that had to be fixed before release. This page maps each fix to its
regression tests and records what was deliberately deferred. Nothing here changes the recording format: `euclens.recording/1` stays backward compatible
(0.4.3 recordings open, and 0.4.4 recordings carry only additive fields).

## What changed, and the tests that pin it

| Finding | Fix | Regression tests |
|---|---|---|
| **A.** File-controlled string-table id grows a list without bound | `StringTable.Set` rejects negative ids, ids ≥ 100,000 and ids more than 1,024 past the end, before allocating. The writer shares one overflow entry once a part's table is full, so a file the writer produces is always one the reader accepts. | `HistoryHardeningTests`: `AFileControlledStringTableIdNeverAllocatesWithoutBound` (2,000,000,000, −1, −2³¹, 100,000, 5,000, 99,999), `TheLegacySampleReaderRejects…`, `AnOrdinaryStringTableStillRoundTrips` |
| **B.** Unbounded line length, decompression, events, incidents, issues | `BoundedLineReader` (1 MiB lines, skipped and reported); `HistoryLimits`: 1 GiB decompressed per part, 4 GiB per recording, 100,000 timeline entries, 5,000 incidents, 50 listed problems per kind plus one summary with the true count. A limit ends the read and the recording is shown as **Partial analysis** with the reason; it is never called complete. The 250,000-sample limit is unchanged. Cancellation is checked whenever more data is fetched. | `ALineLongerThanTheLimit…`, `AFileWithoutLineBreaks…`, `ASmallCompressedFileThatExpandsPastTheLimit…`, `TheTotalReadBudget…`, `TooManyTimelineEntries…`, `TooManyMarkedIncidents…`, `FiveThousandEntriesStayCompleteAndAccessible…`, `ThousandsOfDamagedLines…`, `CancellingDuringAVeryLongLine…` |
| **C.** Compression not durable before the plain part is deleted | The compressed copy is written to a temporary file, flushed to disk, **read back and compared byte for byte in length**, renamed, and only then is the plain part removed. Any failure keeps the plain part. A crash between rename and delete leaves both; readers prefer the plain one. | `RecordingDurabilityTests`: five injected failures (create, write, flush, cut short, rename), `…OnlyAfterTheCopyWasReadBack…`, `ACrashBetweenRenameAndDelete…` |
| **D.** Finalization failures escaped the writer loop | `FinishAsync` catches storage failures at the footer, flush, close and index steps, releases the handle, completes every waiter and raises a **fatal fault** ("could not be finalized"); the recording is then an interrupted one, never a complete one. An unexpected exception in the loop raises a fault and ends the recording instead of leaving callers waiting. | `AFailureWhileFinalizingIsReported…` (footer write, flush, close), `AFooterThatCouldNotBeWrittenLeavesAnInterrupted…` |
| **E.** A timed-out durable mark stayed queued and could be written later | Three outcomes. **Saved**: flushed and fsynced. **Not saved**: definitely not in the file (never queued, withdrawn before the writer started on it, or the write failed before the line reached the stream). **Outcome unknown**: the writer had started on it when the disk failed or did not answer, so it may still appear; reported with the marker's identity, never retried. A withdrawn marker is skipped by the writer; its sequence number is not reused. | `AMarkThatCouldNotEvenBeQueued…`, `AMarkTheWriterNeverStartedOn…`, `AMarkTheDiskDidNotConfirmInTime…`, `CancellingWhileTheDiskIsStillWorking…`, `AWriteThatFailsBeforeTheLineReachedTheStream…` |
| **F.** `cts.Token` read after an await | The token is read once, before the first await; any other failure while opening is shown as an error and logged. | `SessionHistoryHardeningTests.LeavingThePageAfterTheLoadFinished…`, `ARepeatedOpenAndLeave…`, `AnUnexpectedFailureWhileOpening…` |
| **G.** Malformed numbers and enums | Enum values must be defined, gap lengths finite and within a year, every other decoding exception is a damaged line, and an IOException mid-read ends that part with a reported problem. | `InvalidNumbersAndEnumValuesAreDamagedLines…` |
| **H.** One damaged header lost the recording | A part without a readable header is skipped and reported (`DamagedHeader`); the other parts are used. An unsupported schema is still rejected, in any part, and a recording with no readable header at all is still rejected. | `AHealthyPartIsRecoveredWhen…`, `AnUnsupportedSchemaIsStillRejected…`, `ARecordingWhoseOnlyPartHasNoReadableHeaderIsRejected` |
| **I.** Stale list selection after Previous/Next | The list shows no selection when the incident's row is hidden by the filter; the filter is left as the user set it. | `PreviousAndNextIncidentNeverLeaveAStaleRow…` |
| **J.** Annotation edits lost | Unsaved edits are kept per incident while the same recording is open (including leaving the page). A save writes what was shown when Save was pressed; text typed meanwhile stays, unsaved, and is never overwritten by the older save. | `TextTypedWhileASaveRuns…`, `UnsavedAnnotationEditsSurvive…` |
| **K.** Annotation sidecar safety | Unique temporary file names, flushed to disk before the rename, one read-modify-write at a time, a file larger than 8 MiB is neither parsed nor overwritten, and a file from a newer schema is left byte for byte unchanged (the edit is kept and the user told). | `AnAnnotationFileFromANewerVersion…`, `ConcurrentAnnotationSaves…`, `AnAnnotationFileFarLarger…` |
| **L.** Recordings stamped 0.4.3 | One version source: `Directory.Build.props` (`0.4.4`), read by `AppVersion`; new recordings record it. | `NewRecordingsCarryTheApplicationVersionAndOldOnesStillOpen` (also reads a recording stamped 0.4.3 with no incident fields) |

Also: the writer re-announces a data loss that failed to reach the file (`ReopenAsync`), and the gap search no longer scans every entry for every gap.

## Deferred beyond 0.4.4

| Item | Risk | Why it waits |
|---|---|---|
| Release the loaded recording while the window is hidden in the tray (about 58 MB at the sample cap) | Footprint, not correctness | A tray-memory optimization; the page already releases on navigation |
| Culture-dependent number formatting (`N0`, `0.#`) in a few messages | Cosmetic (a decimal comma) | Localization pass |
| Gap-reason wording ("The remote session was disconnected" next to a gap) and the analyzer line at the P95 boundary | Wording | Changes deterministic analysis text; no cause is asserted by the rules themselves |
| Row automation name on the `ListBoxItem` instead of the inner grid; clicking an already-selected row; "No valid values" flash | Accessibility polish | Needs a screen-reader pass (manual acceptance) |
| Per-point chart updates, per-view statistics allocations, per-marker allocations in the strip | Possible frame cost on very long recordings | No failure at the tested sizes (21,300 samples; 250,000 not measured on Windows) |
| `.partial` and `.unreadable-*` leftovers are not counted in the quota or cleaned by retention | Disk use | They only exist after a crash or a damaged sidecar |
| Retention uses the newest file time, so annotating an old recording postpones its deletion | Arguably desirable | Document, decide with users |
| Part numbers above 999 are not recognized | Not reachable with the default 64 MiB parts and 1 GiB quota | Needs a format decision |
| Production crash handler for `async void` command and tray handlers | Theoretical: no realistic thrower found | Cross-cutting; separate change |
| Two quick incident marks finishing out of order can leave the older as "last incident" | Cosmetic (tray tooltip) | Trivial but outside the blockers |

## Still required before release approval

Manual acceptance on a real Omnissa Horizon session (see [the Windows acceptance checklist](../validation/WINDOWS_ACCEPTANCE.md)), including the items the review added: Blast counters and raw units charted in history, a
multi-hour recording with several hidden-window marks, Focus Assist, a folder of 0.4.3 recordings (retention and quota with sidecars), SmartScreen and antivirus behaviour of
the portable EXE, an offline/firewall check, high DPI across monitors, tray overflow and Explorer restart, a disconnected or read-only recordings folder, a non-UTC time zone and
a DST change, opening the CSV exports in Excel, and keyboard-only use with a screen reader.
