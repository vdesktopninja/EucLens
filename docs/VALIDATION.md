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
