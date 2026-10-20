using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EucLens.Providers;

public interface IGpuAdapterSource
{
    /// <summary>Static inventory. Called rarely (startup and periodic refresh), never every sample.</summary>
    IReadOnlyList<GpuAdapterInfo> Enumerate();
}

public sealed record GpuCounterRead<T>(IReadOnlyList<T> Values, Core.MetricStatus Status, string? Reason, DateTimeOffset Timestamp);
public readonly record struct GpuMemoryReading(string Instance, double DedicatedBytes, double SharedBytes);

/// <summary>Bulk reads of the "GPU Engine" and "GPU Adapter Memory" counter sets (Windows 10 1709+).</summary>
public interface IGpuCounterSource : IDisposable
{
    GpuCounterRead<GpuEngineReading> ReadEngines();
    GpuCounterRead<GpuMemoryReading> ReadAdapterMemory();
    /// <summary>Windows session of a process, or null when the process has exited or is inaccessible.</summary>
    int? SessionOf(int processId);
}

/// <summary>DXGI 1.1 enumeration through COM vtable slots; no WMI.</summary>
public sealed class DxgiAdapterSource : IGpuAdapterSource
{
    public IReadOnlyList<GpuAdapterInfo> Enumerate() => OperatingSystem.IsWindows() ? EnumerateWindows() : [];

    [SupportedOSPlatform("windows")]
    private static unsafe GpuAdapterInfo[] EnumerateWindows()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == 0)
            throw new InvalidOperationException("CreateDXGIFactory1 failed.");
        var drivers = DisplayDrivers();
        var adapters = new List<GpuAdapterInfo>();
        try
        {
            // IDXGIFactory1 vtable: IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11, EnumAdapters1 = 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint i = 0; i < 64; i++)
            {
                nint adapter = 0;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                if (hr < 0 || adapter == 0) continue;
                try
                {
                    // IDXGIAdapter1 vtable: IDXGIAdapter 7-9, GetDesc1 = 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)(*(nint**)adapter)[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) < 0) continue;
                    var name = new string(desc.Description).TrimEnd('\0').Trim();
                    drivers.TryGetValue((desc.VendorId, desc.DeviceId), out var driver);
                    adapters.Add(new((int)i, name, desc.VendorId, desc.DeviceId, desc.SubSysId,
                        desc.DedicatedVideoMemory, desc.DedicatedSystemMemory, desc.SharedSystemMemory,
                        GpuAdapterInfo.FormatLuid(desc.LuidHigh, desc.LuidLow),
                        (desc.Flags & 2) != 0, (desc.Flags & 1) != 0, driver.Version, driver.Date));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return adapters.ToArray();
    }

    private static unsafe void Release(nint unknown) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    /// <summary>Display-class driver keys are user-readable; matched by PCI vendor/device ID.</summary>
    [SupportedOSPlatform("windows")]
    private static Dictionary<(uint, uint), (string? Version, string? Date)> DisplayDrivers()
    {
        var result = new Dictionary<(uint, uint), (string?, string?)>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (root is null) return result;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
            try
            {
                using var key = root.OpenSubKey(name);
                var id = (key?.GetValue("MatchingDeviceId") as string)?.ToUpperInvariant();
                if (id is null) continue;
                var ven = id.IndexOf("VEN_", StringComparison.Ordinal);
                var dev = id.IndexOf("DEV_", StringComparison.Ordinal);
                if (ven < 0 || dev < 0 || id.Length < dev + 8 ||
                    !uint.TryParse(id.AsSpan(ven + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var v) ||
                    !uint.TryParse(id.AsSpan(dev + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var d)) continue;
                result.TryAdd((v, d), (key!.GetValue("DriverVersion") as string, key.GetValue("DriverDate") as string));
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemoryRaw, DedicatedSystemMemoryRaw, SharedSystemMemoryRaw;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
        public readonly ulong DedicatedVideoMemory => DedicatedVideoMemoryRaw;
        public readonly ulong DedicatedSystemMemory => DedicatedSystemMemoryRaw;
        public readonly ulong SharedSystemMemory => SharedSystemMemoryRaw;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);
}

public sealed class WindowsGpuCounterSource : IGpuCounterSource
{
    public const string EngineCategory = "GPU Engine";
    public const string MemoryCategory = "GPU Adapter Memory";
    private Dictionary<string, RawSample> previousEngines = new(StringComparer.Ordinal);
    /// <summary>CounterSample fields; CounterSample itself is Windows-only and cannot be stored here.</summary>
    private readonly record struct RawSample(long Raw, long Base, long Frequency, long SystemFrequency, long TimeStamp, long TimeStamp100nSec, int Type, long CounterTimeStamp);
    private readonly Dictionary<int, int?> sessions = [];
    private DateTimeOffset nextSessionPurge;

    public GpuCounterRead<GpuEngineReading> ReadEngines()
    {
        var now = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows()) return new([], Core.MetricStatus.Unavailable, "GPU performance counters require Windows.", now);
        return ReadEnginesWindows(now);
    }

    [SupportedOSPlatform("windows")]
    private GpuCounterRead<GpuEngineReading> ReadEnginesWindows(DateTimeOffset now)
    {
        if (!PerformanceCounterCategory.Exists(EngineCategory))
            return new([], Core.MetricStatus.Unavailable, "\"GPU Engine\" counters are not present (needs Windows 10 1709+ and a WDDM 2.x GPU driver).", now);
        // One ReadCategory call returns every engine instance; reading per-instance counters would cost far more.
        var data = new PerformanceCounterCategory(EngineCategory).ReadCategory();
        if (!data.Contains("Utilization Percentage"))
            return new([], Core.MetricStatus.Unavailable, "\"GPU Engine\\Utilization Percentage\" is missing.", now);
        var current = new Dictionary<string, RawSample>(StringComparer.Ordinal);
        var readings = new List<GpuEngineReading>();
        foreach (InstanceData instance in data["Utilization Percentage"]!.Values)
        {
            var sample = instance.Sample;
            current[instance.InstanceName] = new(sample.RawValue, sample.BaseValue, sample.CounterFrequency, sample.SystemFrequency,
                sample.TimeStamp, sample.TimeStamp100nSec, (int)sample.CounterType, sample.CounterTimeStamp);
            if (previousEngines.TryGetValue(instance.InstanceName, out var old) && sample.TimeStamp100nSec > old.TimeStamp100nSec)
            {
                var previous = new CounterSample(old.Raw, old.Base, old.Frequency, old.SystemFrequency, old.TimeStamp,
                    old.TimeStamp100nSec, (PerformanceCounterType)old.Type, old.CounterTimeStamp);
                readings.Add(new(instance.InstanceName, CounterSample.Calculate(previous, sample)));
            }
        }
        var warming = previousEngines.Count == 0;
        previousEngines = current;
        if (warming) return new([], Core.MetricStatus.Unavailable, "Warming up: GPU engine utilization requires two samples.", now);
        return new(readings, Core.MetricStatus.Available, null, now);
    }

    public GpuCounterRead<GpuMemoryReading> ReadAdapterMemory()
    {
        var now = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows()) return new([], Core.MetricStatus.Unavailable, "GPU performance counters require Windows.", now);
        return ReadMemoryWindows(now);
    }

    [SupportedOSPlatform("windows")]
    private static GpuCounterRead<GpuMemoryReading> ReadMemoryWindows(DateTimeOffset now)
    {
        if (!PerformanceCounterCategory.Exists(MemoryCategory))
            return new([], Core.MetricStatus.Unavailable, "\"GPU Adapter Memory\" counters are not present.", now);
        var data = new PerformanceCounterCategory(MemoryCategory).ReadCategory();
        if (!data.Contains("Dedicated Usage") || !data.Contains("Shared Usage"))
            return new([], Core.MetricStatus.Unavailable, "GPU Adapter Memory usage counters are missing.", now);
        var shared = data["Shared Usage"]!;
        var readings = new List<GpuMemoryReading>();
        foreach (InstanceData instance in data["Dedicated Usage"]!.Values)
        {
            var sharedBytes = shared.Contains(instance.InstanceName) ? shared[instance.InstanceName]!.RawValue : double.NaN;
            readings.Add(new(instance.InstanceName, instance.RawValue, sharedBytes));
        }
        return new(readings, Core.MetricStatus.Available, null, now);
    }

    public int? SessionOf(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var now = DateTimeOffset.UtcNow;
        // Process IDs are reused; a periodic purge keeps the cache bounded and correct.
        if (now >= nextSessionPurge) { sessions.Clear(); nextSessionPurge = now.AddSeconds(30); }
        if (sessions.TryGetValue(processId, out var cached)) return cached;
        int? session = ProcessIdToSessionId((uint)processId, out var id) ? (int)id : null;
        sessions[processId] = session;
        return session;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    public void Dispose() { previousEngines.Clear(); sessions.Clear(); }
}
