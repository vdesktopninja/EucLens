using System.Runtime.InteropServices;
using EucLens.Core;

namespace EucLens.Providers;

public sealed record HostSample(SystemMetrics System, GpuMetrics Gpu, string Resolution, string MonitorSummary,
    string WindowsVersion, IReadOnlyList<MetricEntry> Entries);

public interface IHostMetricsCollector : IDisposable
{
    HostSample Collect(int sessionId, CancellationToken token);
}

/// <summary>Protocol-independent host metrics. Each collector is isolated so one failure never hides the others.</summary>
public sealed class HostMetricsCollector(WindowsSystemMetricsProvider system, WindowsGpuMetricsProvider gpu,
    DisplayInformationProvider display, TimeProvider? clock = null) : IHostMetricsCollector
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public HostMetricsCollector() : this(new WindowsSystemMetricsProvider(new WindowsSystemNative(), new WindowsPerformanceCounterReader()),
        new WindowsGpuMetricsProvider(new DxgiAdapterSource(), new WindowsGpuCounterSource(), () => new NvmlClient()),
        new DisplayInformationProvider(new WindowsDisplayInfoSource())) { }

    public HostSample Collect(int sessionId, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var entries = new List<MetricEntry>(96);
        SystemSample s;
        try { s = system.Collect(token); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { s = FailedSystem(ex, now); }
        entries.AddRange(s.Entries);
        GpuSample g;
        try { g = gpu.Collect(sessionId, token); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { g = FailedGpu(ex, now); }
        entries.AddRange(g.Entries);
        DisplaySample d;
        try { d = display.Collect(token); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { d = new("N/A", "N/A", []); }
        entries.AddRange(d.Entries);
        return new(new(s.Cpu, s.RamUtilization, s.DiskThroughput), g.Dashboard, d.PrimaryResolution, d.MonitorSummary, s.WindowsVersion, entries);
    }

    private static SystemSample FailedSystem(Exception ex, DateTimeOffset now)
    {
        var failed = new Metric(null, "%", ex.Message, "Windows system collectors", now, SampleStatus: MetricStatus.Failed, Error: ex.Message, Scope: MetricScope.Machine);
        return new(failed, failed, failed with { Unit = "MB/s" }, "N/A", [new("system.status", MetricCategory.WindowsCpu, "System collection", failed)]);
    }

    private static GpuSample FailedGpu(Exception ex, DateTimeOffset now)
    {
        var failed = new Metric(null, "%", ex.Message, "Windows GPU collectors", now, SampleStatus: MetricStatus.Failed, Error: ex.Message, Scope: MetricScope.Machine);
        return new(new("N/A", failed, failed, failed, failed, "N/A"), [new("gpu.status", MetricCategory.Gpu, "GPU collection", failed)]);
    }

    public void Dispose() { system.Dispose(); gpu.Dispose(); }
}

/// <summary>What Windows reports about the session's client protocol (WTSClientProtocolType).</summary>
public interface ISessionProtocolProbe { string Describe(int sessionId); }

public sealed class WtsSessionProtocolProbe : ISessionProtocolProbe
{
    public string Describe(int sessionId)
    {
        if (!OperatingSystem.IsWindows()) return "Windows client protocol type unavailable (requires Windows).";
        if (!WTSQuerySessionInformation(0, sessionId, 16 /* WTSClientProtocolType */, out var buffer, out var bytes))
            return $"Windows client protocol type unavailable (error {Marshal.GetLastWin32Error()}).";
        try
        {
            var type = bytes >= 2 ? Marshal.ReadInt16(buffer) : -1;
            // Third-party protocol stacks may register as RDP, so this is reported, not used to label the session.
            return type switch
            {
                0 => "Windows reports a console session (WTS protocol type 0).",
                1 => "Windows reports protocol type 1 (ICA, reserved for Citrix).",
                2 => "Windows reports protocol type 2 (RDP or an RDP-registered protocol stack).",
                _ => $"Windows reports protocol type {type}."
            };
        }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytesReturned);
    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}

/// <summary>
/// Real Session mode: Horizon Blast counters (session-scoped) merged with protocol-independent host metrics
/// (machine-scoped). Both run concurrently off the UI thread; one failing never blocks the other.
/// </summary>
public sealed class RealSessionMetricsProvider(OmnissaBlastProvider blast, IHostMetricsCollector host, ISessionProtocolProbe protocol) : IMetricsProvider
{
    private readonly SemaphoreSlim hostGate = new(1, 1);
    private readonly int sessionId = CurrentSessionId();
    private bool disposed;
    public string Name => "Real session (Horizon Blast + Windows host)";

    public Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderAvailability(true, "Windows host metrics are always collected; Blast metrics when a Blast session is active."));

    public Task InitializeAsync(CancellationToken cancellationToken = default) => blast.InitializeAsync(cancellationToken);

    public async Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var blastTask = blast.CollectAsync(cancellationToken);
        var hostTask = Task.Run(async () =>
        {
            await hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return host.Collect(sessionId, cancellationToken); }
            finally { hostGate.Release(); }
        }, cancellationToken);
        // WhenAll observes both tasks, so a failure in one never leaves an unobserved exception in the other.
        await Task.WhenAll(blastTask, hostTask).ConfigureAwait(false);
        var s = await blastTask.ConfigureAwait(false);
        var h = await hostTask.ConfigureAwait(false);
        var detection = s.Session.Protocol == RemoteProtocol.OmnissaBlast
            ? s.Session.ProtocolDetection
            : $"{s.Session.ProtocolDetection} {SafeProbe(s.Session.SessionId)}".Trim();
        var cpu = h.System.CpuUtilization.Display;
        return s with
        {
            Session = s.Session with { OperatingSystem = h.WindowsVersion, ProtocolDetection = detection },
            Graphics = s.Graphics with { Resolution = h.Resolution == "N/A" ? "N/A" : $"{h.Resolution} (session desktop)" },
            Gpu = h.Gpu,
            System = h.System,
            HostMetrics = h.Entries,
            CollectionStatus = $"{s.CollectionStatus} · Host: CPU {cpu}, {h.Entries.Count(e => e.Metric.Status == MetricStatus.Available)}/{h.Entries.Count} host metrics available",
        };
    }

    private static int CurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }

    private string SafeProbe(int sessionId)
    {
        try { return protocol.Describe(sessionId); }
        catch (Exception ex) when (WindowsSystemMetricsProvider.IsNativeError(ex)) { return $"Windows client protocol type unavailable ({ex.Message})."; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await blast.DisposeAsync().ConfigureAwait(false);
        await hostGate.WaitAsync().ConfigureAwait(false);
        try { host.Dispose(); }
        finally { hostGate.Release(); }
    }
}
