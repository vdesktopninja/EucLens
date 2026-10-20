using System.Diagnostics;
using System.Runtime.Versioning;
using EucLens.Core;

namespace EucLens.Providers;

/// <summary>Owns and reuses native performance counter handles and rate baselines.</summary>
public sealed class WindowsBlastCounterReader : IBlastCounterReader
{
    private readonly Dictionary<(string Category, string Instance, string Counter), Entry> entries = [];
    private bool disposed;
    private sealed class Entry(PerformanceCounter counter)
    {
        public PerformanceCounter Counter { get; } = counter;
        public CounterSample? Previous { get; set; }
    }

    public IReadOnlyList<string> GetInstances(string category)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return OperatingSystem.IsWindows() ? GetWindowsInstances(category) : [];
    }

    [SupportedOSPlatform("windows")]
    private static string[] GetWindowsInstances(string category) =>
        PerformanceCounterCategory.Exists(category) ? new PerformanceCounterCategory(category).GetInstanceNames() : [];

    public CounterReading Read(string category, string instance, string counter)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!OperatingSystem.IsWindows())
            return new(null, null, DateTimeOffset.UtcNow, MetricStatus.Unavailable, "Windows performance counters require Windows.");
        return ReadWindows(category, instance, counter);
    }

    [SupportedOSPlatform("windows")]
    private CounterReading ReadWindows(string category, string instance, string counter)
    {
        var key = (category, instance, counter);
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                if (!new PerformanceCounterCategory(category).CounterExists(counter))
                    return new(null, null, now, MetricStatus.Unavailable, $"Counter is not installed: {counter}.");
                entry = new(new PerformanceCounter(category, counter, instance, readOnly: true));
                entries.Add(key, entry);
            }
            var sample = entry.Counter.NextSample();
            var previous = entry.Previous;
            entry.Previous = sample;
            if (previous is { } old && sample.TimeStamp <= old.TimeStamp)
                return new(null, sample.RawValue, now, MetricStatus.Stale, "Native counter timestamp has not advanced.");
            // Raw count counters need no warm-up. Other counter types need two native samples.
            var raw = sample.CounterType is PerformanceCounterType.NumberOfItems32 or
                PerformanceCounterType.NumberOfItems64 or PerformanceCounterType.NumberOfItemsHEX32 or
                PerformanceCounterType.NumberOfItemsHEX64;
            if (!raw && previous is null)
                return new(null, sample.RawValue, now, MetricStatus.Unavailable, "Warming up: this counter requires two samples.");
            var value = raw ? (double)sample.RawValue : CounterSample.Calculate(previous!.Value, sample);
            return new(value, sample.RawValue, now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            if (entries.Remove(key, out var entry)) entry.Counter.Dispose();
            return new(null, null, now, MetricStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Reset()
    {
        if (OperatingSystem.IsWindows()) DisposeWindowsEntries();
        entries.Clear();
    }

    [SupportedOSPlatform("windows")]
    private void DisposeWindowsEntries()
    {
        foreach (var entry in entries.Values) entry.Counter.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        Reset();
        disposed = true;
    }
}
