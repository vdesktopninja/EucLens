# MVP validation evidence (historical)

> **Historical record.** This page documents the validation of the first MVP in October 2026. The Windows CI runs it names were made in the maintainers' private development repository and cannot be opened from here. The current validation is the Windows CI workflow ([`.github/workflows/windows.yml`](../../.github/workflows/windows.yml)), the [0.5.1 audit](STABLE_RELEASE_0.5.1.md) and the [Windows acceptance checklist](WINDOWS_ACCEPTANCE.md).

Validated on 2026-10-08 on Linux x64, .NET SDK 10.0.401, branch
`feature/euc-lens-mvp`. This is a cross-build, not execution of the Windows UI.

## Commands and results

```sh
dotnet build EucLens.sln -c Release -m:1
# Build succeeded: 0 warnings, 0 errors

dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release \
  --logger 'trx;LogFileName=tests.trx' --results-directory TestResults
# Passed: 44; failed: 0; skipped: 0

dotnet publish src/EucLens.App/EucLens.App.csproj \
  -p:PublishProfile=Portable -o artifacts/win-x64 -m:1
# Publish succeeded
```

The output directory contains exactly `EucLens.exe` (186,225,718 bytes).
Final EXE SHA256: `94d9847a86ff29725d66190ab753cd28d72a7d98aceb6adbb3d6074600dbd769`.

`file` identifies an x86-64 PE32+ Windows GUI executable. Single-file generation
includes the Windows runtime, WPF and native chart dependencies. These facts
verify packaging; they do not prove launch/rendering on Windows.

Project/XAML XML parsing and `git diff --check` passed. No proprietary RDA source
or real vendor metric collection is present. One parallel MSBuild publish attempt
ended with child-process errors on this host; the single-node retry succeeded.
No compiler warnings were suppressed to accommodate chart dependencies; the app
uses a Windows 10 19041 target to select their modern .NET assets.

## Test coverage

- All three simulated protocols, explicit demo metadata and timestamps.
- All five scenarios, time-series variation and bounded metric ranges.
- Provider availability, initialization, cancellation, disposal; all vendor
  adapters unavailable and rejecting collection.
- Available zero vs unavailable metrics; nonfinite-value rejection.
- Health for every scenario, contextual FPS with correlated frame drops.
- RTT, GPU-memory and bandwidth threshold boundaries.
- Unavailable rule inputs, nonpositive bandwidth capacity, empty engine and
  known critical findings alongside unavailable inputs.
- Start/stop/reset recording, elapsed time freeze and maximum sample cap.
- JSON timestamp/demo/status retention, CSV locale/quoting/unavailable semantics.
- Cancelled export preserves existing destination and removes temporary files.

## Independent review

A separate read-only review rebuilt, tested and cross-published the committed
solution. It found one confirmed data-loss risk: an export from an old recording
could mark a newly started recording as saved. Export completion now checks the
recording generation and keeps a monotonic saved-sample count within that
recording. Five additional regression cases compile the actual viewmodel and
commands on Linux without WPF: JSON/CSV completion across recording replacement,
additional samples during export, out-of-order export completion and failed
export. All pass. The binding-error listener now attaches before constructing
the smoke-test window. No other confirmed blocker was reported.

## Windows CI verification

On 2026-10-08 the branch was pushed, with user authorization, and
Windows CI run 37803672588 (private development repository)
passed at commit `02c69b9`. Runner: Windows Server 2025 (10.0.26100), image
`windows-2025-vs2026` 20260925.250.1, SDK 10.0.401.

- Restore/build: passed, zero compiler warnings/errors.
- Tests: 44 executed and passed; zero failed, skipped, aborted or timed out.
- Portable publish: passed; output asserted to contain exactly `EucLens.exe`.
- Published WPF EXE smoke: exited successfully; report states
  `PASS: startup, sampling, all five views, themes, recording, JSON/CSV export, no WPF binding errors.`
- Application and TRX/smoke evidence artifacts uploaded successfully.

The initial run emitted an action-runtime deprecation notice for the old
artifact upload action. The workflow now uses the maintained v7 action; the
final revision will run the same Windows checks again. The downloaded Windows
artifact and evidence are retained locally under `artifacts/windows-ci`.

This validates Windows startup and automated UI paths, not interactive visual
acceptance in every VDI platform. Manual DPI/accessibility/file-dialog and
Citrix/Horizon/RDP graphics checks remain under
[WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md). Vendor research, import/playback,
sustained diagnostics, signing and measured performance benchmarks are next.

## Redesign verification recovery (2026-10-08)

The redesign at `8e20950` was preserved. The cockpit's final command failed with
exit 127 because the manually installed SDK `/tmp/euclens-dotnet/dotnet` was
invoked by absolute path during implementation and never registered on the fresh
check shell's PATH. A user-owned launcher now exists at
`<home>/.nvm/versions/node/<version>/bin/dotnet`, an existing PATH directory.
A fresh shell resolves .NET SDK 10.0.401 and `dotnet --info` succeeds. This host
launcher points to a temporary SDK; it is not a replacement for installing the
SDK in a new environment.

`tools/verify.sh` explicitly checks SDK discovery and adds the documented SDK
location to PATH when needed (override with `EUCLENS_DOTNET_DIR`). It restores,
builds, runs every existing test, publishes the portable app and asserts that
output contains exactly `EucLens.exe`. It uses one MSBuild node because this
Linux host emitted premature worker-node exits during one parallel solution
build; no test is skipped or compiler warning suppressed.

The original command was rerun once PATH was repaired and revealed
NETSDK1099/NETSDK1098: solution-wide single-file publish attempted to publish
class libraries and the test host. `IsPublishable` now defaults to false and the
WPF executable explicitly sets it true. All projects still build and tests run.
The exact original `dotnet test && dotnet publish -c Release -r win-x64
--self-contained -p:PublishSingleFile=true` subsequently exited 0 (45 tests passed).
Use the Portable profile for delivery, since it also embeds native libraries.

Final canonical local verification, `./tools/verify.sh`, passed:

- Release solution build: zero warnings, zero errors.
- Unit tests: 45 passed, zero failed/skipped; `TestResults/verification.trx`.
- Design contract and shell syntax checks passed; `git diff --check` passed.
- Self-contained win-x64 portable publish passed and contains exactly one EXE.
- EXE SHA-256: `ded89757c3cf15864b55fc2de456f9f355d8ad1bd9ef77c3f8db22a981eab708`.

The Windows workflow now accepts `cez/**` feature branches, explicitly verifies
`dotnet --info` after `actions/setup-dotnet`, and uploads expanded smoke evidence.
The published EXE's `--smoke-test` exercises navigation via the real ListBox
binding in all five views and both themes, asserts chart presence and active
paint resources, verifies adaptive panes do not overlap or exceed available
width, and produces real WPF client-visual PNG captures at requested 1366×768
and 1920×1080 window sizes. Native title bars are not included in those captures.

The layout matrix uses available DIP space equivalent to all four requested
resolutions at 100/125/150% scaling. It records the host's actual DPI separately;
it does **not** change Windows DPI or replace actual per-monitor acceptance.
Manual DPI changes, keyboard/focus and pixel/text inspection remain in
[the Windows acceptance checklist](WINDOWS_ACCEPTANCE.md). At this point no Windows CI execution, runtime pass or
actual PNG from the redesigned revision is claimed: the feature branch is still
local and the operator's Guard requires approval before its remote push.

### Windows runtime follow-up

With user authorization, `cez/30c36cd6` was pushed. The first redesign run,
37810980268 (private development repository), passed
build, 45 tests and publish, then detected a genuine startup exception:
`NavigationWidth` was a Double used as `ColumnDefinition.Width` (GridLength).
Commit `476cbfc` changes only that token's type and adds a contract gate rejecting
wrong grid-sizing resource types. It preserves the 184-DIP navigation layout.

Windows run 37811451477 (private development repository)
passed at `476cbfc`, including startup, sampling, navigation binding, both themes,
all five views, chart paint assertions, adaptive-pane bounds/overlap, recording,
JSON/CSV export and no WPF binding errors. Actual host DPI was 96×96. The EXE
and all 20 WPF client captures were downloaded; inspection of the overview
capture exposed the limitations below.

Evidence inspection detected that the hosted desktop capped native windows at
1044 DIPs wide. Those first captures therefore do not validate 1366/1920-wide
layouts, even though the corresponding window sizes were requested. Transparent
client areas also appeared black because Window.Background was outside the
captured visual. The validation helper now arranges the same live WPF client
at the exact requested viewport (subtracting native chrome) independently of
that host cap, asserts those actual dimensions, and composites the active
Window.Background into PNGs. This changes only validation/capture behavior.
The resulting captures are real WPF client renders, excluding native chrome;
they are not full desktop screenshots. Final CI must pass on the corrected
helper before those exact-size captures are accepted.

Actual changes of Windows display DPI and mixed-monitor transitions remain
manual acceptance; the CI report explicitly separates host DPI from available
DIP-space simulation at 100/125/150%.

The viewport harness now hosts the same loaded client in a smoke-only Canvas
so native desktop constraints cannot override its requested size. Run
37812865544 (private development repository) passed
at `5e6d1c1`: 45/45 Windows tests, published EXE smoke, all 120 layout cases and
20 real WPF client renders. Dimensions verified from PNG headers are 1350×729
and 1904×1041 pixels (requested 1366×768 and 1920×1080 minus native chrome).

Pixel inspection then found a product theme issue rather than a capture issue:
MainWindow did not explicitly apply the implicit base Window style, leaving its
background at the system white brush while dark-theme text changed to light.
The existing shared Window style is now applied explicitly; no new design or
palette was introduced. Windows smoke asserts the actual window background
matches WindowBrush in each theme and layout rounding is enabled. The final
revision must pass this additional assertion and its captures must be inspected.
