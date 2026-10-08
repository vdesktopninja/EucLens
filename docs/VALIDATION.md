# MVP validation evidence

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
[Windows CI run 37803672588](https://github.com/vdesktopninja/RDA/actions/runs/37803672588)
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
`/home/ziut/.nvm/versions/node/v24.21.0/bin/dotnet`, an existing PATH directory.
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
`WINDOWS_ACCEPTANCE.md`. At this point no Windows CI execution, runtime pass or
actual PNG from the redesigned revision is claimed: the feature branch is still
local and the operator's Guard requires approval before its remote push.
