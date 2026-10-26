namespace EucLens.Core;

/// <summary>
/// The metrics the history can chart. A channel is offered for a recording only if it holds at least one valid value; its unit is the unit the
/// recording stored (raw counters keep their raw-unit label and are flagged as unverified, never converted).
/// </summary>
public static class HistoryChannels
{
    private static Metric? Blast(SessionSnapshot s, string key) => s.BlastMetrics is { } m && m.TryGetValue(key, out var metric) ? metric : null;

    public static IReadOnlyList<HistoryChannel> All { get; } =
    [
        new("rtt", "Round-trip time", HistoryGroup.Connection, true, s => s.Connection.RoundTripTime, "Protocol round-trip time"),
        new("packetLoss", "Packet loss", HistoryGroup.Connection, true, s => s.Connection.PacketLoss),
        new("availableBandwidth", "Available bandwidth", HistoryGroup.Connection, false, s => s.Connection.AvailableBandwidth, "Estimated bandwidth as reported; shown in raw counter units when the unit is not verified"),
        new("bandwidthConsumption", "Bandwidth consumption", HistoryGroup.Connection, true, s => s.Connection.BandwidthConsumption),
        new("fps", "Frames per second", HistoryGroup.Graphics, false, s => s.Graphics.FramesPerSecond),
        new("dirtyFps", "Dirty frames per second", HistoryGroup.Graphics, false, s => Blast(s, "dirtyFps")),
        new("droppedFrames", "Dropped frames", HistoryGroup.Graphics, true, s => s.Graphics.DroppedFramesPercent),
        new("outQueueing", "Out queueing time", HistoryGroup.Graphics, true, s => Blast(s, "outQueueingTime"), "Image delivery queueing"),
        new("imagingOutbound", "Image delivery, outbound", HistoryGroup.Graphics, true, s => Blast(s, "imagingOutboundMbps")),
        new("imagingInbound", "Image delivery, inbound", HistoryGroup.Graphics, true, s => Blast(s, "imagingInboundMbps")),
        new("cpu", "CPU utilization", HistoryGroup.Guest, true, s => s.System.CpuUtilization),
        new("ram", "Memory utilization", HistoryGroup.Guest, true, s => s.System.RamUtilization),
        new("disk", "Disk activity", HistoryGroup.Guest, true, s => s.System.DiskActivity),
        new("gpu", "GPU utilization", HistoryGroup.Guest, true, s => s.Gpu.Utilization),
    ];

    public static HistoryChannel? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    public static string GroupTitle(HistoryGroup group) => group switch { HistoryGroup.Connection => "Connection", HistoryGroup.Graphics => "Graphics", _ => "VDI Guest" };

    /// <summary>A raw counter whose unit is not documented: shown as recorded, with this warning, and never interpreted.</summary>
    public static bool IsUnverified(string unit) => unit.StartsWith("raw", StringComparison.OrdinalIgnoreCase);

    public const string UnverifiedWarning = "Raw counter, unit not verified: values are shown as recorded and are not converted or interpreted.";

    /// <summary>A usable value: the metric is available and finite. Stale, failed and unavailable measurements are never valid.</summary>
    public static bool TryGetValid(Metric? metric, out double value)
    {
        if (metric is { Status: MetricStatus.Available, Value: { } v } && double.IsFinite(v)) { value = v; return true; }
        value = double.NaN;
        return false;
    }
}
