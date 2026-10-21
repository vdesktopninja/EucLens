using System.Runtime.CompilerServices;
using LiveChartsCore.SkiaSharpView;

namespace EucLens.App.ViewModels;

/// <summary>
/// LiveCharts 2.0 configures itself on first use (the <see cref="Axis"/> type initializer registers default mappers in a
/// plain dictionary). Two threads creating chart objects at the same time can corrupt that dictionary, after which every
/// later <see cref="Axis"/> fails with a TypeInitializationException for the life of the process. View models call this
/// before creating chart objects so the first initialization runs exactly once.
/// </summary>
public static class ChartsRuntime
{
    private static readonly object Gate = new();
    private static volatile bool ready;

    public static void EnsureInitialized()
    {
        if (ready) return;
        lock (Gate)
        {
            if (ready) return;
            RuntimeHelpers.RunClassConstructor(typeof(Axis).TypeHandle);
            ready = true;
        }
    }
}
