using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EucLens.Core;

/// <summary>
/// The JSON and CSV recording exports (schema 0.4, columns only ever appended). The samples come from memory or, for persisted
/// recordings, are streamed from disk, so an export never needs the whole recording in RAM.
/// </summary>
public static class RecordingExport
{
    public static Task ExportJsonAsync(string path, Func<IEnumerable<SessionSnapshot>> samples, CancellationToken cancellationToken = default) =>
        Task.Run(() => WriteAtomicAsync(path, async stream =>
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            await JsonSerializer.SerializeAsync(stream, samples(), options, cancellationToken);
        }, cancellationToken), cancellationToken);

    /// <param name="samples">Called twice: the header is the union of all metric columns, so the data is read once to find them and once to write.</param>
    public static Task ExportCsvAsync(string path, Func<IEnumerable<SessionSnapshot>> samples, CancellationToken cancellationToken = default) =>
        Task.Run(() => WriteAtomicAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            var blastSet = new List<string>(); var blastSeen = new HashSet<string>(StringComparer.Ordinal);
            var hostSet = new List<string>(); var hostSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in samples())
            {
                foreach (var key in s.BlastMetrics?.Keys ?? Enumerable.Empty<string>()) if (blastSeen.Add(key)) blastSet.Add(key);
                foreach (var entry in s.HostMetrics ?? []) if (hostSeen.Add(entry.Id)) hostSet.Add(entry.Id);
            }
            var blastKeys = blastSet.ToArray();
            var hostKeys = hostSet.ToArray();
            // Real providers add counter-level metrics; columns are the union in first-seen order.
            var blastHeader = string.Concat(blastKeys.Select(k => $",blast_{k},blast_{k}_unit,blast_{k}_status,blast_{k}_raw,blast_{k}_source"));
            // Schema 0.3 appends columns only, so existing column positions are unchanged.
            var hostHeader = ",protocol_detection" + string.Concat(hostKeys.Select(k => $",host_{k},host_{k}_unit,host_{k}_status,host_{k}_raw,host_{k}_scope,host_{k}_source"));
            await writer.WriteLineAsync("timestamp,data_source,is_demo,protocol,session_id,workload,demo_scenario,rtt_ms,network_latency_ms,available_mbps,consumed_mbps,packet_loss_percent,fps,dropped_frames_percent,encoder,codec,graphics_mode,hardware_encoding,resolution,gpu_model,gpu_percent,vram_percent,encoder_percent,decoder_percent,driver,cpu_percent,ram_percent,disk_mbps,collection_status" + blastHeader + hostHeader + DiagnosticsHeader);
            foreach (var s in samples())
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
                    s.Session.Protocol, s.Session.SessionId, s.Workload?.ToString() ?? "Unavailable", s.DemoScenario?.ToString() ?? "Unavailable", V(s.Connection.RoundTripTime),
                    V(s.Connection.NetworkLatency), V(s.Connection.AvailableBandwidth), V(s.Connection.BandwidthConsumption),
                    V(s.Connection.PacketLoss), V(s.Graphics.FramesPerSecond), V(s.Graphics.DroppedFramesPercent),
                    Q(s.Graphics.Encoder), Q(s.Graphics.Codec), Q(s.Graphics.Mode), s.Graphics.HardwareEncoding?.ToString() ?? "Unavailable",
                    Q(s.Graphics.Resolution), Q(s.Gpu.Model), V(s.Gpu.Utilization), V(s.Gpu.VramUtilization),
                    V(s.Gpu.EncoderUtilization), V(s.Gpu.DecoderUtilization), Q(s.Gpu.DriverVersion),
                    V(s.System.CpuUtilization), V(s.System.RamUtilization), V(s.System.DiskActivity), Q(s.CollectionStatus)) + blast + host + Diagnostics(s.Diagnostics, Q));
            }
            await writer.FlushAsync(cancellationToken);
        }, cancellationToken), cancellationToken);

    /// <summary>Schema 0.4: diagnostic state per sample, appended after all earlier columns.</summary>
    private const string DiagnosticsHeader = ",diag_connection,diag_graphics,diag_guest,diag_coverage,diag_active_findings,diag_events";
    private static string Diagnostics(DiagnosticSnapshot? d, Func<string?, string> q)
    {
        if (d is null) return ",,,,,,";
        string State(DiagnosticDomain domain) => d.Assessments.FirstOrDefault(a => a.Domain == domain)?.StateLabel ?? "";
        var coverage = string.Join("; ", d.Assessments.Select(a => $"{a.Domain} {a.Coverage}"));
        var events = string.Join("; ", d.Events.Select(e => $"{e.Transition} {e.Finding.RuleId} {e.Finding.Severity}"));
        return string.Join(",", "", q(State(DiagnosticDomain.Connection)), q(State(DiagnosticDomain.Graphics)), q(State(DiagnosticDomain.Guest)),
            q(coverage), q(string.Join("; ", d.ActiveFindings)), q(events));
    }
    internal static async Task WriteAtomicAsync(string path, Func<Stream, Task> write, CancellationToken token)
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
