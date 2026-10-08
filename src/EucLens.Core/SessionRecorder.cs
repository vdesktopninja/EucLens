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
        return WriteAtomicAsync(path, async stream =>
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            await JsonSerializer.SerializeAsync(stream, copy, options, cancellationToken);
        }, cancellationToken);
    }
    public Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var copy = samples.ToArray();
        return WriteAtomicAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            await writer.WriteLineAsync("timestamp,is_demo,protocol,session_id,workload,demo_scenario,rtt_ms,network_latency_ms,available_mbps,consumed_mbps,packet_loss_percent,fps,dropped_frames_percent,encoder,codec,graphics_mode,hardware_encoding,resolution,gpu_model,gpu_percent,vram_percent,encoder_percent,decoder_percent,driver,cpu_percent,ram_percent,disk_mbps");
            foreach (var s in copy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string V(Metric m) => m.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "Unavailable";
                string Q(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
                await writer.WriteLineAsync(string.Join(",", s.Timestamp.ToString("O"), s.Session.IsDemo,
                    s.Session.Protocol, s.Session.SessionId, s.Workload, s.DemoScenario?.ToString() ?? "Unavailable", V(s.Connection.RoundTripTime),
                    V(s.Connection.NetworkLatency), V(s.Connection.AvailableBandwidth), V(s.Connection.BandwidthConsumption),
                    V(s.Connection.PacketLoss), V(s.Graphics.FramesPerSecond), V(s.Graphics.DroppedFramesPercent),
                    Q(s.Graphics.Encoder), Q(s.Graphics.Codec), Q(s.Graphics.Mode), s.Graphics.HardwareEncoding?.ToString() ?? "Unavailable",
                    Q(s.Graphics.Resolution), Q(s.Gpu.Model), V(s.Gpu.Utilization), V(s.Gpu.VramUtilization),
                    V(s.Gpu.EncoderUtilization), V(s.Gpu.DecoderUtilization), Q(s.Gpu.DriverVersion),
                    V(s.System.CpuUtilization), V(s.System.RamUtilization), V(s.System.DiskActivity)));
            }
            await writer.FlushAsync(cancellationToken);
        }, cancellationToken);
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
