# Third-party notices

EUC Lens is released under the MIT License (see `LICENSE`). The portable executable is a self-contained build: it carries the .NET
runtime and the open-source libraries below. Each is used under its own license; EUC Lens does not modify them.

This is the complete set of NuGet packages the application restores (a test compares it with the restored package list), plus the .NET runtime.
For every component the license name below is the one its package metadata states or, where the package states none, the one its project publishes
(said so explicitly). The license texts and notices that these components require are shipped **verbatim** with the application: they are in the
`licenses` folder of the repository and are embedded in the executable (About → *Release notes and legal* → *Full third-party license texts*).
`licenses/README.md` says where each file came from.

## .NET runtime and Windows Desktop (WPF)

- .NET 10 runtime, base class libraries and Windows Desktop runtime (WPF): MIT License, Copyright (c) .NET Foundation and Contributors. Texts: `dotnet-runtime-LICENSE.TXT`, `dotnet-windowsdesktop-LICENSE.txt`. https://github.com/dotnet/runtime, https://github.com/dotnet/wpf
- The runtime contains third-party code under further licenses (46 notices, among them zlib-ng, Brotli, Unicode data, xxHash, mimalloc, musl, Json.NET). They are reproduced in `dotnet-runtime-THIRD-PARTY-NOTICES.TXT`.

## Charts and graphics

- LiveChartsCore, LiveChartsCore.SkiaSharpView, LiveChartsCore.SkiaSharpView.WPF 2.0.0: MIT License (declared by the packages), Copyright (c) 2021 Alberto Rodriguez Orozco. Text: `livecharts-LICENSE.txt`. https://github.com/beto-rodriguez/LiveCharts2
- SkiaSharp, SkiaSharp.HarfBuzz, SkiaSharp.NativeAssets.Win32, SkiaSharp.Views.Desktop.Common, SkiaSharp.Views.WPF 3.119.0, HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 8.3.1.1: MIT License (declared by the packages), Copyright (c) 2015-2016 Xamarin, Inc. and (c) 2017-2018 Microsoft Corporation. Text: `skiasharp-LICENSE.txt`. https://github.com/mono/SkiaSharp
- The native libraries of SkiaSharp and HarfBuzzSharp incorporate third-party material under its own licenses. The packages' own notice file lists, with each license text, ANGLE, HarfBuzz, Skia, etc1, gif, libpng, DNG SDK, expat, FreeType, ICU, imgui, jsoncpp, libjpeg-turbo, libwebp, libmicrohttpd, piex, SDL, sfntly, SPIR-V Headers, SPIR-V Tools and zlib. It is reproduced unchanged in `skiasharp-native-THIRD-PARTY-NOTICES.txt`. Not every listed component is necessarily present in the Windows binaries; the file is the supplier's notice for the packages.
- OpenTK, OpenTK.Compute, OpenTK.Core, OpenTK.Graphics, OpenTK.Input, OpenTK.Mathematics, OpenTK.OpenAL, OpenTK.Windowing.Common, OpenTK.Windowing.Desktop, OpenTK.Windowing.GraphicsLibraryFramework 4.3.0: MIT License, Copyright (c) 2006-2019 Stefanos Apostolopoulos for the Open Toolkit project. The `OpenTK` package declares MIT in its metadata; its companion packages state no license, and the MIT license of the OpenTK project (https://github.com/opentk/opentk) is the one that applies to them. Text: `opentk-LICENSE.md`.
- OpenTK.GLWpfControl 4.2.3: MIT License, Copyright (c) 2019 varon (the package metadata names "Copyright (c) 2022 Team OpenTK"). The package states no license; its `licenseUrl` points to the MIT text published at https://github.com/varon/GLWpfControl. Text: `glwpfcontrol-LICENSE.md`.
- OpenTK.redist.glfw 3.3.0-pre20200830200122 (GLFW): the zlib/libpng license, Copyright (c) 2002-2006 Marcus Geelnard and (c) 2006-2016 Camilla Löwy (the package names its file `COPYING.md` as its license). Text: `glfw-COPYING.md`. https://www.glfw.org

## Microsoft.Extensions and System packages

- Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Options, Microsoft.Extensions.Primitives 10.0.0: MIT License (declared by the packages), Copyright (c) Microsoft Corporation. Part of the .NET libraries; text: `dotnet-runtime-LICENSE.TXT`.
- System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Diagnostics.PerformanceCounter, System.Security.Cryptography.ProtectedData 10.0.0: MIT License (declared by the packages), Copyright (c) Microsoft Corporation. Part of the .NET libraries; text: `dotnet-runtime-LICENSE.TXT`.

## Fonts

EUC Lens ships no font. It uses Segoe UI Variable and Segoe UI, which are part of Windows, and Segoe MDL2 Assets for navigation glyphs.

## Not included

The test projects (xUnit and its dependencies) are not part of the executable and are not listed.
