using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EucLens.App;

public partial class App : Application
{
    private ServiceProvider? services;
    private readonly CancellationTokenSource cancellation = new();
    private Task? sampling;
    private bool closing;
    private bool smoke;
    private readonly List<string> unhandled = [];
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        smoke = e.Args.Contains("--smoke-test");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var registrations = new ServiceCollection();
            registrations.AddLogging(builder => builder.AddProvider(new LocalFileLoggerProvider()));
            registrations.AddSingleton<IDemoMetricsProvider, MockMetricsProvider>();
            registrations.AddSingleton<IBlastCounterReader, WindowsBlastCounterReader>();
            registrations.AddSingleton<IBlastSessionContext, WindowsBlastSessionContext>();
            registrations.AddSingleton<IMetricsProvider, OmnissaBlastProvider>();
            registrations.AddSingleton<ISessionRecorder, SessionRecorder>();
            registrations.AddSingleton<IDiagnosticRule, LatencyRule>();
            registrations.AddSingleton<IDiagnosticRule, FrameRateRule>();
            registrations.AddSingleton<IDiagnosticRule, GpuPressureRule>();
            registrations.AddSingleton<IDiagnosticRule, BandwidthRule>();
            registrations.AddSingleton<IDiagnosticEngine, DiagnosticEngine>();
            registrations.AddSingleton<IDesktopServices>(new DesktopServices { AutoConfirmNewRecording = smoke });
            registrations.AddSingleton<MainViewModel>();
            services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var vm = services.GetRequiredService<MainViewModel>();
            BindingErrorListener? bindingErrors = null;
            if (smoke)
            {
                DispatcherUnhandledException += (_, args) => unhandled.Add(args.Exception.ToString());
                TaskScheduler.UnobservedTaskException += (_, args) => unhandled.Add(args.Exception.ToString());
                bindingErrors = new BindingErrorListener();
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            }
            var window = new MainWindow { DataContext = vm };
            MainWindow = window;
            window.Closing += WindowClosing;
            window.Show();
            await vm.InitializeAsync(cancellation.Token);
            sampling = vm.RunAsync(cancellation.Token);
            if (smoke) await RunSmokeAsync(vm, bindingErrors!);
        }
        catch (Exception ex)
        {
            services?.GetService<ILogger<App>>()?.LogCritical(ex, "Application startup or smoke test failed.");
            if (smoke) await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), ex.ToString());
            else MessageBox.Show($"EUC Lens could not start. {ex.Message}", "EUC Lens", MessageBoxButton.OK, MessageBoxImage.Error);
            await StopAsync();
            closing = true;
            Shutdown(1);
        }
    }
    private async Task RunSmokeAsync(MainViewModel vm, BindingErrorListener errors)
    {
        var recorder = services!.GetRequiredService<ISessionRecorder>();
        var directory = Path.Combine(Path.GetTempPath(), "EucLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidence = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message + " Status: " + vm.Status); }
        static double?[] Points(ISeries[] series) => ((LineSeries<DateTimePoint>)series[0]).Values!.Select(p => p.Value).ToArray();
        try
        {
            // CI runners have no Horizon Agent: Real Session must keep running and report N/A, never demo values.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.RealSession);
            vm.StartRecordingCommand.Execute(null);
            await Task.Delay(2200);
            vm.StopRecordingCommand.Execute(null);
            Check(vm.Status.StartsWith("REAL SESSION", StringComparison.Ordinal) || vm.Status.StartsWith("Recording stopped", StringComparison.Ordinal), "Real Session status missing.");
            Check(vm.ModeBanner.StartsWith("REAL SESSION", StringComparison.Ordinal), "Real Session banner missing.");
            Check(vm.OverviewCards.Count > 0 && vm.OverviewCards.All(c => c.Value == "N/A"), "Real Session without Horizon Agent must show only N/A.");
            Check(vm.RecordingState.Contains("REAL SESSION", StringComparison.Ordinal) && vm.SampleCount >= 1, "Real Session recording failed.");
            var realRtt = Points(vm.RttSeries);
            Check(realRtt.Length >= 1 && realRtt.All(v => v is null), "Real RTT chart must contain gaps, not fabricated values.");
            Check(vm.DiagnosticCards.Count == 4 && vm.Health == nameof(HealthStatus.Unavailable), "Real Session diagnostics must be Unavailable.");
            await recorder.ExportJsonAsync(Path.Combine(directory, "real.json"));
            await recorder.ExportCsvAsync(Path.Combine(directory, "real.csv"));
            var realCsv = await File.ReadAllLinesAsync(Path.Combine(directory, "real.csv"));
            Check(realCsv[0].Contains("data_source", StringComparison.Ordinal) && realCsv[0].Contains("blast_rtt_status", StringComparison.Ordinal), "Real CSV header lacks Blast columns.");
            Check(realCsv.Skip(1).All(l => l.Split(',')[1] == "RealSession"), "Real CSV contains non-real rows.");
            using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "real.json"))))
                Check(json.RootElement.EnumerateArray().All(e => !e.GetProperty("Session").GetProperty("IsDemo").GetBoolean()
                    && e.GetProperty("BlastMetrics").GetProperty("rtt").GetProperty("Value").ValueKind == System.Text.Json.JsonValueKind.Null), "Real JSON contains demo or fabricated data.");
            evidence.Add($"Real Session: {realCsv.Length - 1} samples, all N/A; status: {vm.Status}");

            // Demo Mode: synthetic values, separate recording, live charts populated.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
            Check(vm.ModeBanner.StartsWith("DEMO DATA", StringComparison.Ordinal), "Demo banner missing.");
            vm.StartRecordingCommand.Execute(null);
            Check(recorder.IsRecording && recorder.SampleCount == 0, "New demo recording did not replace the real recording.");
            await Task.Delay(2200);
            Check(vm.OverviewCards.Any(c => c.Value != "N/A" && c.Value != "Unavailable"), "Demo Mode did not produce values.");
            Check(Points(vm.RttSeries).Any(v => v is not null) && Points(vm.FpsSeries).Any(v => v is not null), "Demo live charts are empty.");
            Check(Points(vm.RecordingSeries).Any(v => v is not null), "Recording chart is empty.");

            // Switching mode during a recording must stop it, so demo and real samples never mix.
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.RealSession);
            Check(!recorder.IsRecording && vm.Status.Contains("Recording stopped", StringComparison.Ordinal), "Mode switch did not stop the recording.");
            Check(Points(vm.RttSeries).Length == 0 && vm.OverviewCards.Count == 0, "Mode switch did not clear live data.");
            Check(recorder.Samples.All(s => s.Session.IsDemo), "Recording mixed real and demo samples.");
            await recorder.ExportCsvAsync(Path.Combine(directory, "demo.csv"));
            var demoCsv = await File.ReadAllLinesAsync(Path.Combine(directory, "demo.csv"));
            Check(demoCsv.Length > 1 && demoCsv.Skip(1).All(l => l.Split(',')[1] == "Demo"), "Demo CSV contains non-demo rows.");
            evidence.Add($"Demo Mode: {demoCsv.Length - 1} samples; Real→Demo→Real switching stopped recording and cleared live data.");
            await Task.Delay(1500);
            Check(vm.OverviewCards.All(c => c.Value == "N/A"), "Real Session after switching back must show only N/A.");
            vm.SelectedMode = vm.Modes.Single(m => m.Value == MonitoringMode.DemoMode);
        }
        finally { Directory.Delete(directory, true); }
        vm.StartRecordingCommand.Execute(null);
        await Task.Delay(2200);
        vm.StopRecordingCommand.Execute(null);
        if (vm.SampleCount < 1 || vm.DiagnosticCards.Count != 4) throw new InvalidOperationException("Sampling/diagnostics smoke check failed.");
        foreach (var page in vm.Pages)
        {
            vm.SelectedPage = page;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(350);
        }
        vm.IsDark = false;
        vm.SelectedPage = vm.Pages[0];
        await Task.Delay(500);
        vm.IsDark = true;
        await DesktopValidation.RunAsync(MainWindow, vm);
        directory = Path.Combine(Path.GetTempPath(), "EucLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await recorder.ExportJsonAsync(Path.Combine(directory, "demo.json"));
            await recorder.ExportCsvAsync(Path.Combine(directory, "demo.csv"));
            if (new FileInfo(Path.Combine(directory, "demo.json")).Length == 0) throw new IOException("Empty JSON export.");
        }
        finally { Directory.Delete(directory, true); }
        if (errors.Errors.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join("\n", errors.Errors));
        GC.Collect(); GC.WaitForPendingFinalizers();
        if (unhandled.Count > 0) throw new InvalidOperationException("Unhandled exceptions: " + string.Join("\n", unhandled));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), "PASS: startup, Real Session N/A without Horizon Agent (recording + JSON/CSV), Real↔Demo switching, demo sampling and live charts, all five views, themes, recording, JSON/CSV export, themed charts, navigation/resize layout matrix, WPF client captures, no WPF binding errors. Actual Windows DPI changes require manual acceptance.\n" + string.Join("\n", evidence));
        await StopAsync();
        closing = true;
        Shutdown(0);
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        var vm = services?.GetService<MainViewModel>();
        if (!smoke && vm?.HasUnexportedSamples == true && MessageBox.Show(MainWindow,
            "There are unexported recording samples. Exit and discard them? Choose No to return and export.",
            "Unsaved recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        closing = true;
        try { await StopAsync(); Shutdown(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Shutdown(1); }
    }
    private async Task StopAsync()
    {
        cancellation.Cancel();
        if (sampling is not null) await sampling;
        if (services is not null) { await services.DisposeAsync(); services = null; }
    }
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
