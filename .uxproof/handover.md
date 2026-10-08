# Step 1 handover — design contract and shared WPF foundations

**Subsequent status:** Step 2 has implemented the shell, five views, responsive panes and chart theme adapter. See [implementation.md](implementation.md) for current evidence and step 3 Windows acceptance. The remainder below records step 1 history.

Step 1 is complete. The overall redesign is not complete: step 2 implements the shell/views/chart presentation, and step 3 owns Windows runtime, publish and visual acceptance. The audit was explicitly requested by the operator in addition to the setup workflow.

## Extracted and implemented

- Stack: WPF, .NET 10, MVVM, native controls and LiveCharts2 2.0.0.
- Tokens: 78 declarations: 24 shared foundation values plus 27 color/brush declarations per theme (54 color/brush declarations in total). Repeated semantic keys are intentional light/dark variants.
- Registry: 28 entries: 6 shell/view components, 19 styles and 3 data templates.
- Theme keys match. All token sources are maintained theme dictionaries; 0% scratch/generated contributions.
- Shared resources now implement Segoe UI typography, tabular metric numerals, 4/8 DIP spacing, 1 DIP borders, 4 DIP control/panel radii, 6 DIP popup radii, neutral light/graphite dark surfaces, blue accent and green/amber/red status. Reusable metric strip/rows and chart surface styles are available.
- Existing keys remain compatible; theme replacement still uses merged dictionary index 0. Metrics engine, providers, commands and tests are unchanged.

| Screen archetype | Canonical existing example |
| --- | --- |
| Session overview | `src/EucLens.App/Views/DashboardView.xaml` |
| Diagnostic evidence | `src/EucLens.App/Views/DiagnosticsView.xaml` |
| Recording and export | `src/EucLens.App/Views/RecorderView.xaml` |
| Technical inventory | `src/EucLens.App/Views/SystemView.xaml` |
| Preferences | `src/EucLens.App/Views/SettingsView.xaml` |

These examples have not yet been redesigned. Read `ui-audit.md` for concrete source evidence and the next implementation directions; `conventions.md` contains the operator's manual design rules and deliberate exceptions.

## Verification

- `python3 tools/ux-contract.py --check`: passed. Deterministic JSON extraction, XML parse, named resource presence and theme parity.
- `dotnet build EucLens.sln -c Release --no-restore`: passed, 0 warnings and 0 errors with temporary SDK 10.0.401.
- `dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=step1-tests.trx' --results-directory TestResults`: 44 passed, 0 failed, 0 skipped. TRX is local ignored evidence under `TestResults/step1-tests.trx`.
- `git diff --check`: passed.
- `.github/workflows/windows.yml` now checks the WPF contract before its existing Windows restore/build/test/publish/EXE smoke sequence. No GitHub Actions execution or Windows runtime result is claimed.

The first build/test attempt ran concurrently and hit shared-output file locks. Both commands were subsequently run sequentially, producing the clean results above.

## Evidence limits and tooling

The selected skill's repo-local override and referenced `agentic-setup.md`, `rules.md`, `contract-format.md` and `report-templates.md` were not available. The supplied workflow was followed using the pinned package's actual contract implementation for field shapes and manual-section markers; this handover is therefore not claimed to reproduce the unavailable report template.

`npx --yes uxproof@0.3.1 init --no-skills` exited 1: “this does not look like a React repo (no framework, no components found). Nothing written.” This is an unsupported-stack fallback, not a successful npm extraction. `tools/ux-contract.py` performs complete source extraction for this WPF repository without scanning generated/build files. Inform the operator before refresh; its `--refresh` option requires deliberate use and never rewrites human conventions. React/browser companion audits do not validate WPF.

A cross-target WPF compilation does not validate merged-dictionary runtime loading, Skia painting, focus behavior, clipping or DPI. No EXE, screenshots or Windows visual approval are delivered by this step. Keep those requirements in step 3 and implement the layout/chart changes in step 2 first.

The most useful next command is `python3 tools/ux-contract.py --check`. Commit the contract with its shared resources and extractor so subsequent stages judge against the same design system.
