These are the license texts and notices that the components redistributed inside EucLens.exe require, copied **verbatim** (not edited, not reformatted).
The EUC Lens license itself is `LICENSE` in the repository root; `THIRD_PARTY_NOTICES.md` is the readable summary.

| File | What it covers | Where it was taken from |
|---|---|---|
| `dotnet-runtime-LICENSE.TXT`, `dotnet-runtime-THIRD-PARTY-NOTICES.TXT` | The .NET runtime and base class libraries bundled by the self-contained build, and the third-party code in them | `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` of the NuGet package `Microsoft.NETCore.App.Runtime.win-x64` 10.0.13 (byte-identical in 10.0.12 and 10.0.14, the version the published build bundles) |
| `dotnet-windowsdesktop-LICENSE.txt` | The Windows Desktop runtime (WPF) | `LICENSE` of `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.13 |
| `skiasharp-LICENSE.txt` | SkiaSharp, SkiaSharp.HarfBuzz, SkiaSharp.Views.*, HarfBuzzSharp (MIT; the packages carry the same file) | `LICENSE.txt` of the NuGet package `SkiaSharp` 3.119.0 |
| `skiasharp-native-THIRD-PARTY-NOTICES.txt` | Native libraries inside `libSkiaSharp` and `libHarfBuzzSharp` (ANGLE, HarfBuzz, Skia, FreeType, libpng, zlib, expat, ICU, libjpeg-turbo, libwebp, …) | `THIRD-PARTY-NOTICES.txt` of `SkiaSharp.NativeAssets.Win32` 3.119.0 (byte-identical in `HarfBuzzSharp.NativeAssets.Win32` 8.3.1.1) |
| `glfw-COPYING.md` | GLFW (native library in `OpenTK.redist.glfw`): zlib/libpng license | `COPYING.md` of `OpenTK.redist.glfw` 3.3.0-pre20200830200122 (its `license` file entry) |
| `opentk-LICENSE.md` | OpenTK 4.3.0 (MIT). Only the `OpenTK` package states MIT in its metadata (`license` expression); its companion packages state none. | `LICENSE.md` of https://github.com/opentk/opentk (default branch, retrieved 2026-11-05) |
| `glwpfcontrol-LICENSE.md` | OpenTK.GLWpfControl 4.2.3 (MIT). The package's `licenseUrl` points to this file. | `LICENSE.md` of https://github.com/varon/GLWpfControl (default branch, retrieved 2026-11-05) |
| `livecharts-LICENSE.txt` | LiveChartsCore, LiveChartsCore.SkiaSharpView, LiveChartsCore.SkiaSharpView.WPF 2.0.0 (MIT; the packages declare MIT) | `LICENSE` of https://github.com/beto-rodriguez/LiveCharts2 (default branch, retrieved 2026-11-05) |

The Microsoft.Extensions.* and System.* packages 10.0.0 declare MIT and are part of the .NET libraries covered by `dotnet-runtime-LICENSE.TXT`.
When a component or the runtime version changes, re-copy the files from the new package and update this table. `THIRD_PARTY_NOTICES.md` is checked against the restored package list by a test.
