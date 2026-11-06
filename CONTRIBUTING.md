# Contributing to EUC Lens

Thank you for helping. EUC Lens is a diagnostics tool, so its first rule is accuracy: it reports what was measured, and it says so plainly when something could not be measured.

## Workflow

Use a feature branch and open a pull request; do not push changes directly to `main`. Keep changes focused and use logical commits. Install the .NET 10 SDK and follow [Building from source](README.md#building-from-source). Windows is required to run the UI; the platform-neutral business tests run on Linux and macOS. Start with the [architecture overview](docs/development/ARCHITECTURE.md) and the [documentation index](docs/README.md).

## Principles

- **Never invent measurements.** Do not guess vendor APIs, counter names or unit conversions. A new source needs documentation of where the value comes from, its unit, the supported versions and the permissions it needs, and an explicit way to report *unavailable*. A missing value is never zero.
- **Keep Core independent of WPF and vendor SDKs.** Inject provider, diagnostic, recorder and desktop dependencies, and keep business decisions in testable code.
- **Do not block the UI thread.** Use asynchronous collection and cancellation, cap memory growth and dispose resources. Document data semantics and units.
- **Keep synthetic data out of the application.** EUC Lens monitors the real session only. Synthetic data exists in automated tests and in the `--smoke-test` harness, and real monitoring rejects samples marked synthetic.
- **Stay local and private.** No telemetry, authentication, server component or mandatory internet dependency for normal operation. The update check is manual and opt-in; keep it that way.
- **Be conservative in findings.** State measured facts, thresholds and limitations; do not claim a root cause the data cannot show.
- **Tests.** Add meaningful tests for new rules, missing-data behavior, providers, recordings and exports. Build with warnings as errors and run the [Windows acceptance checklist](docs/validation/WINDOWS_ACCEPTANCE.md) for UI changes. Do not claim Windows execution based on a Linux cross-build.
- **License.** Contributions are under the [MIT License](LICENSE). Do not copy proprietary or vendor implementation code, including from Remote Display Analyzer.

## Before you open a pull request

```sh
dotnet build EucLens.sln -c Release
dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release
dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64
```

On Windows, run `EucLens.exe --smoke-test` in the output directory. It drives the UI automatically, creates and removes temporary exports, does not save preferences, exits with code 0 and writes `smoke-result.txt` on success. It is a validation harness that uses synthetic data; use a normal launch for interactive testing. The GitHub Actions workflow runs the same checks on every pull request.

Include build and test evidence and any material limitations in the pull request description.
