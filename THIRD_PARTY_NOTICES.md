# Third-party notices

EUC Lens is released under the MIT License (see `LICENSE`). The portable executable is a self-contained build: it carries the .NET
runtime and the open-source libraries below. Each is used under its own license; EUC Lens does not modify them.

The list is the complete set of NuGet packages the application restores (a test compares it with the restored package list), with the license
each package declares in its own metadata.

## .NET runtime and Windows Desktop (WPF)

- .NET 10 runtime, base class libraries and Windows Desktop runtime (WPF): MIT. Copyright (c) .NET Foundation and Contributors. https://github.com/dotnet/runtime, https://github.com/dotnet/wpf

## Charts and graphics

- LiveChartsCore, LiveChartsCore.SkiaSharpView, LiveChartsCore.SkiaSharpView.WPF 2.0.0: MIT. Copyright (c) Alberto Rodriguez Orozco and contributors. https://github.com/beto-rodriguez/LiveCharts2
- SkiaSharp, SkiaSharp.HarfBuzz, SkiaSharp.NativeAssets.Win32, SkiaSharp.Views.Desktop.Common, SkiaSharp.Views.WPF 3.119.0: MIT. Copyright (c) Microsoft Corporation and contributors. https://github.com/mono/SkiaSharp
- HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 8.3.1.1: MIT. Copyright (c) Microsoft Corporation and contributors. https://github.com/mono/SkiaSharp
- The native SkiaSharp library embeds Skia (BSD 3-Clause, Copyright (c) Google Inc.) and further native components such as HarfBuzz, FreeType, libpng, zlib and expat under their own licenses; see https://github.com/mono/SkiaSharp and https://skia.org.
- OpenTK, OpenTK.Compute, OpenTK.Core, OpenTK.Graphics, OpenTK.Input, OpenTK.Mathematics, OpenTK.OpenAL, OpenTK.Windowing.Common, OpenTK.Windowing.Desktop, OpenTK.Windowing.GraphicsLibraryFramework 4.3.0: the Open Toolkit library license (an MIT-style license; the OpenTK package declares MIT), published at https://github.com/opentk/opentk. Copyright (c) 2006-2022 Team OpenTK. Only the OpenTK package itself states the license in its metadata; its companion packages carry no license text of their own.
- OpenTK.GLWpfControl 4.2.3: the license published at https://github.com/varon/GLWpfControl/blob/master/LICENSE.md (MIT). Copyright (c) 2022 Team OpenTK. The package carries no license text of its own.
- OpenTK.redist.glfw 3.3.0-pre20200830200122: GLFW license (zlib/libpng). Copyright (c) 2002-2006 Marcus Geelnard, (c) 2006-2016 Camilla Löwy. https://www.glfw.org

## Microsoft.Extensions and System packages

- Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Options, Microsoft.Extensions.Primitives 10.0.0: MIT. Copyright (c) Microsoft Corporation. https://github.com/dotnet/runtime
- System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Diagnostics.PerformanceCounter, System.Security.Cryptography.ProtectedData 10.0.0: MIT. Copyright (c) Microsoft Corporation. https://github.com/dotnet/runtime

## Fonts

EUC Lens ships no font. It uses Segoe UI Variable and Segoe UI, which are part of Windows, and Segoe MDL2 Assets for navigation glyphs.

## Not included

The test projects (xUnit and its dependencies) are not part of the executable and are not listed.
