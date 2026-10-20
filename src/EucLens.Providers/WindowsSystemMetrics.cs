using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EucLens.Core;
using Microsoft.Win32;

namespace EucLens.Providers;

/// <summary>Cumulative 100 ns system times from GetSystemTimes. Kernel time includes idle time.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
/// <summary>Physical memory from GlobalMemoryStatusEx; commit charge is deliberately not used.</summary>
public readonly record struct PhysicalMemory(ulong TotalBytes, ulong AvailableBytes);
public sealed record CpuInventory(string Model, int? LogicalProcessors, int? PhysicalCores);

/// <summary>Native system queries; mocked in tests.</summary>
public interface ISystemNative
{
    CpuTimes? GetSystemTimes();
    PhysicalMemory? GetPhysicalMemory();
    CpuInventory GetCpuInventory();
    string GetWindowsVersion();
}

public sealed record SystemSample(Metric Cpu, Metric RamUtilization, Metric DiskThroughput,
    string WindowsVersion, IReadOnlyList<MetricEntry> Entries);

/// <summary>Machine-wide CPU, physical memory and disk throughput. Not thread-safe; callers serialize.</summary>
public sealed class WindowsSystemMetricsProvider(ISystemNative native, IPerformanceCounterReader counters, TimeProvider? clock = null) : IDisposable
{
    public const string DiskCategory = "PhysicalDisk";
    public const string DiskInstance = "_Total";
    private const string CpuSource = "GetSystemTimes (kernel32)";
    private const string MemorySource = "GlobalMemoryStatusEx (kernel32)";
    private const string InventorySource = "Registry CentralProcessor\\0 + GetLogicalProcessorInformationEx";
    private const double GiB = 1024d * 1024 * 1024;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private CpuTimes? previous;
    private IReadOnlyList<MetricEntry>? staticEntries;
    private string? windowsVersion;

    public SystemSample Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var entries = new List<MetricEntry>(16);
        entries.AddRange(staticEntries ??= Inventory(now));
        var cpu = Cpu(now);
        entries.Add(new("cpu.utilization", MetricCategory.WindowsCpu, "CPU utilization (all logical processors)", cpu));
        var (ramPercent, memoryEntries) = Memory(now);
        entries.AddRange(memoryEntries);
        var read = Disk("disk.readMBps", "Disk Read Bytes/sec", "Disk read throughput", now);
        var write = Disk("disk.writeMBps", "Disk Write Bytes/sec", "Disk write throughput", now);
        entries.Add(read); entries.Add(write);
        entries.Add(DiskActive(now));
        var total = read.Metric.Status == MetricStatus.Available && write.Metric.Status == MetricStatus.Available
            ? read.Metric with { Value = read.Metric.Value + write.Metric.Value, RawValue = null,
                Source = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec + Disk Write Bytes/sec" }
            : Failure(read.Metric.Status != MetricStatus.Available ? read.Metric : write.Metric, "MB/s");
        return new(cpu, ramPercent, total, windowsVersion ?? "N/A", entries);
    }

    private IReadOnlyList<MetricEntry> Inventory(DateTimeOffset now)
    {
        CpuInventory inventory;
        try { inventory = native.GetCpuInventory(); windowsVersion = native.GetWindowsVersion(); }
        catch (Exception ex) when (IsNativeError(ex)) { inventory = new("N/A", null, null); windowsVersion = "N/A"; }
        Metric Count(int? value, string reason) => value is { } v
            ? new(v, "count", Source: InventorySource, Timestamp: now, RawValue: v, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
            : new(null, "count", reason, InventorySource, now, SampleStatus: MetricStatus.Unavailable, Scope: MetricScope.Machine);
        var text = new Metric(null, "", Source: InventorySource, Timestamp: now, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
        return
        [
            new("cpu.model", MetricCategory.WindowsCpu, "CPU model", text, inventory.Model),
            new("cpu.logicalProcessors", MetricCategory.WindowsCpu, "Logical processors", Count(inventory.LogicalProcessors, "Processor topology query failed.")),
            new("cpu.physicalCores", MetricCategory.WindowsCpu, "Physical cores (as presented to this VM)", Count(inventory.PhysicalCores, "Processor topology query failed.")),
            new("os.version", MetricCategory.WindowsCpu, "Windows version", text with { Source = "Registry CurrentVersion" }, windowsVersion),
        ];
    }

    private Metric Cpu(DateTimeOffset now)
    {
        CpuTimes? times;
        try { times = native.GetSystemTimes(); }
        catch (Exception ex) when (IsNativeError(ex)) { previous = null; return Status(MetricStatus.Failed, "%", CpuSource, now, ex.Message); }
        if (times is not { } current) { previous = null; return Status(MetricStatus.Failed, "%", CpuSource, now, "GetSystemTimes failed."); }
        var last = previous;
        previous = current;
        if (last is not { } old) return Status(MetricStatus.Unavailable, "%", CpuSource, now, "Warming up: CPU utilization requires two samples.");
        if (current.Idle < old.Idle || current.Kernel < old.Kernel || current.User < old.User)
            return Status(MetricStatus.Stale, "%", CpuSource, now, "System time counters moved backwards; baseline reset.");
        var total = (current.Kernel - old.Kernel) + (current.User - old.User);
        if (total == 0) return Status(MetricStatus.Stale, "%", CpuSource, now, "No processor time elapsed since the previous sample.");
        var busy = total - Math.Min(total, current.Idle - old.Idle);
        return new(Math.Clamp(100d * busy / total, 0, 100), "%", Source: CpuSource, Timestamp: now,
            SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
    }

    private (Metric Percent, MetricEntry[] Entries) Memory(DateTimeOffset now)
    {
        PhysicalMemory? memory;
        string? error = null;
        try { memory = native.GetPhysicalMemory(); }
        catch (Exception ex) when (IsNativeError(ex)) { memory = null; error = ex.Message; }
        if (memory is not { TotalBytes: > 0 } m || m.AvailableBytes > m.TotalBytes)
        {
            var failed = Status(MetricStatus.Failed, "%", MemorySource, now, error ?? "GlobalMemoryStatusEx returned no valid physical memory values.");
            return (failed, [new("memory.utilization", MetricCategory.WindowsMemory, "RAM utilization", failed),
                new("memory.totalGiB", MetricCategory.WindowsMemory, "Physical RAM total", failed with { Unit = "GiB" }),
                new("memory.availableGiB", MetricCategory.WindowsMemory, "Physical RAM available", failed with { Unit = "GiB" }),
                new("memory.usedGiB", MetricCategory.WindowsMemory, "Physical RAM in use", failed with { Unit = "GiB" })]);
        }
        var used = m.TotalBytes - m.AvailableBytes;
        Metric Bytes(ulong bytes) => new(bytes / GiB, "GiB", Source: MemorySource, Timestamp: now, RawValue: (long)bytes,
            SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
        var percent = new Metric(100d * used / m.TotalBytes, "%", Source: MemorySource, Timestamp: now,
            SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
        return (percent, [new("memory.utilization", MetricCategory.WindowsMemory, "RAM utilization", percent),
            new("memory.totalGiB", MetricCategory.WindowsMemory, "Physical RAM total", Bytes(m.TotalBytes)),
            new("memory.availableGiB", MetricCategory.WindowsMemory, "Physical RAM available", Bytes(m.AvailableBytes)),
            new("memory.usedGiB", MetricCategory.WindowsMemory, "Physical RAM in use", Bytes(used))]);
    }

    private MetricEntry Disk(string id, string counter, string name, DateTimeOffset now)
    {
        var source = $@"\{DiskCategory}({DiskInstance})\{counter}";
        var reading = Read(counter, now);
        var metric = reading.Status == MetricStatus.Available && reading.Value is { } v && double.IsFinite(v)
            ? new Metric(v / 1_000_000d, "MB/s", Source: source, Timestamp: reading.Timestamp, RawValue: reading.RawValue,
                SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
            : new Metric(null, "MB/s", reading.Error, source, reading.Timestamp, reading.RawValue,
                reading.Status == MetricStatus.Available ? MetricStatus.Failed : reading.Status, reading.Error ?? "Non-finite value.", MetricScope.Machine);
        return new(id, MetricCategory.WindowsDisk, name, metric);
    }

    private MetricEntry DiskActive(DateTimeOffset now)
    {
        const string source = @"100 − \PhysicalDisk(_Total)\% Idle Time";
        var reading = Read("% Idle Time", now);
        var metric = reading.Status == MetricStatus.Available && reading.Value is { } idle && double.IsFinite(idle)
            ? new Metric(Math.Clamp(100 - idle, 0, 100), "%", Source: source, Timestamp: reading.Timestamp, RawValue: reading.RawValue,
                SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)
            : new Metric(null, "%", reading.Error, source, reading.Timestamp, reading.RawValue,
                reading.Status == MetricStatus.Available ? MetricStatus.Failed : reading.Status, reading.Error ?? "Non-finite value.", MetricScope.Machine);
        return new("disk.activePercent", MetricCategory.WindowsDisk, "Disk active time (average of physical disks)", metric);
    }

    private CounterReading Read(string counter, DateTimeOffset now)
    {
        try { return counters.Read(DiskCategory, DiskInstance, counter); }
        catch (Exception ex) when (IsNativeError(ex)) { return new(null, null, now, MetricStatus.Failed, ex.Message); }
    }

    private static Metric Status(MetricStatus status, string unit, string source, DateTimeOffset now, string reason) =>
        new(null, unit, reason, source, now, SampleStatus: status, Error: status == MetricStatus.Failed ? reason : null, Scope: MetricScope.Machine);
    private static Metric Failure(Metric cause, string unit) => cause with { Value = null, Unit = unit, RawValue = null };
    internal static bool IsNativeError(Exception ex) => ex is InvalidOperationException or System.ComponentModel.Win32Exception
        or UnauthorizedAccessException or System.Security.SecurityException or IOException or NotSupportedException
        or ExternalException or DllNotFoundException or EntryPointNotFoundException;

    public void Dispose() => counters.Dispose();
}

public sealed class WindowsSystemNative : ISystemNative
{
    public CpuTimes? GetSystemTimes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetSystemTimesNative(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;
    }

    public PhysicalMemory? GetPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? new PhysicalMemory(status.TotalPhys, status.AvailPhys) : null;
    }

    public CpuInventory GetCpuInventory() =>
        OperatingSystem.IsWindows() ? WindowsInventory() : new("N/A (requires Windows)", null, null);

    public string GetWindowsVersion() => OperatingSystem.IsWindows() ? WindowsVersion() : "N/A (requires Windows)";

    [SupportedOSPlatform("windows")]
    private static CpuInventory WindowsInventory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var model = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "N/A";
        var (logical, cores) = Topology();
        return new(model, logical, cores);
    }

    /// <summary>Counts RelationProcessorCore records and the logical processors in their group masks.</summary>
    private static unsafe (int? Logical, int? Cores) Topology()
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, null, ref length);
        if (length == 0) return (null, null);
        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, start, ref length)) return (null, null);
            int cores = 0, logical = 0;
            for (uint offset = 0; offset + 8 <= length;)
            {
                var record = start + offset;
                var size = *(uint*)(record + 4);
                if (size == 0) break;
                if (*(int*)record == relationProcessorCore)
                {
                    cores++;
                    // PROCESSOR_RELATIONSHIP: GroupCount at +30, GROUP_AFFINITY[] (16 bytes each on x64) at +32.
                    var groups = *(ushort*)(record + 8 + 22);
                    for (var g = 0; g < groups; g++)
                        logical += System.Numerics.BitOperations.PopCount(*(ulong*)(record + 8 + 24 + g * 16));
                }
                offset += size;
            }
            return (logical > 0 ? logical : null, cores > 0 ? cores : null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var display = key?.GetValue("DisplayVersion") as string;
        var build = key?.GetValue("CurrentBuildNumber") as string;
        var ubr = key?.GetValue("UBR") is int revision ? revision : (int?)null;
        // ProductName still reads "Windows 10" on Windows 11; build 22000+ is Windows 11.
        if (int.TryParse(build, out var number) && number >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return $"{product}{(display is null ? "" : " " + display)} (build {build ?? "?"}{(ubr is null ? "" : "." + ubr)})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimesNative(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
}
