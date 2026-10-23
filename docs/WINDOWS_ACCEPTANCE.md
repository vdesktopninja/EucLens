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
- [ ] Switch all protocols and five scenarios; verify rows, readouts and trends update at
      one-second cadence and demo identity is always visible.
- [ ] Low FPS + interactive workload warns; idle/office content alone does not.
- [ ] Check high-latency, GPU and congestion diagnostic evidence and health colors.
- [ ] Toggle dark/light theme on Dashboard and Recorder while sampling/recording;
      inspect chart backgrounds, axes, 1-DIP gridlines, 2-DIP strokes, timestamp/unit
      tooltips, text and combo popups. RTT color must match live and recorder charts.
      Check unavailable readouts and history gaps; a missing value must not look like zero.
- [ ] Record across scenario/protocol changes; stop; check elapsed/sample count.
- [ ] Export JSON/CSV with non-English locale and paths containing spaces;
      verify timestamps, demo flag, scenario, values and missing-value semantics.
- [ ] Cancel save dialog; test inaccessible destination; UI remains responsive and
      reports failure. Export while recording; saved snapshot remains consistent.
- [ ] New recording warns before discarding previous samples; close warns about
      unexported data; selecting No returns to the recorder for export.
- [ ] Save preferences and restart; theme and demo selections are restored.
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
