using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EucLens.Core;

/// <summary>UI owns mutation; exports take a stable copy before asynchronous I/O.</summary>
public sealed class SessionRecorder : ISessionRecorder
{
    public const int MaximumSamples = 86_400;
    private readonly List<SessionSnapshot> samples = [];
    private readonly Stopwatch elapsed = new();
    public bool IsRecording { get; private set; }
    public int SampleCount => samples.Count;
    public TimeSpan Duration => elapsed.Elapsed;
    public IReadOnlyList<SessionSnapshot> Samples => samples.AsReadOnly();
    public void Start()
    {
        if (IsRecording) throw new InvalidOperationException("A recording is already active.");
        samples.Clear();
        elapsed.Restart();
        IsRecording = true;
    }
    public void Stop() { IsRecording = false; elapsed.Stop(); }
    public void Add(SessionSnapshot sample)
    {
        if (!IsRecording) return;
        if (samples.Count >= MaximumSamples) { Stop(); return; }
        samples.Add(sample);
        if (samples.Count == MaximumSamples) Stop();
    }
    public Task ExportJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        var copy = samples.ToArray();
        return Task.Run(() => WriteAtomicAsync(path, async stream =>
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            await JsonSerializer.SerializeAsync(stream, copy, options, cancellationToken);
        }, cancellationToken), cancellationToken);
    }
    public Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var copy = samples.ToArray();
        return Task.Run(() => WriteAtomicAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            // Real providers add counter-level metrics; columns are the union in first-seen order.
            var blastKeys = copy.SelectMany(s => s.BlastMetrics?.Keys ?? Enumerable.Empty<string>()).Distinct().ToArray();
            var blastHeader = string.Concat(blastKeys.Select(k => $",blast_{k},blast_{k}_unit,blast_{k}_status,blast_{k}_raw,blast_{k}_source"));
            // Schema 0.3 appends columns only, so existing column positions are unchanged.
            var hostKeys = copy.SelectMany(s => s.HostMetrics?.Select(e => e.Id) ?? []).Distinct().ToArray();
            var hostHeader = ",protocol_detection" + string.Concat(hostKeys.Select(k => $",host_{k},host_{k}_unit,host_{k}_status,host_{k}_raw,host_{k}_scope,host_{k}_source"));
            await writer.WriteLineAsync("timestamp,data_source,is_demo,protocol,session_id,workload,demo_scenario,rtt_ms,network_latency_ms,available_mbps,consumed_mbps,packet_loss_percent,fps,dropped_frames_percent,encoder,codec,graphics_mode,hardware_encoding,resolution,gpu_model,gpu_percent,vram_percent,encoder_percent,decoder_percent,driver,cpu_percent,ram_percent,disk_mbps,collection_status" + blastHeader + hostHeader);
            foreach (var s in copy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Non-measurements are written as their status, never as zero.
                string V(Metric m) => m.Value?.ToString("R", CultureInfo.InvariantCulture) ?? m.Status.ToString();
                string Q(string? text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
                var blast = string.Concat(blastKeys.Select(k => s.BlastMetrics?.TryGetValue(k, out var m) == true
                    ? $",{V(m)},{Q(m.Unit)},{m.Status},{m.RawValue?.ToString(CultureInfo.InvariantCulture) ?? ""},{Q(m.Source)}"
                    : ",Unavailable,\"\",Unavailable,,\"\""));
                var hostById = s.HostMetrics?.ToDictionary(e => e.Id, StringComparer.Ordinal);
                var host = "," + Q(s.Session.ProtocolDetection) + string.Concat(hostKeys.Select(k => hostById?.TryGetValue(k, out var e) == true
                    ? $",{(e.Text is null ? V(e.Metric) : Q(e.Text))},{Q(e.Metric.Unit)},{e.Metric.Status},{e.Metric.RawValue?.ToString(CultureInfo.InvariantCulture) ?? ""},{e.Metric.Scope?.ToString() ?? ""},{Q(e.Metric.Source)}"
                    : ",Unavailable,\"\",Unavailable,,,\"\""));
                await writer.WriteLineAsync(string.Join(",", s.Timestamp.ToString("O"), s.Session.IsDemo ? "Demo" : "RealSession", s.Session.IsDemo,
                    s.Session.Protocol, s.Session.SessionId, s.Workload, s.DemoScenario?.ToString() ?? "Unavailable", V(s.Connection.RoundTripTime),
                    V(s.Connection.NetworkLatency), V(s.Connection.AvailableBandwidth), V(s.Connection.BandwidthConsumption),
                    V(s.Connection.PacketLoss), V(s.Graphics.FramesPerSecond), V(s.Graphics.DroppedFramesPercent),
                    Q(s.Graphics.Encoder), Q(s.Graphics.Codec), Q(s.Graphics.Mode), s.Graphics.HardwareEncoding?.ToString() ?? "Unavailable",
                    Q(s.Graphics.Resolution), Q(s.Gpu.Model), V(s.Gpu.Utilization), V(s.Gpu.VramUtilization),
                    V(s.Gpu.EncoderUtilization), V(s.Gpu.DecoderUtilization), Q(s.Gpu.DriverVersion),
                    V(s.System.CpuUtilization), V(s.System.RamUtilization), V(s.System.DiskActivity), Q(s.CollectionStatus)) + blast + host);
            }
            await writer.FlushAsync(cancellationToken);
        }, cancellationToken), cancellationToken);
    }
    private static async Task WriteAtomicAsync(string path, Func<Stream, Task> write, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous)) await write(stream);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
