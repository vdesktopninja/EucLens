# Step 2 — implemented WPF redesign

Implemented on the existing feature branch `cez/30c36cd6`. No push, base-branch merge or remote action was performed.

## Result

- Shell: 184-DIP navigation, 14-DIP Segoe MDL2 glyphs, compact selection, persistent plain demo disclosure, sample timestamp and status. Default light theme; saved dark preferences still restore.
- Overview: technical session identity and textual health assessment; one RTT/FPS/current-bandwidth/GPU/CPU strip; protocol/scenario/workload selectors; equal network and graphics histories; structured Connection/Graphics/GPU/System rows. Complete session inventory remains in System information.
- All other views use the shared typography, compact panels/rows and semantic theme resources. Recorder state, counters and export actions appear before history and storage details. Diagnostics retains evidence, possible cause and action for every rule.
- `Presentation/AdaptiveColumnsPanel.cs` measures equal-width panes in DIPs and reflows charts/technical sections with available content width. Chart breakpoint is 680 DIPs; overview details use up to four 250-DIP columns. Extended detail deliberately scrolls at narrow/scaled sizes; content is not scaled with a Viewbox. Window minimum is 760×420 DIPs, initial size clamps to the primary work area, and the manifest declares PerMonitorV2 DPI awareness.
- `Presentation/ChartTheme.cs` maps dynamic WPF semantic brushes to chart/tooltip/axis/series Skia paints on load and resource changes. No per-sample theme rebuild, additional dependency, point markers or area fill. RTT uses the same blue in live and recorded histories; FPS uses the neutral secondary color. Existing zero animation and 120/600 history bounds remain. Current chart readouts explicitly display Unavailable when appropriate; null history values remain gaps.
- Overview bandwidth uses `Connection.BandwidthConsumption` (Mbps); availability/unit display remains owned by the metric model. Added one regression test sampling the production VM to verify actual bandwidth/session/readout mapping. Metrics engine, demo provider and recording/export algorithms are unchanged.

The chart adapter uses the existing [LiveCharts tooltip paint/formatter API](https://www.livecharts.dev/docs/wpf/2.0.0/samples.general.customTooltips). DPI declarations follow [Microsoft's manifest guidance](https://learn.microsoft.com/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process).

## Local evidence

Temporary SDK: `/tmp/euclens-dotnet/dotnet` (10.0.401), Linux host.

- `dotnet build EucLens.sln -c Release --no-restore`: 0 warnings/errors, including final work-area clamp and DPI manifest.
- `dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=step2-tests.trx' --results-directory TestResults`: 45 passed, 0 failed/skipped. Ignored local TRX at `TestResults/step2-tests.trx`.
- `python3 tools/ux-contract.py --refresh` (operator informed), followed by `--check`: matching themes, valid XML/resource names; 78 tokens and 29 components. Human manual section preserved; screen state now records implementation pending Windows visual acceptance.
- `git diff --check`: passed.
- `dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64`: succeeded. Republished with `--no-restore` after the final WPF changes. Output contains only the self-contained `EucLens.exe` (PE32+ Windows x86-64 GUI executable, approximately 178 MB).
- EXE SHA-256: `f83a474972c28d75390d49bfe10974e4ec3ad355a07852214a7bbce7bacce42e`.

## Step 3 ownership and evidence limits

The Linux cross-build and portable publish do not establish Windows execution, chart rendering, keyboard/focus behavior, clipping, DPI or visual approval. No actual application screenshots or GitHub Actions execution are claimed.

Run the existing `.github/workflows/windows.yml` on this feature revision: contract/build/tests, self-contained publish, all-view/theme/recording/export/binding smoke and downloadable EXE evidence are already configured. Then complete `docs/WINDOWS_ACCEPTANCE.md` at all four requested physical resolutions and 100/125/150% scaling in both themes. Include initial launch in the smallest scaled workspace, repeated page/theme switches, chart tooltip units, unavailable metrics, actual recording/export controls, and movement between monitors with different DPI. The startup clamp uses `SystemParameters.WorkArea`; secondary-monitor placement needs particular attention during acceptance. Capture actual WPF screenshots and fix any issues found before claiming full visual acceptance.
