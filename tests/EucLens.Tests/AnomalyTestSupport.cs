using EucLens.Core;

namespace EucLens.Tests;

/// <summary>
/// Synthetic recordings for the anomaly engine, built directly as analyzer input (no disk, no recorder): a healthy Horizon Blast session at one sample per second with small
/// deterministic variation, which each test then bends. Nothing here is random, so every run sees the same numbers.
/// </summary>
internal sealed class Synthetic
{
    public static readonly DateTimeOffset T0 = new(2026, 3, 4, 14, 0, 0, TimeSpan.Zero);
    public const string Blast = "Omnissa Horizon Blast";

    private readonly Dictionary<string, (string Unit, double[] Values)> channels = new(StringComparer.Ordinal);
    private readonly List<int> skipped = [];
    public List<HistoryEvent> Events { get; } = [];
    public List<string> Protocols { get; } = [Blast];
    public List<string> Limitations { get; } = [];
    public bool ReducedConfidence { get; set; }
    public TimeSpan Step { get; }
    public int Count { get; }
    public string Key { get; set; } = "synthetic";

    /// <param name="healthy">False leaves the channels empty so a test can define only what it needs.</param>
    public Synthetic(int count, double stepSeconds = 1, bool healthy = true)
    {
        Count = count; Step = TimeSpan.FromSeconds(stepSeconds);
        if (!healthy) return;
        Add("rtt", "ms", i => 30 + 3 * Math.Sin(i * 0.37));
        Add("packetLoss", "%", _ => 0);
        Add("fps", "FPS", i => 30 + 2 * Math.Sin(i * 0.23));
        Add("dirtyFps", "FPS", i => 20 + 4 * Math.Sin(i * 0.11));
        Add("droppedFrames", "%", i => 0.5 + 0.2 * Math.Sin(i * 0.5));
        Add("outQueueing", "µs", i => 2000 + 300 * Math.Sin(i * 0.31));
        Add("cpu", "%", i => 25 + 4 * Math.Sin(i * 0.19));
        Add("ram", "%", i => 45 + 1 * Math.Sin(i * 0.05));
        Add("disk", "MB/s", i => 3 + 1 * Math.Sin(i * 0.41));
    }

    public DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);
    public int Index(double seconds) => (int)Math.Round(seconds / Step.TotalSeconds);

    public Synthetic Add(string id, string unit, Func<int, double> value)
    {
        var v = new double[Count];
        for (var i = 0; i < Count; i++) v[i] = value(i);
        channels[id] = (unit, v);
        return this;
    }

    public Synthetic Remove(string id) { channels.Remove(id); return this; }

    public Synthetic Unit(string id, string unit) { channels[id] = (unit, channels[id].Values); return this; }

    /// <summary>Sets a metric to a value over [fromSecond, toSecond) of the recording.</summary>
    public Synthetic Set(string id, double fromSecond, double toSecond, double value) => Set(id, fromSecond, toSecond, _ => value);

    public Synthetic Set(string id, double fromSecond, double toSecond, Func<int, double> value)
    {
        var v = channels[id].Values;
        for (var i = Math.Max(0, Index(fromSecond)); i < Math.Min(Count, Index(toSecond)); i++) v[i] = value(i);
        return this;
    }

    public Synthetic Missing(string id, double fromSecond, double toSecond) => Set(id, fromSecond, toSecond, double.NaN);
    public Synthetic MissingAll(double fromSecond, double toSecond) { foreach (var id in channels.Keys.ToArray()) Missing(id, fromSecond, toSecond); return this; }

    /// <summary>Removes the samples in [fromSecond, toSecond): a gap in the recording.</summary>
    public Synthetic Skip(double fromSecond, double toSecond)
    {
        for (var i = Math.Max(0, Index(fromSecond)); i < Math.Min(Count, Index(toSecond)); i++) skipped.Add(i);
        return this;
    }

    public Synthetic Event(HistoryEventKind kind, double second, string? title = null)
    {
        Events.Add(new(kind, At(second), title ?? kind.ToString(), Order: Events.Count));
        return this;
    }

    /// <param name="order">Which samples to hand over, in which order: a permutation puts them out of order, a repeated index gives a duplicate timestamp.</param>
    public AnomalyInput ToInput(IReadOnlyList<int>? order = null)
    {
        var skippedSet = skipped.ToHashSet();
        var keep = order?.ToArray() ?? Enumerable.Range(0, Count).Where(i => !skippedSet.Contains(i)).ToArray();
        var ticks = keep.Select(i => (T0 + TimeSpan.FromTicks(Step.Ticks * i)).UtcTicks).ToArray();
        var list = channels.Select(c => new AnomalyChannel(c.Key, c.Value.Unit, keep.Select(i => c.Value.Values[i]).ToArray())).ToList();
        return AnomalyInput.Create(Key, ticks, list, Events, Protocols, null, Limitations, ReducedConfidence);
    }

    public AnomalyAnalysis Analyze(AnomalyOptions? options = null, CancellationToken token = default) => AnomalyDetectionEngine.Analyze(ToInput(), options, token);
}
