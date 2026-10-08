# EUC Lens source UI audit and redesign handoff

Requested separately from contract extraction by the operator for step 1, “Audit UI and design contract.” This is a source inspection of the baseline at this step's starting revision, not a running-UI verdict. The shared resources have subsequently been updated; shell/views/chart rendering remain for step 2.

## Baseline observations

| Evidence | Observation | Implementation direction |
| --- | --- | --- |
| `MainWindow.xaml`: 228 DIP sidebar, 18/28 DIP sidebar padding, 24px branding, large bottom demo panel | Navigation occupies substantial width and repeats demo identity in the toolbar | 184 DIP rail, 32 DIP items, 16px icons, compact product label and persistent plain demo indicator |
| `MainWindow.xaml`: 28px content inset, 1000×700 minimum, global ScrollViewer | Content uses less available width; minimum size exceeds the effective 1366×768 desktop at 150% scaling | 16 DIP page inset; compact minimum, flexible/reflowing pages, deliberate scrolling for secondary detail |
| `DashboardView.xaml`: “Your session, in focus”, introductory sentence and separate health card | Narrative hero duplicates technical session context and consumes vertical space | Session overview with protocol/transport/session/health context; preserve demo disclosure |
| `Controls.xaml` baseline: 12 DIP card radius, 20 DIP padding; Dashboard uses overview cards and five sections of card grids | Every datum is promoted to a decorative container; layout is lengthy rather than scan-friendly | One metrics strip, two equal charts and compact label/value tables; session inventory remains accessible |
| `Light.xaml`/`Dark.xaml` baseline | Blue-tinted light surfaces and multiple navy surface levels give broad accent coverage | Neutral surfaces, subtle 1 DIP borders, restrained blue selection/action |
| Dashboard and Diagnostics inline critical hex `#ED7373` | Critical status bypasses theme resources | Replace with CriticalBrush during view redesign |
| `MainViewModel.cs`: fixed blue/green/purple series, gray labels, translucent fill | Series/axes are independent of theme switching; green suggests health while encoding FPS; recorder RTT differs from live RTT | Theme-aware presentation adapter using shared palette, no redundant fill, same RTT color throughout |
| Dashboard/Recorder CartesianChart instances have no explicit background or tooltip theme | Background and tooltip consistency are not established by view resources | Bind actual chart background/tooltip paint to theme; inspect runtime Skia plot surface in both themes |
| `ChartAnimation` is zero; geometry is zero; live/recorder retention is bounded | Existing performance choices suit continuous sampling | Preserve these choices and test theme updates without rebuilding series every sample |
| Diagnostics, Recorder and Settings headlines | “Understand the signals”, “Capture the session story”, “Make it your workspace” use promotional wording | Technical page names, concise explanatory text, structured diagnostic evidence |
| Recorder 32 DIP counters, 300 DIP chart and large vertical margins | Recording actions and history can consume most of a scaled desktop | Compact control strip with state/duration/sample count and responsive history pane; keep export actions visible |
| Repeated fixed 3/4-column UniformGrids and fixed-height charts | Narrow/scaled windows have no intentional alternate layout | Reflow at measured content width; wrap technical values; avoid dashboard-wide scaling via Viewbox |
| Custom Button/ComboBox/ListBoxItem templates | Keyboard focus/hover parity needs checking; baseline navigation hover changes border measurement | Shared compact templates, stable border geometry, visible focus, Windows keyboard smoke/acceptance checks |

No claim is made that charts actually render white, text clips, or specific controls overlap: these need Windows evidence. Their styling/layout risks are visible in source.

## Shared implementation completed in step 1

`Foundations.xaml` declares typography, spacing, geometry and layout dimensions. Light/dark dictionaries expose identical semantic Color/Brush keys, including critical status and chart grid/secondary series colors. `Controls.xaml` uses compact shared typography, panel geometry and control/navigation spacing, with metric row/strip templates and tabular numerals. `App.xaml` loads the foundations while preserving theme replacement at merged index 0. Existing resource keys and MVVM bindings remain available.

This changes the visual foundation of existing screens but does not finish the shell, overview, detail layout or LiveCharts theme adapter. The current view model still contains the old chart colors and views still contain hero copy and inline critical colors; step 2 must address them.

## Reference principles

Interpretation for EUC Lens: use a modest type hierarchy, information in aligned columns, selection connected to technical details, and monitoring history close to the current readings. This is original application design; it does not copy any reference app's layout or assets.

- [Windows typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography): hierarchy supports efficient understanding.
- [Windows spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics): coherent grouping and alignment.
- [Process Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer): focused process listing with related details.
- [Wireshark packet list](https://www.wireshark.org/docs/wsug_html_chunked/ChUsePacketListPaneSection.html): compact columns presenting technical facts.
- [Windows Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager): process/performance inspection.
- [Windows Performance Monitor](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/perfmon): monitoring and diagnostic workflows.

## Acceptance for subsequent steps

1. Implement the shell and all five views using this contract. Preserve selectors, commands and factual limitations. Present the five requested overview metrics; verify bandwidth units rather than relabeling an existing value.
2. Theme LiveCharts surfaces, text, grids, series and tooltips. Keep the VM portable and the sample engine untouched.
3. Run existing tests, Windows WPF build, portable win-x64 publish and EXE smoke through `.github/workflows/windows.yml`. Check all five pages and theme switches while sampling/recording.
4. Validate all four requested resolutions at 100/125/150% with screenshots of the actual application. Include the smallest scaled workspace, keyboard controls, recording/export, long/unavailable values and a prolonged sampling session.
5. Record evidence and limitations; do not substitute XML checks or the existing functional smoke test for visual/DPI acceptance.
