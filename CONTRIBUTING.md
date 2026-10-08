# Contributing

Use a feature branch and open a pull request; do not push changes directly to
main. Keep changes focused and use logical commits. Install .NET 10 and follow
the commands in README. Windows is required to run the UI; platform-neutral
business tests run on Linux/macOS.

- Keep Core independent of WPF and vendor SDKs. Inject provider, diagnostic,
  recorder and desktop dependencies. Keep business decisions in testable code.
- Never invent measurements, vendor APIs or unsupported counter definitions.
  Include sources and a supported-version/permission matrix for real adapters.
- Use asynchronous collection and cancellation; do not block the dispatcher.
  Cap memory growth and dispose resources. Document data semantics and units.
- Add meaningful tests for new rules, missing-data behavior, providers and exports.
  Build with warnings as errors and run the Windows acceptance checklist for UI
  changes. Do not claim Windows execution based on a Linux cross-build.
- Keep demo labeling visible on every page and in exports. Introduce no telemetry,
  authentication, server or mandatory internet dependency for normal operation.
- Follow the MIT license. Do not copy proprietary RDA or vendor implementation code.

Before review run:

```sh
dotnet build EucLens.sln -c Release
dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release
dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64
```

On Windows, run `EucLens.exe --smoke-test` in the output directory; it exits with
code 0 and writes `smoke-result.txt` on success. This mode intentionally exercises
the UI automatically and exits; use a normal launch for interactive testing.
The smoke mode creates temporary exports and removes them. It does not save
preferences. Include build/test evidence and material limitations in the PR.
