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

## Windows verification still required

The Windows GitHub workflow is committed but was not executed from this session;
no branch was pushed. Its published-EXE smoke mode loads all five views, samples,
changes themes, records, exports and checks WPF binding errors. This Linux host
cannot execute WPF. Manual VDI/DPI/accessibility/file-dialog testing remains
pending under [WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md).

The MVP is ready for source review and Windows validation, not a claim of a
Windows-tested release. Vendor integration research, import/playback,
sustained diagnostics, signing and measured performance benchmarks are next.
