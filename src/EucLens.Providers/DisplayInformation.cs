using System.Globalization;
using System.Runtime.InteropServices;
using EucLens.Core;

namespace EucLens.Providers;

public sealed record MonitorInfo(string DeviceName, bool IsPrimary, int Left, int Top, int Width, int Height,
    int? RefreshHz, int? DpiX);
public sealed record DisplayTopology(IReadOnlyList<MonitorInfo> Monitors, bool IsRemoteSession);

public interface IDisplayInfoSource { DisplayTopology Read(); }

public sealed record DisplaySample(string PrimaryResolution, string MonitorSummary, IReadOnlyList<MetricEntry> Entries);

/// <summary>
/// Describes the Windows desktop of this session, i.e. what the remote display protocol renders. The client's
/// physical monitors may differ (client-side scaling, partial monitor use). Cached briefly: topology rarely changes.
/// </summary>
public sealed class DisplayInformationProvider(IDisplayInfoSource source, TimeProvider? clock = null)
{
    public const string Source = "EnumDisplayMonitors / EnumDisplaySettings / GetDpiForMonitor (this session's desktop)";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private DisplaySample? cached;
    private DateTimeOffset expires;

    public DisplaySample Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (cached is not null && now < expires) return cached;
        expires = now + CacheDuration;
        DisplayTopology topology;
        try { topology = source.Read(); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex))
        {
            var failed = WindowsGpuMetricsProvider.Text("display.status", "Display information", $"Failed: {ex.Message}", Source, now,
                MetricStatus.Failed, MetricCategory.Display, MetricScope.Session);
            return cached = new("N/A", "N/A", [failed]);
        }
        if (topology.Monitors.Count == 0)
        {
            var none = WindowsGpuMetricsProvider.Text("display.status", "Display information", "No monitors enumerated (requires an interactive Windows desktop).",
                Source, now, MetricStatus.Unavailable, MetricCategory.Display, MetricScope.Session);
            return cached = new("N/A", "N/A", [none]);
        }
        var ordered = topology.Monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToArray();
        var primary = ordered[0];
        static string Scale(int? dpi) => dpi is { } d ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(d * 100 / 96.0)} %") : "N/A";
        static string Describe(MonitorInfo m) => string.Create(CultureInfo.InvariantCulture,
            $"{m.Width} × {m.Height}{(m.RefreshHz is > 1 ? $" @ {m.RefreshHz} Hz" : "")} at ({m.Left}, {m.Top}), scale {Scale(m.DpiX)}{(m.IsPrimary ? ", primary" : "")}");
        var entries = new List<MetricEntry>
        {
            new("display.monitorCount", MetricCategory.Display, "Active monitors",
                new(ordered.Length, "count", Source: Source, Timestamp: now, RawValue: ordered.Length, SampleStatus: MetricStatus.Available, Scope: MetricScope.Session)),
            Text("display.primary", "Primary display", Describe(primary), now),
            Text("display.remoteSession", "Remote session (SM_REMOTESESSION)", topology.IsRemoteSession
                ? "Yes — values describe the remote desktop, not the client's physical monitors" : "No — local console session", now),
            Text("display.topology", "Topology", string.Join(" | ", ordered.Select(m => $"{m.DeviceName}: {Describe(m)}")), now),
        };
        if (primary.DpiX is { } dpi)
            entries.Add(new("display.primaryScalePercent", MetricCategory.Display, "Primary display scaling",
                new(Math.Round(dpi * 100 / 96.0), "%", Source: Source, Timestamp: now, RawValue: dpi, SampleStatus: MetricStatus.Available, Scope: MetricScope.Session)));
        for (var i = 0; i < ordered.Length; i++)
            entries.Add(Text($"display.monitor{i + 1}", $"Monitor {i + 1} ({ordered[i].DeviceName})", Describe(ordered[i]), now));
        var resolution = string.Create(CultureInfo.InvariantCulture, $"{primary.Width} × {primary.Height}");
        var summary = string.Create(CultureInfo.InvariantCulture, $"{ordered.Length} · primary scale {Scale(primary.DpiX)}");
        return cached = new(resolution, summary, entries);
    }

    private static MetricEntry Text(string id, string name, string text, DateTimeOffset now) =>
        WindowsGpuMetricsProvider.Text(id, name, text, Source, now, MetricStatus.Available, MetricCategory.Display, MetricScope.Session);
}

public sealed class WindowsDisplayInfoSource : IDisplayInfoSource
{
    public DisplayTopology Read()
    {
        if (!OperatingSystem.IsWindows()) return new([], false);
        var handles = new List<nint>();
        bool Callback(nint monitor, nint hdc, nint rect, nint data) { handles.Add(monitor); return true; }
        MonitorEnumProc callback = Callback;
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        var monitors = new List<MonitorInfo>(handles.Count);
        foreach (var handle in handles)
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(handle, ref info)) continue;
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            var hasMode = EnumDisplaySettingsW(info.Device, -1 /* ENUM_CURRENT_SETTINGS */, ref mode);
            int? dpi = GetDpiForMonitor(handle, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 ? (int)dpiX : null;
            // Mode width/height are physical pixels regardless of this process's DPI awareness.
            var width = hasMode ? (int)mode.PelsWidth : info.Monitor.Right - info.Monitor.Left;
            var height = hasMode ? (int)mode.PelsHeight : info.Monitor.Bottom - info.Monitor.Top;
            monitors.Add(new(info.Device, (info.Flags & 1) != 0, info.Monitor.Left, info.Monitor.Top, width, height,
                hasMode ? (int)mode.DisplayFrequency : null, dpi));
        }
        return new(monitors, GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0);
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode mode);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
