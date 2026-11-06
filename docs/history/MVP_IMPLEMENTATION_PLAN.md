# MVP implementation plan (historical)

> **Historical document.** This is the plan of the first MVP (October 2026): five pages, a synthetic demo mode and in-memory recording, all of which were replaced by later versions. It is kept for the project's engineering history and does not describe the current product. For what EUC Lens does today, see the [README](../../README.md) and the [documentation index](../README.md).

1. Establish .NET 10 solution: platform-neutral Core, Providers and Diagnostics;
   Windows WPF App; cross-platform xUnit Tests. Keep vendor dependencies out of UI.
2. Define explicit available/unavailable metrics, timestamped session snapshots,
   provider lifecycle, workload context, diagnostic and recorder interfaces.
3. Implement deterministic, correlated mock time series and five demo scenarios;
   unavailable vendor adapters with no guessed APIs.
4. Build MVVM shell with five working navigation pages, dynamic themes, metric
   cards, bounded real-time LiveCharts history, async one-second sampling.
5. Add workload-aware diagnostic rules and local timestamped recording with
   bounded memory, JSON/CSV export and recording history chart.
6. Test provider lifecycle/scenarios, missing metrics, contextual rules, recording
   and exports. Cross-build WPF and publish a self-contained Windows x64 EXE.
7. Add Windows CI with startup smoke check and artifact upload, documentation,
   manual Windows acceptance checklist, and review compilation/runtime risks.

## Validation boundaries

Linux can execute business tests and compile Windows targeting packs, but cannot
execute WPF. Windows CI and manual interactive checks must cover startup,
rendering, navigation, theme changes, charts and file dialogs.
