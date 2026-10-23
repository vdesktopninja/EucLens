using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Providers;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace EucLens.App;

/// <summary>Everything the background validation needs from the running application.</summary>
internal sealed record BackgroundSmokeContext(Application App, Window Window, MainViewModel Vm, MonitoringService Monitoring, TrayIconService Tray,
    ApplicationLifetimeService Lifetime, ISessionRecorder Recorder, string EvidenceDirectory);

/// <summary>
/// Windows-only acceptance of background operation, invoked by --smoke-test after the layout matrix: the tray icon and its state mapping,
/// minimize and close to tray, restore, a recording that runs while the window is hidden, the single-instance guard, the application icon,
/// and a measured comparison of visible and hidden operation. The 48-cycle navigation soak is separate and unchanged.
/// </summary>
internal static class BackgroundValidation
{
    private const int SoakSeconds = 40;
    private static readonly StringBuilder Report = new();

    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static void Log(BackgroundSmokeContext c, string line)
    {
        Report.AppendLine(line);
        SmokeLog.Write("background: " + line);
        File.WriteAllText(Path.Combine(c.EvidenceDirectory, "background-validation.txt"), Report.ToString());
    }

    private static Task Idle(BackgroundSmokeContext c) => c.App.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitFor(Func<bool> condition, string what, int milliseconds = 8000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new InvalidOperationException($"Timed out waiting for {what}."); await Task.Delay(50); }
    }

    private static string Hash(Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return Convert.ToHexString(SHA256.HashData(bytes))[..12];
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Drawing.Icon Resource(string key)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Tray/tray-{key}.ico", UriKind.Absolute)) ?? throw new InvalidOperationException($"Resource tray-{key}.ico missing.");
        using var stream = info.Stream;
        return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    public static async Task RunAsync(BackgroundSmokeContext c)
    {
        Report.Clear();
        var directory = c.EvidenceDirectory;
        SmokeLog.Write("background validation started");
        await c.Vm.Background.LoadAsync();
        c.Vm.SelectedMode = c.Vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
        if (c.Monitoring.Coordinator.State != MonitoringState.Running) c.Monitoring.StartMonitoring();
        if (c.Recorder.IsRecording) await c.Recorder.StopAsync();
        c.Vm.SelectedPage = c.Vm.Pages[0];
        await Task.Delay(1500);

        // Every section runs even when an earlier one fails, so one problem cannot hide the evidence of the others. Each failure is
        // written to background-validation.txt as it happens; the run fails at the end if there was any.
        var failures = new List<string>();
        await Section(c, failures, "icons", () => { CheckIcons(c); return Task.CompletedTask; });
        await Section(c, failures, "tray states", () => CheckTrayStates(c));
        await Section(c, failures, "tray artifacts", () => CaptureTrayArtifacts(c));
        await Section(c, failures, "minimize, restore and hidden recording", () => CheckMinimizeRestoreAndHiddenRecording(c));
        await Section(c, failures, "close to tray", () => CheckCloseToTray(c));
        await Section(c, failures, "single instance", () => CheckSingleInstance(c));
        await Section(c, failures, "visible versus hidden soak", () => Soak(c));
        if (failures.Count > 0) throw new InvalidOperationException($"Background validation failed in {failures.Count} section(s): {string.Join("; ", failures)}");
        Log(c, "PASS: background validation complete");
    }

    private static async Task Section(BackgroundSmokeContext c, List<string> failures, string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex)
        {
            failures.Add(name);
            Log(c, $"FAIL: {name}: {ex.GetType().Name}: {ex.Message}");
        }
        await ResetAsync(c);
    }

    /// <summary>Returns the application to a known state (window shown, monitoring running, nothing recording) so one section cannot corrupt the next.</summary>
    private static async Task ResetAsync(BackgroundSmokeContext c)
    {
        try
        {
            if (c.Recorder.IsRecording) await c.Recorder.StopAsync();
            if (c.Monitoring.Coordinator.State != MonitoringState.Running) c.Monitoring.StartMonitoring();
            c.Lifetime.Restore();
            await Idle(c);
        }
        catch (Exception ex) { SmokeLog.Write("background reset: " + ex.Message); }
    }

    // ---------- icons ----------

    private static void CheckIcons(BackgroundSmokeContext c)
    {
        Require(c.Tray.NotifyIcon.Visible && c.Tray.CurrentIcon is not null, "The tray icon was not created.");
        var small = WinForms.SystemInformation.SmallIconSize;
        Require(c.Tray.CurrentIcon!.Width == small.Width, $"Tray icon is {c.Tray.CurrentIcon.Width} px, Windows asks for {small.Width}.");
        var hashes = TrayStateResolver.IconKeys.ToDictionary(k => k, k => { using var icon = Resource(k); return Hash(icon); });
        Require(hashes.Values.Distinct().Count() == hashes.Count, "Two tray states render the same icon: " + string.Join(", ", hashes.Select(h => $"{h.Key}={h.Value}")));
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        using var associated = Drawing.Icon.ExtractAssociatedIcon(exe) ?? throw new InvalidOperationException("The executable has no icon.");
        using var bitmap = associated.ToBitmap();
        var blue = 0; var white = 0; var cyan = 0;
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.A == 255 && p.B > 130 && p.R < 60) blue++;
            if (p.A == 255 && p.R > 235 && p.G > 235 && p.B > 235) white++;
            if (p.A == 255 && p.R < 90 && p.G > 170 && p.B > 210) cyan++;
        }
        Require(blue > 100 && white > 20 && cyan > 5, $"The executable icon is not the EUC Lens icon (blue {blue}, white {white}, cyan {cyan} pixels).");
        bitmap.Save(Path.Combine(c.EvidenceDirectory, "exe-icon.png"), Drawing.Imaging.ImageFormat.Png);
        var frames = ((BitmapFrame)c.Window.Icon).Decoder.Frames.Count;
        Require(frames == 8, $"The window icon has {frames} sizes, expected 8.");
        Log(c, $"PASS: executable icon is the EUC Lens icon ({associated.Width}×{associated.Height} extracted; blue {blue}, white {white}, cyan {cyan} px); window icon has {frames} sizes; tray icon {c.Tray.CurrentIcon.Width}×{c.Tray.CurrentIcon.Height} for Windows small-icon size {small.Width}×{small.Height}; six tray states render six different icons ({string.Join(", ", hashes.Select(h => $"{h.Key}={h.Value}"))})");
    }

    // ---------- tray state mapping, driven through the real services ----------

    private static DiagnosticFinding Finding(Severity severity) => new("connection.rtt.sustained", DiagnosticDomain.Connection, severity, "Elevated Blast round-trip time", "d",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 60, 58, 60, [], "rule", [], [], "limits");

    private static async Task CheckTrayStates(BackgroundSmokeContext c)
    {
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync(); // the provider contract: initialize before collecting, dispose when done (await using)
        var sample = await provider.CollectAsync();
        var expected = TrayStateResolver.IconKeys.ToDictionary(k => k, k => { using var icon = Resource(k); return Hash(icon); });
        var steps = new List<string>();

        // Each step changes the real state and checks the icon in the same synchronous turn, so the sampling loop cannot interleave.
        void Check(string label, string key, Action change)
        {
            change();
            c.Tray.Refresh();
            Require(c.Tray.Appearance!.IconKey == key, $"{label}: tray state is '{c.Tray.Appearance.IconKey}', expected '{key}' (status {c.Monitoring.Status}).");
            Require(Hash(c.Tray.CurrentIcon!) == expected[key], $"{label}: the tray shows another icon than tray-{key}.ico.");
            c.Tray.UpdateMenu();
            steps.Add($"{label} → {key} (\"{c.Tray.Appearance.Tooltip}\")");
        }
        void Problems(bool on) => c.Monitoring.OnSample(sample, new DiagnosticReport(on ? HealthStatus.Warning : HealthStatus.Healthy, [], Findings: on ? [Finding(Severity.Warning)] : []));
        string Menu(string item) => c.Tray.MenuState().First(m => m.Text.Contains(item, StringComparison.Ordinal)).Text;
        bool Enabled(string item) => c.Tray.MenuState().First(m => m.Text.Contains(item, StringComparison.Ordinal)).Enabled;

        Check("monitoring running", "monitoring", () => Problems(false));
        Require(Menu("Monitoring") == "Pause Monitoring" && Menu("Recording") == "Start Recording" && Enabled("Recording"), "Menu text does not match 'running, not recording'.");
        Check("recording started", "recording", () => c.Vm.StartRecordingCommand.Execute(null));
        Require(c.Recorder.IsRecording, "The recording did not start.");
        Require(Menu("Recording") == "Stop Recording", "Menu does not offer 'Stop Recording' while recording.");
        Check("recording + active warning", "recording-warning", () => Problems(true));
        Check("warning cleared", "recording", () => Problems(false));
        Check("monitoring paused", "paused", () => c.Monitoring.PauseMonitoring());
        Require(Menu("Monitoring") == "Resume Monitoring" && Menu("Recording") == "Stop Recording", "Menu does not offer 'Resume Monitoring' while paused.");
        Check("monitoring resumed", "recording", () => c.Monitoring.ResumeMonitoring());
        Check("recording stopped", "monitoring", () => c.Vm.StopRecordingCommand.Execute(null));
        Require(!c.Recorder.IsRecording && Menu("Recording") == "Start Recording", "After stopping, the icon or menu still suggests recording.");
        Check("active warning, not recording", "warning", () => Problems(true));
        Check("warning cleared", "monitoring", () => Problems(false));
        Check("monitoring stopped", "idle", () => c.Monitoring.StopMonitoring());
        Require(Menu("Monitoring") == "Start Monitoring" && !Enabled("Recording"), "Menu does not offer 'Start Monitoring' / disabled recording while stopped.");
        Check("monitoring started", "monitoring", () => c.Monitoring.StartMonitoring());
        await c.Recorder.StopAsync();
        Log(c, "PASS: tray state follows the real services: " + string.Join("; ", steps));
    }

    private static async Task CaptureTrayArtifacts(BackgroundSmokeContext c)
    {
        try
        {
            // Runtime icons of all six states, enlarged, from the same resources the tray uses.
            using var sheet = new Drawing.Bitmap(6 * 140 + 20, 170);
            using (var g = Drawing.Graphics.FromImage(sheet))
            {
                g.Clear(Drawing.Color.FromArgb(243, 243, 243));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                var x = 20;
                foreach (var key in TrayStateResolver.IconKeys)
                {
                    using var icon = Resource(key);
                    using var bitmap = icon.ToBitmap();
                    g.DrawImage(bitmap, new Drawing.Rectangle(x, 10, 128, 128));
                    g.DrawString(key, new Drawing.Font("Segoe UI", 9), Drawing.Brushes.Black, x, 144);
                    x += 140;
                }
            }
            sheet.Save(Path.Combine(c.EvidenceDirectory, "tray-icons-runtime.png"), Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or InvalidOperationException or IOException)
        { Log(c, "NOTE: tray icon sheet not captured: " + ex.Message); }
        await CaptureScreenAreas(c);
    }

    private static async Task CaptureScreenAreas(BackgroundSmokeContext c)
    {
        // The notification area and its menu belong to the Windows shell; capturing them depends on the runner's desktop.
        try
        {
            c.Monitoring.StartMonitoring();
            c.Vm.StartRecordingCommand.Execute(null);
            c.Tray.Refresh(); c.Tray.UpdateMenu();
            var point = new Drawing.Point(120, 120);
            c.Tray.Menu.Show(point);
            await Task.Delay(700);
            var bounds = c.Tray.Menu.Bounds;
            using (var shot = new Drawing.Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)))
            {
                using var g = Drawing.Graphics.FromImage(shot);
                g.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
                shot.Save(Path.Combine(c.EvidenceDirectory, "tray-context-menu-recording.png"), Drawing.Imaging.ImageFormat.Png);
            }
            c.Tray.Menu.Close();
            await c.Recorder.StopAsync();
            var taskbar = NativeTaskbar();
            if (taskbar is { Width: > 0, Height: > 0 } area)
            {
                using var shot = new Drawing.Bitmap(area.Width, area.Height);
                using var g = Drawing.Graphics.FromImage(shot);
                g.CopyFromScreen(area.Location, Drawing.Point.Empty, area.Size);
                shot.Save(Path.Combine(c.EvidenceDirectory, "taskbar.png"), Drawing.Imaging.ImageFormat.Png);
                Log(c, $"NOTE: tray context menu and taskbar captured ({bounds.Width}×{bounds.Height} and {area.Width}×{area.Height} px); their appearance depends on the runner's shell");
            }
            else Log(c, "NOTE: the tray context menu was captured; the taskbar was not found on this runner");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { Log(c, "NOTE: shell captures skipped on this runner: " + ex.Message); }
        finally { c.Tray.Menu.Close(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static Drawing.Rectangle? NativeTaskbar()
    {
        var handle = FindWindow("Shell_TrayWnd", null);
        return handle != IntPtr.Zero && GetWindowRect(handle, out var r) ? new Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    // ---------- minimize to tray, recording while hidden, restore ----------

    private static async Task SaveSettings(BackgroundSmokeContext c, Action<BackgroundViewModel> change)
    {
        change(c.Vm.Background);
        await c.Vm.Background.SaveCommand.ExecuteAsync();
        Require(!c.Vm.Background.SettingsInvalid, "Saving the background settings failed: " + c.Vm.Background.SettingsMessage);
    }

    private static async Task CheckMinimizeRestoreAndHiddenRecording(BackgroundSmokeContext c)
    {
        await SaveSettings(c, b => { b.MinimizeToTray = true; b.CloseToTray = false; });
        Require(c.Lifetime.Settings.MinimizeToTray, "The setting did not take effect.");
        c.Vm.StartRecordingCommand.Execute(null);
        Require(c.Recorder.IsRecording && c.Recorder.IsPersistent, "The persistent recording did not start.");
        await WaitFor(() => c.Recorder.SampleCount >= 3, "the first recorded samples");
        var lastShown = c.Vm.RttChart.Points.Count > 0 ? c.Vm.RttChart.Points[^1].At : default;
        var samples0 = c.Recorder.SampleCount; var ticks0 = c.Monitoring.Coordinator.Ticks;
        var hints0 = c.Lifetime.TrayHints;

        c.Window.WindowState = WindowState.Minimized;
        await Idle(c);
        Require(!c.Window.IsVisible && c.Lifetime.IsHiddenToTray, "Minimize to tray left the window visible.");
        Require(!c.Vm.IsPresenting, "The presentation was not suspended while hidden.");
        Require(c.Tray.NotifyIcon.Visible, "The tray icon disappeared when the window was hidden.");
        Require(c.Lifetime.TrayHints == hints0 + 1 || c.Lifetime.Settings.TrayHintShown, "The one-time background explanation was not shown.");
        var hiddenAt = Stopwatch.StartNew();
        var shownWhileHidden = c.Vm.RttChart.Points.Count > 0 ? c.Vm.RttChart.Points[^1].At : default;
        await Task.Delay(6000);
        var gained = c.Recorder.SampleCount - samples0; var tickGain = c.Monitoring.Coordinator.Ticks - ticks0;
        Require(gained >= 4, $"Only {gained} samples were recorded in {hiddenAt.Elapsed.TotalSeconds:0.0} s while the window was hidden.");
        Require(tickGain <= hiddenAt.Elapsed.TotalSeconds + 3, $"{tickGain} collection ticks in {hiddenAt.Elapsed.TotalSeconds:0.0} s: a second sampler is running.");
        Require(c.Recorder.IsRecording && c.Tray.Appearance!.State is TrayState.Recording, "Recording or the recording tray state was lost while hidden.");
        Require((c.Vm.RttChart.Points.Count > 0 ? c.Vm.RttChart.Points[^1].At : default) == shownWhileHidden, "Charts were fed while the window was hidden.");

        var restore = Stopwatch.StartNew();
        c.Lifetime.Restore();
        await Idle(c);
        restore.Stop();
        Require(c.Window.IsVisible && c.Window.WindowState == WindowState.Normal && c.Vm.IsPresenting, "Restore did not show the window with live presentation.");
        var latest = c.Recorder.Samples[^1].Timestamp.LocalDateTime;
        Require(c.Vm.RttChart.Points.Count > 0 && (latest - c.Vm.RttChart.Points[^1].At).Duration() < TimeSpan.FromSeconds(2.5), "The restored window does not show the current state.");
        Log(c, $"PASS: minimize to tray hid the window; {gained} samples were recorded in {hiddenAt.Elapsed.TotalSeconds:0.0} s while hidden ({tickGain} ticks, one sampler); restore showed the current state in {restore.ElapsedMilliseconds} ms (to the first idle frame)");

        // Repeated hide and restore: no second sampler, no duplicate or out-of-order samples, charts still render.
        var before = c.Recorder.SampleCount; var started = Stopwatch.StartNew(); var ticksBefore = c.Monitoring.Coordinator.Ticks;
        for (var i = 0; i < 10; i++)
        {
            c.Window.WindowState = WindowState.Minimized; await Idle(c); Require(!c.Window.IsVisible, "A minimize in the repeated cycle did not hide the window.");
            await Task.Delay(300);
            c.Lifetime.Restore(); await Idle(c); Require(c.Window.IsVisible, "A restore in the repeated cycle did not show the window.");
            await Task.Delay(300);
        }
        var stamps = c.Recorder.Samples.Select(s => s.Timestamp).ToArray();
        Require(stamps.Distinct().Count() == stamps.Length && stamps.SequenceEqual(stamps.OrderBy(t => t)), "Samples are duplicated or out of order after repeated hide and restore.");
        var cycleTicks = c.Monitoring.Coordinator.Ticks - ticksBefore;
        Require(cycleTicks <= started.Elapsed.TotalSeconds + 3, $"{cycleTicks} ticks in {started.Elapsed.TotalSeconds:0.0} s after repeated hide and restore: a second sampler exists.");
        var second = false;
        try { await c.Vm.RunAsync(CancellationToken.None); } catch (InvalidOperationException) { second = true; }
        Require(second, "A second monitoring loop could be started.");
        c.Vm.SelectedPage = c.Vm.Pages[0];
        await Idle(c);
        await DesktopValidation.WaitForChartMeasureAsync(c.Window);
        DesktopValidation.CheckCharts(c.Window, c.Vm.SelectedPage);
        Log(c, $"PASS: 10 minimize/restore cycles: {c.Recorder.SampleCount - before} new samples in {started.Elapsed.TotalSeconds:0.0} s, {cycleTicks} ticks, strictly ordered, one loop, charts render");

        // The recording that ran while hidden is complete on disk.
        await c.Recorder.StopAsync();
        var folder = c.Vm.Background.Current.EffectiveDirectory;
        var summary = RecordingCatalog.Scan(new PhysicalRecordingStorage(), folder).FirstOrDefault() ?? throw new InvalidOperationException("The recording is not listed.");
        Require(summary.IsComplete && summary.Samples == c.Recorder.SampleCount && summary.LostSamples == 0, $"The recording is incomplete: {summary}");
        var times = RecordingCatalog.ReadSamples(new PhysicalRecordingStorage(), summary).Select(s => s.Timestamp).ToArray();
        var longest = times.Zip(times.Skip(1), (a, b) => b - a).Max();
        Require(times.Length == summary.Samples && longest < TimeSpan.FromSeconds(4), $"The recording has a gap of {longest.TotalSeconds:0.0} s although monitoring never paused.");
        var csv = Path.Combine(c.EvidenceDirectory, "background-recording.csv");
        await RecordingExport.ExportCsvAsync(csv, () => RecordingCatalog.ReadSamples(new PhysicalRecordingStorage(), summary));
        Require(File.ReadAllLines(csv).Length == summary.Samples + 1, "The CSV export of the saved recording has the wrong row count.");
        Log(c, $"PASS: the saved recording holds all {summary.Samples} samples without a gap longer than {longest.TotalSeconds:0.0} s ({summary.Parts.Count} part(s), {BackgroundViewModel.FormatBytes(summary.Bytes)}, {summary.LostSamples} lost) and exports as CSV");
    }

    // ---------- close to tray ----------

    private static async Task CheckCloseToTray(BackgroundSmokeContext c)
    {
        await SaveSettings(c, b => { b.MinimizeToTray = false; b.CloseToTray = true; });
        c.Vm.StartRecordingCommand.Execute(null);
        await WaitFor(() => c.Recorder.SampleCount >= 2, "recorded samples before closing");
        var before = c.Recorder.SampleCount;
        c.Window.Close();
        await Idle(c);
        Require(!c.Window.IsVisible && !c.Lifetime.IsExiting, "The close button did not hide the window (or started exiting).");
        Require(c.App.MainWindow == c.Window && c.Tray.NotifyIcon.Visible, "The window was destroyed or the tray icon vanished.");
        await Task.Delay(3500);
        Require(c.Recorder.SampleCount >= before + 2 && c.Recorder.IsRecording, "Recording stopped after close to tray.");
        c.Lifetime.Restore(); await Idle(c);
        Require(c.Window.IsVisible, "The window could not be restored after close to tray.");
        await c.Recorder.StopAsync();
        await SaveSettings(c, b => { b.CloseToTray = false; });
        Log(c, "PASS: close to tray hid the window, monitoring and recording continued, the window was restored; Exit (tested at the end of this run) is the only way out");
    }

    // ---------- single instance ----------

    private static async Task CheckSingleInstance(BackgroundSmokeContext c)
    {
        var name = Environment.GetEnvironmentVariable(App.SmokeInstanceVariable) ?? throw new InvalidOperationException("The single-instance name is unknown.");
        await SaveSettings(c, b => b.MinimizeToTray = true);
        c.Window.WindowState = WindowState.Minimized; await Idle(c);
        Require(!c.Window.IsVisible, "The window was not hidden before the second start.");
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--probe-single-instance", name]) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The second process did not start.");
        Require(child.WaitForExit(20000), "The second process did not end.");
        Require(child.ExitCode == 42, $"The second start returned {child.ExitCode} (42 = refused and the running instance was signalled).");
        await WaitFor(() => c.Window.IsVisible, "the running instance to show itself after the second start", 4000);
        await SaveSettings(c, b => b.MinimizeToTray = false);
        Log(c, "PASS: a second start was refused and made the running instance show its window (guard scoped to this user and Windows session)");
    }

    // ---------- measured comparison: visible vs hidden ----------

    private sealed record Measure(string Mode, double CpuPercentOfMachine, long WorkingSetStartMiB, long WorkingSetEndMiB, double HeapStartMiB, double HeapEndMiB, long Samples, long Lost, long Bytes,
        double LatencyMedianMs, double LatencyMaxMs, long Ticks);

    private static async Task<Measure> MeasureWindow(BackgroundSmokeContext c, string mode)
    {
        const double MiB = 1024 * 1024;
        var process = Process.GetCurrentProcess();
        var heapStart = GC.GetTotalMemory(true); process.Refresh();
        var workStart = process.WorkingSet64; var cpuStart = process.TotalProcessorTime;
        var samples0 = c.Recorder.SampleCount; var ticks0 = c.Monitoring.Coordinator.Ticks;
        var clock = Stopwatch.StartNew();
        var latencies = new List<double>();
        while (clock.Elapsed < TimeSpan.FromSeconds(SoakSeconds))
        {
            var t = Stopwatch.StartNew();
            await c.App.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            latencies.Add(t.Elapsed.TotalMilliseconds);
            await Task.Delay(250);
        }
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        var status = c.Recorder.SinkStatus;
        var heapEnd = GC.GetTotalMemory(true); process.Refresh();
        latencies.Sort();
        return new(mode, cpu, (long)(workStart / MiB), (long)(process.WorkingSet64 / MiB), heapStart / MiB, heapEnd / MiB, c.Recorder.SampleCount - samples0, status?.SamplesLost ?? 0,
            status?.BytesWritten ?? 0, latencies[latencies.Count / 2], latencies[^1], c.Monitoring.Coordinator.Ticks - ticks0);
    }

    private static async Task Soak(BackgroundSmokeContext c)
    {
        // Real counters on a Demo-free runner would be the honest background load, but demo data drives the engine and the writer the same way.
        c.Vm.SelectedPage = c.Vm.Pages[0];
        c.Lifetime.Restore(); await Idle(c);
        c.Vm.StartRecordingCommand.Execute(null);
        await Task.Delay(2000);
        var visible = await MeasureWindow(c, "visible (Dashboard)");
        await SaveSettings(c, b => b.MinimizeToTray = true);
        c.Window.WindowState = WindowState.Minimized; await Idle(c);
        Require(!c.Window.IsVisible, "The window was not hidden for the hidden measurement.");
        var hidden = await MeasureWindow(c, "hidden in the tray");
        c.Lifetime.Restore(); await Idle(c);
        await SaveSettings(c, b => b.MinimizeToTray = false);
        await c.Recorder.StopAsync();

        string Line(Measure m) => $"{m.Mode}: CPU {m.CpuPercentOfMachine:0.00} % of the machine ({Environment.ProcessorCount} logical processors), working set {m.WorkingSetStartMiB} → {m.WorkingSetEndMiB} MiB, " +
            $"managed heap {m.HeapStartMiB:0.0} → {m.HeapEndMiB:0.0} MiB, {m.Samples} samples ({m.Ticks} ticks) in {SoakSeconds} s, recording bytes {BackgroundViewModel.FormatBytes(m.Bytes)}, samples lost {m.Lost}, dispatcher latency median {m.LatencyMedianMs:0.0} ms max {m.LatencyMaxMs:0.0} ms";
        var text = new StringBuilder()
            .AppendLine($"Background monitoring soak: Demo data, recording to disk, {SoakSeconds} s per mode, measured in-process (managed heap after a full GC at both ends).")
            .AppendLine(Line(visible)).AppendLine(Line(hidden))
            .AppendLine($"Hidden vs visible: CPU {hidden.CpuPercentOfMachine - visible.CpuPercentOfMachine:+0.00;-0.00} percentage points of the machine; working set {hidden.WorkingSetEndMiB - visible.WorkingSetEndMiB:+0;-0} MiB.")
            .AppendLine("This is a short measured comparison on a shared CI runner, not an eight-hour soak; eight-hour behaviour is covered by the accelerated recording simulation in the unit tests and needs manual confirmation on a real VDI.");
        File.WriteAllText(Path.Combine(c.EvidenceDirectory, "background-soak-result.txt"), text.ToString());
        Log(c, "MEASURED: " + Line(visible));
        Log(c, "MEASURED: " + Line(hidden));
        Require(hidden.Lost == 0 && visible.Lost == 0, "Samples were lost during the soak although the disk was healthy.");
        Require(hidden.Samples >= SoakSeconds - 6 && hidden.Ticks <= SoakSeconds + 4, $"Hidden sampling rate is wrong: {hidden.Samples} samples, {hidden.Ticks} ticks in {SoakSeconds} s.");
        Require(hidden.HeapEndMiB - hidden.HeapStartMiB <= 8, $"The managed heap grew {hidden.HeapEndMiB - hidden.HeapStartMiB:0.0} MiB in {SoakSeconds} s while hidden.");
        Require(hidden.CpuPercentOfMachine <= visible.CpuPercentOfMachine + 3 && hidden.CpuPercentOfMachine < 25, $"Hidden operation costs {hidden.CpuPercentOfMachine:0.0} % CPU (visible {visible.CpuPercentOfMachine:0.0} %).");
        Require(hidden.LatencyMaxMs < 2000, $"The dispatcher stalled for {hidden.LatencyMaxMs:0} ms while hidden.");
    }
}
