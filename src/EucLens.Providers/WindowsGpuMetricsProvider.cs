using System.Globalization;
using EucLens.Core;

namespace EucLens.Providers;

public sealed record GpuSample(GpuMetrics Dashboard, IReadOnlyList<MetricEntry> Entries);

/// <summary>
/// Adapter inventory from DXGI, utilization/memory from Windows GPU counters, NVML as an optional second source.
/// Windows counters are primary; NVML only fills a dashboard field Windows cannot supply, and both keep their source.
/// Not thread-safe; callers serialize.
/// </summary>
public sealed class WindowsGpuMetricsProvider(IGpuAdapterSource adapterSource, IGpuCounterSource counters,
    Func<INvmlClient>? nvmlFactory = null, TimeProvider? clock = null) : IDisposable
{
    private const double GiB = 1024d * 1024 * 1024;
    private const string DxgiSource = "DXGI IDXGIAdapter1::GetDesc1";
    private const string EngineSource = @"\GPU Engine(*)\Utilization Percentage";
    private const string MemorySource = @"\GPU Adapter Memory(*)";
    private static readonly TimeSpan InventoryRefresh = TimeSpan.FromSeconds(60);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private IReadOnlyList<GpuAdapterInfo> adapters = [];
    private string? inventoryError;
    private DateTimeOffset nextInventory;
    private INvmlClient? nvml;
    private bool nvmlLoaded;
    private IReadOnlyList<MetricEntry> staticEntries = [];

    public IReadOnlyList<GpuAdapterInfo> Adapters => adapters;

    public GpuSample Collect(int sessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        RefreshInventory(now);
        if (!nvmlLoaded) { nvmlLoaded = true; nvml = LoadNvml(); }

        var engines = Guard(counters.ReadEngines, now);
        var memory = Guard(counters.ReadAdapterMemory, now);
        var aggregates = engines.Status == MetricStatus.Available
            ? GpuEngineMath.Aggregate(engines.Values, pid => counters.SessionOf(pid) == sessionId)
            : new Dictionary<string, GpuEngineAggregate>();
        var memoryByLuid = memory.Values
            .Select(m => (Luid: GpuEngineMath.MemoryInstanceLuid(m.Instance), m.DedicatedBytes, m.SharedBytes))
            .Where(m => m.Luid is not null)
            .GroupBy(m => m.Luid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Dedicated: g.Sum(x => x.DedicatedBytes), Shared: g.Sum(x => x.SharedBytes)), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<NvmlDeviceSample> nvmlDevices = [];
        string? nvmlError = null;
        try { nvmlDevices = nvml?.Sample() ?? []; }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex) || ex is AccessViolationException) { nvmlError = ex.Message; }

        var entries = new List<MetricEntry>(staticEntries.Count + adapters.Count * 8 + nvmlDevices.Count * 6 + 2);
        entries.AddRange(staticEntries);
        var views = new Dictionary<GpuAdapterInfo, AdapterView>();
        foreach (var adapter in adapters)
        {
            var view = Measure(adapter, aggregates, engines, memoryByLuid, memory, Link(adapter, nvmlDevices), now);
            views[adapter] = view;
            entries.AddRange(view.Entries);
        }
        entries.Add(Text("nvml.status", "NVML backend", nvmlError ?? nvml?.Status ?? "Not loaded.", "nvml.dll", now,
            nvml?.IsAvailable == true && nvmlError is null ? MetricStatus.Available : MetricStatus.Unavailable));
        foreach (var device in nvmlDevices) entries.AddRange(NvmlEntries(device, now));

        var (primary, reason) = GpuClassifier.SelectPrimary(adapters);
        entries.Add(Text("gpu.selected", "Dashboard adapter", primary is null ? $"None — {reason}" : $"Adapter {primary.Index}: {primary.Description}. {reason}", DxgiSource, now));
        return new(Dashboard(primary, primary is null ? null : views[primary], reason, now), entries);
    }

    private sealed record AdapterView(IReadOnlyList<MetricEntry> Entries, Metric Utilization, Metric Vram, Metric Encode,
        Metric Decode, string VramText, NvmlDeviceSample? Nvml);

    private AdapterView Measure(GpuAdapterInfo a, IReadOnlyDictionary<string, GpuEngineAggregate> aggregates,
        GpuCounterRead<GpuEngineReading> engines, IReadOnlyDictionary<string, (double Dedicated, double Shared)> memoryByLuid,
        GpuCounterRead<GpuMemoryReading> memory, NvmlDeviceSample? linked, DateTimeOffset now)
    {
        var prefix = $"gpu.{a.Index}.";
        var label = $"GPU {a.Index}";
        aggregates.TryGetValue(a.Luid, out var agg);
        string engineReason = engines.Status != MetricStatus.Available ? engines.Reason ?? "GPU Engine counters unavailable."
            : $"No GPU Engine instances for adapter LUID {a.Luid}; the driver does not publish engine counters for it.";
        var engineStatus = engines.Status == MetricStatus.Available ? MetricStatus.Unavailable : engines.Status;
        Metric Engine(double? value, string reason, MetricScope scope = MetricScope.Machine) => value is { } v
            ? new(v, "%", Source: EngineSource, Timestamp: engines.Timestamp, SampleStatus: MetricStatus.Available, Scope: scope)
            : new(null, "%", reason, EngineSource, engines.Timestamp, SampleStatus: agg is null ? engineStatus : MetricStatus.Unavailable,
                Error: engineStatus == MetricStatus.Failed ? reason : null, Scope: scope);
        var types = agg is null ? "" : string.Join(", ", agg.EngineTypes);
        var utilization = Engine(agg?.Utilization, engineReason);
        var sessionUtilization = Engine(agg?.SessionUtilization, engineReason, MetricScope.Session);
        var encode = Engine(agg?.Encode, agg is null ? engineReason : $"Adapter exposes no video encode engine (engine types: {types}).");
        var decode = Engine(agg?.Decode, agg is null ? engineReason : $"Adapter exposes no video decode engine (engine types: {types}).");

        Metric Memory(Func<(double Dedicated, double Shared), double> pick, string counter) =>
            memoryByLuid.TryGetValue(a.Luid, out var m) && double.IsFinite(pick(m))
                ? new(pick(m) / GiB, "GiB", Source: $@"{MemorySource}\{counter}", Timestamp: memory.Timestamp, RawValue: (long)pick(m),
                    SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
                : new(null, "GiB", memory.Reason ?? $"No GPU Adapter Memory instance for LUID {a.Luid}.", $@"{MemorySource}\{counter}",
                    memory.Timestamp, SampleStatus: memory.Status == MetricStatus.Failed ? MetricStatus.Failed : MetricStatus.Unavailable, Scope: MetricScope.Machine);
        var dedicatedUsed = Memory(m => m.Dedicated, "Dedicated Usage");
        var sharedUsed = Memory(m => m.Shared, "Shared Usage");
        var vram = dedicatedUsed.Value is { } used && a.DedicatedVideoMemory > 0
            ? new Metric(Math.Clamp(100 * used * GiB / a.DedicatedVideoMemory, 0, 100), "%", Source: $"{MemorySource}\\Dedicated Usage ÷ DXGI DedicatedVideoMemory",
                Timestamp: memory.Timestamp, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
            : new Metric(null, "%", a.DedicatedVideoMemory == 0 ? "Adapter reports no dedicated video memory (shared-memory or virtual adapter)." : dedicatedUsed.UnavailableReason,
                dedicatedUsed.Source, memory.Timestamp, SampleStatus: dedicatedUsed.Value is null ? dedicatedUsed.Status : MetricStatus.Unavailable, Scope: MetricScope.Machine);
        var vramText = dedicatedUsed.Value is { } u && a.DedicatedVideoMemory > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{u:0.00} / {a.DedicatedVideoMemory / GiB:0.00} GiB (dedicated)")
            : a.DedicatedVideoMemory > 0 ? string.Create(CultureInfo.CurrentCulture, $"N/A / {a.DedicatedVideoMemory / GiB:0.00} GiB (dedicated)") : "N/A";

        MetricEntry E(string id, string name, Metric metric) => new(prefix + id, MetricCategory.Gpu, $"{label} · {name}", metric);
        var list = new List<MetricEntry>
        {
            E("utilization", "utilization (busiest engine, all sessions)", utilization),
            E("sessionUtilization", "utilization by this session's processes", sessionUtilization),
            E("encoder", "video encode engine", encode),
            E("decoder", "video decode engine", decode),
            E("dedicatedUsedGiB", "dedicated memory in use", dedicatedUsed),
            E("sharedUsedGiB", "shared system memory in use", sharedUsed),
            E("vramPercent", "dedicated memory utilization", vram),
            Text(prefix + "capabilities", $"{label} · monitoring capabilities", Capabilities(agg, memoryByLuid.ContainsKey(a.Luid), linked), "EUC Lens capability probe", now),
        };
        return new(list, utilization, vram, encode, decode, vramText, linked);
    }

    private static string Capabilities(GpuEngineAggregate? agg, bool memory, NvmlDeviceSample? nvml)
    {
        var parts = new List<string> { agg is null ? "no engine counters" : $"engine counters ({string.Join(", ", agg.EngineTypes)})" };
        parts.Add(memory ? "adapter memory counters" : "no adapter memory counters");
        if (agg is not null) parts.Add(agg.Encode is null ? "no encode engine" : "encode engine");
        if (agg is not null) parts.Add(agg.Decode is null ? "no decode engine" : "decode engine");
        parts.Add(nvml is null ? "no NVML device linked" : $"NVML device {nvml.Index} ({nvml.VirtualizationMode})");
        return string.Join("; ", parts);
    }

    private GpuMetrics Dashboard(GpuAdapterInfo? primary, AdapterView? view, string reason, DateTimeOffset now)
    {
        if (primary is null || view is null)
        {
            var missing = new Metric(null, "%", reason, DxgiSource, now, SampleStatus: MetricStatus.Unavailable, Scope: MetricScope.Machine);
            // Software rendering is a supported configuration, not a failure; only a failed inventory is "unknown".
            return new("None (software rendering)", missing, missing, missing, missing, "N/A", "N/A", reason,
                inventoryError is null ? false : null);
        }
        var n = view.Nvml;
        Metric Prefer(Metric windows, NvmlValue? fallback, string nvmlField) =>
            windows.Status == MetricStatus.Available || fallback?.Value is not { } value ? windows
                : new(value, "%", Source: $"NVML {nvmlField} (device {n!.Index}); Windows counter unavailable: {windows.UnavailableReason}",
                    Timestamp: now, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
        var vram = view.Vram.Status == MetricStatus.Available || n?.MemoryUsedBytes.Value is not { } used || n.MemoryTotalBytes.Value is not > 0
            ? view.Vram
            : new(100 * used / n.MemoryTotalBytes.Value!.Value, "%", Source: $"NVML nvmlDeviceGetMemoryInfo used ÷ total (device {n.Index})",
                Timestamp: now, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
        var driver = primary.DriverVersion ?? "N/A";
        if (n is not null && nvml?.DriverVersion is { } nvidia) driver += $" (NVIDIA {nvidia})";
        // NVML's virtualization mode is authoritative; the name-based vGPU heuristic is only used without it.
        var kind = n is null ? primary.KindReason
            : n.VirtualizationMode.StartsWith("vGPU", StringComparison.Ordinal) ? $"NVIDIA virtual GPU (confirmed by NVML: {n.VirtualizationMode})"
            : $"NVIDIA GPU (NVML virtualization mode: {n.VirtualizationMode})";
        return new(primary.Description, Prefer(view.Utilization, n?.GpuUtilization, "nvmlDeviceGetUtilizationRates.gpu"), vram,
            Prefer(view.Encode, n?.EncoderUtilization, "nvmlDeviceGetEncoderUtilization"),
            Prefer(view.Decode, n?.DecoderUtilization, "nvmlDeviceGetDecoderUtilization"), driver, view.VramText, kind, true);
    }

    /// <summary>Links an NVML device only when exactly one adapter and one device share PCI vendor, device and subsystem IDs.</summary>
    private NvmlDeviceSample? Link(GpuAdapterInfo adapter, IReadOnlyList<NvmlDeviceSample> devices)
    {
        if (adapter.VendorId != GpuClassifier.Nvidia || devices.Count == 0) return null;
        bool Same(GpuAdapterInfo a, NvmlDeviceSample d) => d.PciVendorId == a.VendorId && d.PciDeviceId == a.DeviceId && d.PciSubSystemId == a.SubSysId;
        var candidates = devices.Where(d => Same(adapter, d)).ToArray();
        return candidates.Length == 1 && adapters.Count(a => Same(a, candidates[0])) == 1 ? candidates[0] : null;
    }

    private IEnumerable<MetricEntry> NvmlEntries(NvmlDeviceSample d, DateTimeOffset now)
    {
        var prefix = $"nvml.{d.Index}.";
        var label = $"NVML {d.Index}";
        Metric M(NvmlValue v, string unit, string function, double scale = 1) => v.Value is { } value
            ? new(value * scale, unit, Source: $"NVML {function}", Timestamp: now, RawValue: (long)value, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
            : new(null, unit, v.Error ?? "Not queried.", $"NVML {function}", now, SampleStatus: v.Code == 3 || v.Code == 13 ? MetricStatus.Unavailable : MetricStatus.Failed,
                Error: v.Code is 3 or 13 ? null : v.Error, Scope: MetricScope.Machine);
        yield return Text(prefix + "device", $"{label} · device", $"{d.Name} · PCI {d.BusId} · virtualization: {d.VirtualizationMode}", "NVML nvmlDeviceGetName / GetPciInfo_v3 / GetVirtualizationMode", now);
        yield return new(prefix + "gpuUtilization", MetricCategory.Gpu, $"{label} · GPU utilization (kernel active time)", M(d.GpuUtilization, "%", "nvmlDeviceGetUtilizationRates.gpu"));
        yield return new(prefix + "memoryControllerUtilization", MetricCategory.Gpu, $"{label} · memory controller busy (not VRAM usage)", M(d.MemoryControllerUtilization, "%", "nvmlDeviceGetUtilizationRates.memory"));
        yield return new(prefix + "memoryUsedGiB", MetricCategory.Gpu, $"{label} · framebuffer memory used", M(d.MemoryUsedBytes, "GiB", "nvmlDeviceGetMemoryInfo.used", 1 / GiB));
        yield return new(prefix + "memoryTotalGiB", MetricCategory.Gpu, $"{label} · framebuffer memory total", M(d.MemoryTotalBytes, "GiB", "nvmlDeviceGetMemoryInfo.total", 1 / GiB));
        yield return new(prefix + "encoder", MetricCategory.Gpu, $"{label} · NVENC utilization", M(d.EncoderUtilization, "%", "nvmlDeviceGetEncoderUtilization"));
        yield return new(prefix + "decoder", MetricCategory.Gpu, $"{label} · NVDEC utilization", M(d.DecoderUtilization, "%", "nvmlDeviceGetDecoderUtilization"));
    }

    private void RefreshInventory(DateTimeOffset now)
    {
        if (now < nextInventory) return;
        nextInventory = now + InventoryRefresh;
        try { adapters = adapterSource.Enumerate(); inventoryError = null; }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { adapters = []; inventoryError = ex.Message; }
        var list = new List<MetricEntry>
        {
            Text("gpu.adapterCount", "Display adapters enumerated", inventoryError is null ? adapters.Count.ToString(CultureInfo.InvariantCulture) : $"N/A — {inventoryError}",
                DxgiSource, now, inventoryError is null ? MetricStatus.Available : MetricStatus.Failed)
        };
        foreach (var a in adapters)
        {
            var p = $"gpu.{a.Index}.";
            list.Add(Text(p + "description", $"GPU {a.Index} · adapter", $"{a.Description} · {a.Vendor} · {a.Kind}", DxgiSource, now));
            list.Add(Text(p + "kind", $"GPU {a.Index} · classification", a.KindReason, "EUC Lens classifier (DXGI flags, PCI vendor, name)", now));
            list.Add(Text(p + "pci", $"GPU {a.Index} · PCI IDs / LUID", string.Create(CultureInfo.InvariantCulture,
                $"VEN_{a.VendorId:X4} DEV_{a.DeviceId:X4} SUBSYS_{a.SubSysId:X8} · LUID {a.Luid}"), DxgiSource, now));
            list.Add(Text(p + "driver", $"GPU {a.Index} · driver version", a.DriverVersion is null ? "N/A (display-class registry entry not found)" : $"{a.DriverVersion}{(a.DriverDate is null ? "" : " · " + a.DriverDate)}",
                "Registry Control\\Class\\{4d36e968-…} (MatchingDeviceId)", now, a.DriverVersion is null ? MetricStatus.Unavailable : MetricStatus.Available));
            list.Add(new(p + "dedicatedTotalGiB", MetricCategory.Gpu, $"GPU {a.Index} · dedicated video memory",
                new(a.DedicatedVideoMemory / GiB, "GiB", Source: DxgiSource + " DedicatedVideoMemory", Timestamp: now, RawValue: (long)a.DedicatedVideoMemory, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)));
            list.Add(new(p + "sharedTotalGiB", MetricCategory.Gpu, $"GPU {a.Index} · shared system memory budget",
                new(a.SharedSystemMemory / GiB, "GiB", Source: DxgiSource + " SharedSystemMemory", Timestamp: now, RawValue: (long)a.SharedSystemMemory, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)));
        }
        staticEntries = list;
    }

    private INvmlClient? LoadNvml()
    {
        if (nvmlFactory is null) return null;
        try { return nvmlFactory(); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex) || ex is BadImageFormatException) { return new FailedNvml(ex.Message); }
    }

    private static GpuCounterRead<T> Guard<T>(Func<GpuCounterRead<T>> read, DateTimeOffset now)
    {
        try { return read(); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { return new([], MetricStatus.Failed, ex.Message, now); }
    }

    internal static MetricEntry Text(string id, string name, string text, string source, DateTimeOffset now,
        MetricStatus status = MetricStatus.Available, MetricCategory category = MetricCategory.Gpu, MetricScope scope = MetricScope.Machine) =>
        new(id, category, name, new(null, "", status == MetricStatus.Available ? null : text, source, now, SampleStatus: status, Scope: scope), text);

    public void Dispose() { counters.Dispose(); nvml?.Dispose(); }

    private sealed class FailedNvml(string reason) : INvmlClient
    {
        public bool IsAvailable => false;
        public string Status => $"NVML failed to load: {reason}";
        public string? DriverVersion => null;
        public IReadOnlyList<NvmlDeviceSample> Sample() => [];
        public void Dispose() { }
    }
}
