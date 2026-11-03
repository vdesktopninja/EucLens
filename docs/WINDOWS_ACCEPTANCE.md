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
