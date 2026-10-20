using System.Globalization;
using System.Text.RegularExpressions;

namespace EucLens.Providers;

public enum GpuAdapterKind
{
    NvidiaGpu, NvidiaVirtualGpu, AmdGpu, IntelIntegratedGpu, OtherHardwareGpu,
    MicrosoftBasicDisplay, MicrosoftBasicRender, VmwareSvga, RemoteOrIndirect, Unknown
}

/// <summary>Static adapter facts from DXGI_ADAPTER_DESC1 plus the display-class registry driver entry.</summary>
public sealed record GpuAdapterInfo(int Index, string Description, uint VendorId, uint DeviceId, uint SubSysId,
    ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, string Luid,
    bool IsSoftware, bool IsRemote, string? DriverVersion, string? DriverDate)
{
    public GpuAdapterKind Kind => GpuClassifier.Classify(this).Kind;
    public string KindReason => GpuClassifier.Classify(this).Reason;
    public string Vendor => GpuClassifier.VendorName(VendorId);
    public bool IsHardwareGpu => Kind is GpuAdapterKind.NvidiaGpu or GpuAdapterKind.NvidiaVirtualGpu or GpuAdapterKind.AmdGpu
        or GpuAdapterKind.IntelIntegratedGpu or GpuAdapterKind.OtherHardwareGpu;
    /// <summary>Matches the "luid_0xHHHHHHHH_0xLLLLLLLL" part of GPU performance counter instance names.</summary>
    public static string FormatLuid(int high, uint low) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{unchecked((uint)high):X8}_0x{low:X8}");
}

public static partial class GpuClassifier
{
    public const uint Nvidia = 0x10DE, Amd = 0x1002, AmdAlt = 0x1022, Intel = 0x8086, Microsoft = 0x1414, Vmware = 0x15AD;

    public static string VendorName(uint vendor) => vendor switch
    {
        Nvidia => "NVIDIA", Amd or AmdAlt => "AMD", Intel => "Intel", Microsoft => "Microsoft", Vmware => "VMware",
        _ => string.Create(CultureInfo.InvariantCulture, $"PCI vendor 0x{vendor:X4}")
    };

    // NVIDIA vGPU profile names end in a framebuffer/series suffix such as "A16-2Q" or "GRID T4-4B".
    [GeneratedRegex(@"(\bGRID\b|-\d+[ABCQ]\b)", RegexOptions.CultureInvariant)]
    private static partial Regex VirtualGpuName();

    /// <summary>Order matters: flags and well-known virtual adapters are decided before vendor IDs.</summary>
    public static (GpuAdapterKind Kind, string Reason) Classify(GpuAdapterInfo a)
    {
        if (a.IsSoftware) return a.VendorId == Microsoft
            ? (GpuAdapterKind.MicrosoftBasicRender, "DXGI software adapter (Microsoft Basic Render Driver); CPU rendering.")
            : (GpuAdapterKind.Unknown, "DXGI software adapter.");
        if (a.IsRemote) return (GpuAdapterKind.RemoteOrIndirect, "DXGI remote adapter flag.");
        if (a.Description.Contains("Indirect", StringComparison.OrdinalIgnoreCase) ||
            a.Description.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
            return (GpuAdapterKind.RemoteOrIndirect, "Indirect/remote display adapter (by adapter name).");
        if (a.VendorId == Vmware) return (GpuAdapterKind.VmwareSvga, "VMware SVGA virtual display adapter; no physical GPU.");
        if (a.VendorId == Microsoft) return (GpuAdapterKind.MicrosoftBasicDisplay, "Microsoft Basic Display Adapter or other Microsoft virtual adapter.");
        return a.VendorId switch
        {
            Nvidia when VirtualGpuName().IsMatch(a.Description) =>
                (GpuAdapterKind.NvidiaVirtualGpu, "NVIDIA adapter with a vGPU profile name; confirmed only when NVML reports vGPU mode."),
            Nvidia => (GpuAdapterKind.NvidiaGpu, "NVIDIA hardware GPU (physical or passthrough)."),
            Amd or AmdAlt => (GpuAdapterKind.AmdGpu, "AMD GPU."),
            Intel when a.DedicatedVideoMemory < 1UL << 30 => (GpuAdapterKind.IntelIntegratedGpu, "Intel GPU with under 1 GiB dedicated memory (integrated)."),
            Intel => (GpuAdapterKind.OtherHardwareGpu, "Intel GPU with dedicated memory (discrete)."),
            _ => (GpuAdapterKind.Unknown, "Unrecognised vendor; treated as a non-GPU adapter.")
        };
    }

    /// <summary>
    /// The dashboard needs one adapter. Hardware GPUs win over virtual/basic/remote adapters, discrete NVIDIA/AMD over
    /// integrated, then the most dedicated memory. Enumeration order is only the final tie-breaker.
    /// </summary>
    public static (GpuAdapterInfo? Adapter, string Reason) SelectPrimary(IReadOnlyList<GpuAdapterInfo> adapters)
    {
        var selected = adapters.Where(a => a.IsHardwareGpu)
            .OrderBy(a => a.Kind == GpuAdapterKind.IntelIntegratedGpu ? 1 : 0)
            .ThenByDescending(a => a.DedicatedVideoMemory).ThenBy(a => a.Index).FirstOrDefault();
        if (selected is not null)
            return (selected, adapters.Count(a => a.IsHardwareGpu) > 1
                ? "Selected among several hardware GPUs: discrete before integrated, then most dedicated memory. All adapters are listed in Advanced Metrics."
                : "Only hardware GPU detected.");
        return (null, adapters.Count == 0
            ? "No display adapters were enumerated."
            : "No hardware GPU: " + string.Join("; ", adapters.Select(a => $"{a.Description} ({a.KindReason})")));
    }
}

public readonly record struct GpuEngineReading(string Instance, double UtilizationPercent);

/// <summary>Adapter-level aggregation of GPU Engine counter instances.</summary>
public sealed record GpuEngineAggregate(double? Utilization, double? SessionUtilization, double? Encode, double? Decode,
    IReadOnlyList<string> EngineTypes, string? BusiestEngine);

public static partial class GpuEngineMath
{
    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<luid>0x[0-9A-Fa-f]{8}_0x[0-9A-Fa-f]{8})_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineInstance();

    public static bool TryParse(string instance, out int pid, out string luid, out string engine, out string type)
    {
        var m = EngineInstance().Match(instance);
        pid = m.Success ? int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture) : 0;
        luid = m.Success ? m.Groups["luid"].Value.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal) : "";
        engine = m.Success ? $"phys_{m.Groups["phys"].Value}_eng_{m.Groups["eng"].Value}" : "";
        type = m.Success ? m.Groups["type"].Value : "";
        return m.Success;
    }

    /// <summary>
    /// Per engine, process usage is summed (engines are time-shared); the adapter value is the busiest engine, as in
    /// Task Manager. Different engines are never added together. Sums are clamped to 100 % because per-process timer
    /// samples are not taken atomically.
    /// </summary>
    public static IReadOnlyDictionary<string, GpuEngineAggregate> Aggregate(IEnumerable<GpuEngineReading> readings, Func<int, bool> inCurrentSession)
    {
        var engines = new Dictionary<(string Luid, string Engine), (string Type, double Total, double Session)>();
        foreach (var reading in readings)
        {
            if (!double.IsFinite(reading.UtilizationPercent) || !TryParse(reading.Instance, out var pid, out var luid, out var engine, out var type)) continue;
            var key = (luid, engine);
            engines.TryGetValue(key, out var current);
            engines[key] = (type, current.Total + reading.UtilizationPercent,
                current.Session + (inCurrentSession(pid) ? reading.UtilizationPercent : 0));
        }
        return engines.GroupBy(e => e.Key.Luid).ToDictionary(g => g.Key, g =>
        {
            static double Clamp(double v) => Math.Clamp(v, 0, 100);
            var busiest = g.MaxBy(e => e.Value.Total);
            double? Max(string fragment) => g.Where(e => e.Value.Type.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .Select(e => (double?)Clamp(e.Value.Total)).DefaultIfEmpty(null).Max();
            return new GpuEngineAggregate(Clamp(busiest.Value.Total), Clamp(g.Max(e => e.Value.Session)), Max("Encode"), Max("Decode"),
                g.Select(e => e.Value.Type).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                busiest.Value.Type.Length > 0 ? busiest.Value.Type : busiest.Key.Engine);
        }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"luid_0x..._0x..._phys_0" → "0x..._0x...".</summary>
    public static string? MemoryInstanceLuid(string instance)
    {
        var start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || instance.Length < start + 5 + 21) return null;
        return instance.Substring(start + 5, 21).ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal);
    }
}
