# Windows acceptance checklist

Record Windows build, VDI agent/protocol version, DPI, GPU/driver, artifact commit
and results. Use a standard user. All metrics remain synthetic, even within VDI.
These checks have not been performed on the Linux development host.

- [ ] Download/copy the self-contained EXE to a machine without .NET installed.
- [ ] Run offline without elevation; verify startup, native extraction and charts.
- [ ] Run `EucLens.exe --smoke-test`; check exit 0 and `smoke-result.txt` PASS.
- [ ] Inspect all five views at 1366×768, 1536×864, 1920×1080 and
      2560×1440 at 100%, 125% and 150% DPI. Capture actual application screenshots
      in both themes, including 1366×768 at 150% (about 911×512 DIPs before chrome).
      Verify equal charts at wide widths, one-column charts at narrow widths,
      reflowing technical sections, full text and operable selectors/recording actions.
      Extended detail may scroll; primary readouts and controls must remain accessible.
- [ ] Navigate all five pages using mouse and keyboard; no blank/dead controls.
- [ ] No page offers a data-source, demo protocol, demo scenario or workload selection; Dashboard
      and Settings show the detected protocol and the data availability (0.5.1).
- [ ] In a Horizon Blast session the banner reads "Horizon Blast counters + Windows metrics"; in an RDP
      or console session "Windows metrics only"; rows, readouts and trends update at one-second cadence.
- [ ] Low FPS while the content changes at least 15 times per second (Blast dirty FPS ≥ 15, e.g. video) warns;
      an idle screen or typing in a document does not.
- [ ] Check high-latency, GPU and congestion diagnostic evidence and health colors.
- [ ] Toggle dark/light theme on Dashboard and Recorder while sampling/recording;
      inspect chart backgrounds, axes, 1-DIP gridlines, 2-DIP strokes, timestamp/unit
      tooltips, text and combo popups. RTT color must match live and recorder charts.
      Check unavailable readouts and history gaps; a missing value must not look like zero.
- [ ] Record; stop; check elapsed/sample count.
- [ ] Export JSON/CSV with non-English locale and paths containing spaces;
      verify timestamps, data source, values and missing-value semantics (workload: Unavailable).
- [ ] Cancel save dialog; test inaccessible destination; UI remains responsive and
      reports failure. Export while recording; saved snapshot remains consistent.
- [ ] New recording warns before discarding previous samples; close warns about
      unexported data; selecting No returns to the recorder for export.
- [ ] Save preferences and restart; the theme is restored. A preferences.json from 0.5.0 that
      selected Demo Mode starts in Real Session.
- [ ] Corrupt preference JSON; defaults load and warning is logged locally.
- [ ] Inspect memory/CPU over a long run and after repeated page/theme switches.
- [ ] Verify chart rendering in Citrix/Horizon/RDP sessions with restricted GPU
      access and software rendering; record failures for next milestone.
- [ ] Verify no service, outbound connection, telemetry or administrator prompt.

## Background monitoring and tray (0.4.3) — manual acceptance

CI cannot judge these; please check them once on a real Windows 11 VDI session:

1. The EUC Lens icon is recognizable on the taskbar, in Alt+Tab and in the notification area at 100 %, 125 % and 150 % display scaling (16, 20 and 24 px tray icons).
2. Each tray state is distinguishable at a glance: idle (no badge), monitoring (green play), recording (red dot), warning (amber triangle), paused (grey bars); recording with a problem adds the amber triangle in the opposite corner.
3. Enable *Minimize to system tray*, start a recording, minimize: the taskbar button disappears, the tray icon stays and its tooltip says "recording". Work for a while, restore from the tray (left click or double click): the current state is shown immediately.
4. Enable *Close button minimizes to tray*: the close button hides the window; *Exit EUC Lens* in the tray menu asks about the running recording, finalizes the file and removes the icon (no ghost icon).
5. Start EUC Lens a second time: no second instance appears, the running one comes to the front. A second Windows user or session is not blocked.
6. Disconnect and reconnect the Horizon client while recording: the recording shows availability and remote disconnect/reconnect marks, no invented RTT/FPS, and Blast counters return after reconnect.
7. Lock and unlock the session: monitoring continues; no notification is shown on the lock screen.
8. Point the recordings folder at a share and disconnect it: a storage problem is shown, samples are counted as lost, and the recording stops with a clear reason after a minute.
9. Leave a recording running unattended for hours with the window hidden and compare Task Manager CPU and memory with the visible window.

## Session History workspace (0.5.1) — manual acceptance

1. Open a recording with findings in several areas: the eight overview tiles read correctly (date, duration, protocol, status, samples, data coverage, findings, incidents); *Recording details* holds the rest, including the Recording ID.
2. The performance timeline shows each finding on the track of its area at the time you expect, with gaps in their own row; compare two findings with the charts below. Click a bar: the list scrolls to its line and selects it; the charts and the page stay where they are (press *Show in charts* to move them).
3. Open a recording in which one rule fired several times (guest CPU, for example): one group line with its count, worst severity, highest value and total affected time. Open it with the mouse and with the keyboard (Right, Enter); each occurrence selects on its own; with *Show in charts* the charts show its own interval.
4. Overlapping findings are in separate lanes and none hides another. With an 8-hour recording the narrowest findings can still be seen and clicked.
5. Severity and Area filters are small and change the list and the timeline together; the four export buttons are in one *Export* area and each writes its file.
6. 100 %, 125 % and 150 % scaling on 1366×768 and 1920×1080, light and dark, on a real display: nothing clipped, the overview and the timeline readable, every control reachable with the keyboard, a screen reader announces the timeline, the tiles, the group state and each finding line. (The automated scale cases only simulate the available space; this step is the real DPI check.)
7. Open a recording made by an earlier version: it opens and shows its findings, gaps and incidents.
8. Performance findings, nothing selected: the details pane previews the most severe finding under a "Preview" notice; the charts and the timeline still show the whole recording and *Show full recording* is disabled. *Select and show in charts* selects it and moves the charts; Escape in the list brings the preview back.
9. Choose a CPU group, then one of its occurrences from the compact occurrence list: the key measurement, the duration and the time are large and readable, *Show in charts* shows that occurrence's own interval, and the four disclosures (evidence, threshold, limitations, technical detail) open and close with the mouse and the keyboard. Compare the technical detail's observation ID with an export of the findings.
10. Choose a packet-loss finding: the same layout, with its own measurement. At 1366×768 and 1920×1080 the list and the details sit side by side; at 125 % and 150 % scaling the details are under the list; no value is cut off in either theme.

## Selection, navigation and typography (0.5.1) — manual acceptance

1. Session History with findings: click lines, occurrences, a group, a bar on the performance timeline, and use Ctrl+Left and Ctrl+Right. The page does not scroll, the charts keep their range (try after dragging a range of your own), nothing is highlighted on the charts; the bar and the line are marked and the details change.
2. Click **Show in charts**: the charts go to that finding's interval, its interval is shaded, the evidence chart is emphasized and the page scrolls to the charts. Select another finding: the charts stay until you press the button again. **Show full recording** returns to everything. Escape clears the selection and keeps a range you chose.
3. Times: the line under the timeline and the findings says whether times are UTC or local time with the offset; a recording that crosses a clock change names both offsets. The tile says *When (local)*.
4. Typography on every page (Dashboard, Diagnostics, Advanced Metrics, Session Recorder, Session History, System Information, Settings) in both themes at 100 %, 125 % and 150 % display scaling on 1366×768 and 1920×1080: nothing clipped, overlapping or hidden, no sideways scrolling, text comfortable to read, numbers aligned. Windows 11: the font is Segoe UI Variable (check in the smoke result: `Typography: body stack … resolves to …`); Windows 10: Segoe UI.
5. Tab through a page and the navigation: the focus is always visible (ring or accent border) and moves in reading order; a screen reader announces headings, tiles, lines, groups and the preview.
6. Windows *Settings → Accessibility → Text size* is not applied by WPF applications (display scaling is). Confirm that is acceptable, or ask for it.

## Session history and incident timeline (0.4.4) — manual acceptance

1. Start a recording, minimize EUC Lens to the tray, right-click the tray icon: *Mark Performance Issue* is enabled. Click it; a short notification confirms the time. Without a recording the item is disabled and says so. Unplug or fill the recordings location and mark again: the notification says the marker could not be saved (it never says saved).
2. Mark several issues over a longer period, stop the recording, open **Session Recorder → Analyze recording**. Every marker is on the timeline strip and in the event list at the time you clicked; the first and last samples around each are in *Recorded evidence*.
3. Select an incident: the charts centre on it (±2 min by default). Check that what the text says matches the charts (a spike it names is visible), that a calm interval says nothing stood out, and that missing measurements are stated, not drawn as zero.
4. Drag across the charts, use Ctrl + mouse wheel, the preset, custom range and zoom buttons; all charts move together and the cursor line follows the pointer in every chart. Use the keyboard (arrows, + and −, Home, Page Up/Down) with the charts or strip focused.
5. At 100 %, 125 % and 150 % display scaling and in both themes, check that the charts, the strip, the event icons and their labels are readable at 1366×768 and 1920×1080.
6. Open a recording made by EUC Lens 0.4.3 (no incidents) and one that was interrupted (kill the process while recording): both open, the second is labelled *Interrupted* and never *Complete*.
7. Open a real multi-hour Horizon recording: it loads without freezing the window, memory stays moderate, short RTT or CPU spikes are visible when zoomed out, and zooming in shows every sample.
8. Annotate an incident, close and reopen the page and the application: the category and description are still there; the recording file itself is unchanged (check its timestamp).
9. Daylight-saving change: a recording that spans the clock change shows distinct local times (the offset is shown for the repeated hour) and stays in order.
