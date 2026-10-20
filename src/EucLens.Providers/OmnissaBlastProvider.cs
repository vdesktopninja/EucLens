using System.Collections.ObjectModel;
using System.Globalization;
using EucLens.Core;

namespace EucLens.Providers;

/// <summary>Own-session Blast collection. Native calls are serialized on a worker thread.</summary>
public sealed class OmnissaBlastProvider : IMetricsProvider
{
    private readonly IBlastCounterReader reader;
    private readonly IBlastSessionContext context;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private string? discoverySignature;
    private bool disposed;
    public string Name => "Omnissa Horizon Blast";

    public OmnissaBlastProvider() : this(new WindowsBlastCounterReader(), new WindowsBlastSessionContext()) { }
    public OmnissaBlastProvider(IBlastCounterReader reader, IBlastSessionContext context)
    { this.reader = reader; this.context = context; }

    public async Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var sample = await CollectAsync(cancellationToken).ConfigureAwait(false);
        return new(sample.BlastMetrics!.Values.Any(m => m.Status == MetricStatus.Available), sample.CollectionStatus!);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); }
        finally { gate.Release(); }
    }

    public async Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => Collect(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private SessionSnapshot Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        BlastSessionContext session;
        string? failure = null;
        try { session = context.GetCurrent(); }
        catch (Exception ex) when (IsCollectionError(ex))
        { session = new(-1, false, false, ex.Message); failure = ex.Message; }
        var discovered = new Dictionary<string, (string? Instance, string? Reason, MetricStatus Status)>();
        foreach (var category in new[] { BlastCounterCatalog.SessionCategory, BlastCounterCatalog.ImagingCategory })
        {
            token.ThrowIfCancellationRequested();
            if (!session.AgentInstalled || !session.IsActive)
            {
                discovered[category] = (null, session.Description, failure is null ? MetricStatus.Unavailable : MetricStatus.Failed);
                continue;
            }
            try
            {
                var instances = reader.GetInstances(category).Where(i => BlastCounterCatalog.MatchesSession(i, session.SessionId)).ToArray();
                discovered[category] = instances.Length == 1 ? (instances[0], null, MetricStatus.Available)
                    : (null, instances.Length == 0 ? $"No {category} instance for current Windows session {session.SessionId}."
                        : $"Multiple {category} instances match session {session.SessionId}; refusing ambiguous measurements.", MetricStatus.Unavailable);
            }
            catch (Exception ex) when (IsCollectionError(ex))
            { discovered[category] = (null, ex.Message, MetricStatus.Failed); }
        }
        var signature = $"{session.SessionId}|{session.IsActive}|" + string.Join('|', discovered.Values.Select(d => d.Instance ?? d.Reason));
        if (signature != discoverySignature) { reader.Reset(); discoverySignature = signature; }
        var metrics = new Dictionary<string, Metric>();
        foreach (var definition in BlastCounterCatalog.All)
        {
            token.ThrowIfCancellationRequested();
            var discovery = discovered[definition.Category];
            CounterReading reading;
            if (discovery.Instance is null) reading = new(null, null, now, discovery.Status, discovery.Reason);
            else
            {
                try { reading = reader.Read(definition.Category, discovery.Instance, definition.Counter); }
                catch (Exception ex) when (IsCollectionError(ex)) { reading = new(null, null, now, MetricStatus.Failed, ex.Message); }
            }
            metrics[definition.Key] = BlastCounterCatalog.Convert(definition, reading, discovery.Instance ?? $"Session ID: {session.SessionId}; unavailable");
        }
        token.ThrowIfCancellationRequested();
        Metric Missing(string unit, string reason) => new(null, unit, reason, Name, now, SampleStatus: MetricStatus.Unavailable);
        var unavailable = Missing("%", "Not exposed by the Blast counter sets.");
        var count = metrics.Values.Count(m => m.Status == MetricStatus.Available);
        var reasons = metrics.Values.Where(m => m.Status != MetricStatus.Available).Select(m => m.UnavailableReason).Distinct();
        var status = $"REAL SESSION · {count}/{metrics.Count} counters available · Windows session {session.SessionId}";
        if (count != metrics.Count) status += " · " + string.Join(" ", reasons);
        // The protocol is only claimed when this session's own Blast counter instance exists.
        var blastActive = discovered.Values.Any(d => d.Instance is not null);
        var detection = blastActive
            ? $"Omnissa Horizon Blast: counter instance found for Windows session {session.SessionId}."
            : $"Not detected: no Horizon Blast counters for Windows session {session.SessionId} ({session.Description})";
        return new(now,
            new(blastActive ? RemoteProtocol.OmnissaBlast : RemoteProtocol.Unknown, session.SessionId, Environment.UserName, "N/A", Environment.MachineName,
                Environment.OSVersion.ToString(), "N/A (negotiated transport not exposed)", started, false, detection),
            WorkloadContext.Idle,
            new(metrics["rtt"], Missing("ms", "One-way network latency is not exposed."),
                Missing("Mbps", "Estimated Bandwidth (Uplink) unit is unverified; see the raw counter."),
                metrics["imagingOutboundMbps"], metrics["packetLoss"]),
            new(metrics["fps"], metrics["encoderType"].Value is { } code ? $"Raw code {code.ToString(CultureInfo.InvariantCulture)}" : "N/A",
                "N/A (encoder code mapping unverified)", unavailable, "Blast Extreme", null, "N/A"),
            new("N/A", unavailable, unavailable, unavailable, unavailable, "N/A"),
            new(unavailable, unavailable, Missing("MB/s", "Not exposed by the Blast counter sets.")),
            BlastMetrics: new ReadOnlyDictionary<string, Metric>(metrics), CollectionStatus: status);
    }

    private static bool IsCollectionError(Exception ex) => ex is InvalidOperationException or System.ComponentModel.Win32Exception
        or UnauthorizedAccessException or System.Security.SecurityException or IOException or NotSupportedException;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; reader.Dispose(); } }
        finally { gate.Release(); }
    }
}
