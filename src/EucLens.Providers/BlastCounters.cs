using System.Globalization;
using System.Text.RegularExpressions;
using EucLens.Core;

namespace EucLens.Providers;

public sealed record BlastSessionContext(int SessionId, bool AgentInstalled, bool IsActive, string Description);
public interface IBlastSessionContext
{
    BlastSessionContext GetCurrent();
}

public sealed record CounterReading(double? Value, long? RawValue, DateTimeOffset Timestamp,
    MetricStatus Status = MetricStatus.Available, string? Error = null);

/// <summary>All operations are local and synchronous. Providers schedule them off the UI thread.</summary>
public interface IPerformanceCounterReader : IDisposable
{
    IReadOnlyList<string> GetInstances(string category);
    CounterReading Read(string category, string instance, string counter);
    /// <summary>Releases cached native handles and rate baselines.</summary>
    void Reset();
}
public interface IBlastCounterReader : IPerformanceCounterReader;

public sealed record BlastCounterDefinition(string Key, string Category, string Counter, string Unit,
    double Scale = 1, bool UnitVerified = true);

public static partial class BlastCounterCatalog
{
    public const string SessionCategory = "Horizon Blast Session Counters";
    public const string ImagingCategory = "Horizon Blast Imaging Counters";
    public static IReadOnlyList<BlastCounterDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        S("rtt", "RTT", "ms"),
        S("jitter", "Jitter (Uplink)", "raw (unit unverified)", false),
        S("packetLoss", "Packet Loss (Uplink)", "%"),
        S("estimatedBandwidth", "Estimated Bandwidth (Uplink)", "raw (unit unverified)", false),
        S("reconnects", "Automatic Reconnect Count", "count"),
        S("udpReceivedBytes", "Cumulative Received Bytes over UDP", "bytes"),
        S("udpTransmittedBytes", "Cumulative Transmitted Bytes over UDP", "bytes"),
        S("tcpReceivedBytes", "Cumulative Received Bytes over TCP", "bytes"),
        S("tcpTransmittedBytes", "Cumulative Transmitted Bytes over TCP", "bytes"),
        S("udpReceivedInstantaneous", "Instantaneous Received Bytes over UDP", "raw (interval unverified)", false),
        S("udpTransmittedInstantaneous", "Instantaneous Transmitted Bytes over UDP", "raw (interval unverified)", false),
        S("tcpReceivedInstantaneous", "Instantaneous Received Bytes over TCP", "raw (interval unverified)", false),
        S("tcpTransmittedInstantaneous", "Instantaneous Transmitted Bytes over TCP", "raw (interval unverified)", false),
        I("encoderType", "Encoder Type", "raw code"),
        I("fps", "Frames per second", "FPS"),
        I("dirtyFps", "Dirty frames per second", "FPS"),
        I("pollRate", "Poll Rate", "raw (unit unverified)", verified: false),
        I("fbcRate", "FBC Rate", "raw (unit unverified)", verified: false),
        I("totalFrames", "Total frames", "frames"),
        I("totalDirtyFrames", "Total dirty frames", "frames"),
        I("outQueueingTime", "Out Queueing time (us)", "µs"),
        I("imagingInboundMbps", "Inbound Bandwidth (Kbps)", "Mbps", .001),
        I("imagingOutboundMbps", "Outbound Bandwidth (Kbps)", "Mbps", .001),
        I("imagingReceivedPackets", "Received Packets", "packets"),
        I("imagingTransmittedPackets", "Transmitted Packets", "packets"),
        I("imagingReceivedBytes", "Received Bytes", "bytes"),
        I("imagingTransmittedBytes", "Transmitted Bytes", "bytes")
    });

    private static BlastCounterDefinition S(string key, string name, string unit, bool verified = true) =>
        new(key, SessionCategory, name, unit, UnitVerified: verified);
    private static BlastCounterDefinition I(string key, string name, string unit, double scale = 1, bool verified = true) =>
        new(key, ImagingCategory, name, unit, scale, verified);

    [GeneratedRegex(@"^Session ID:\s*(\d+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();

    public static bool MatchesSession(string instance, int sessionId)
    {
        var match = SessionPattern().Match(instance);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) && parsed == sessionId;
    }

    public static Metric Convert(BlastCounterDefinition definition, CounterReading reading, string instance)
    {
        // Interned: the set of counter paths is small and recordings keep one reference per sample.
        var source = string.Intern($@"\{definition.Category}({instance})\{definition.Counter}");
        var value = reading.Status == MetricStatus.Available ? reading.Value * definition.Scale : null;
        var status = reading.Status;
        var error = reading.Error;
        if (status == MetricStatus.Available && (value is null || !double.IsFinite(value.Value)))
        {
            value = null;
            status = MetricStatus.Failed;
            error = "Counter returned a non-finite or missing value.";
        }
        return new(value, definition.Unit, error, source, reading.Timestamp, reading.RawValue, status, error, MetricScope.Session);
    }
}
