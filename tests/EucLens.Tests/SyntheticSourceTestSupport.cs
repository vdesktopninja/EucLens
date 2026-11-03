using EucLens.App.ViewModels;
using EucLens.Core;

namespace EucLens.Tests;

/// <summary>
/// The application has one data source, the real session. Tests that need values, findings and recordings drive the view model with
/// synthetic data through the automated-validation hook, exactly as the smoke harness does.
/// </summary>
internal static class SyntheticSourceTestSupport
{
    public static MainViewModel WithSynthetic(this MainViewModel vm, IMetricsProvider source)
    {
        vm.UseSyntheticSource(source);
        return vm;
    }
}
