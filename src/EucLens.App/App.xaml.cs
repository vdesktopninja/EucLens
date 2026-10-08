using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
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
            registrations.AddSingleton<ISessionRecorder, SessionRecorder>();
            registrations.AddSingleton<IDiagnosticRule, LatencyRule>();
            registrations.AddSingleton<IDiagnosticRule, FrameRateRule>();
            registrations.AddSingleton<IDiagnosticRule, GpuPressureRule>();
            registrations.AddSingleton<IDiagnosticRule, BandwidthRule>();
            registrations.AddSingleton<IDiagnosticEngine, DiagnosticEngine>();
            registrations.AddSingleton<IDesktopServices, DesktopServices>();
            registrations.AddSingleton<MainViewModel>();
            services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var vm = services.GetRequiredService<MainViewModel>();
            BindingErrorListener? bindingErrors = null;
            if (smoke)
            {
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
        var recorder = services!.GetRequiredService<ISessionRecorder>();
        var directory = Path.Combine(Path.GetTempPath(), "EucLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await recorder.ExportJsonAsync(Path.Combine(directory, "demo.json"));
            await recorder.ExportCsvAsync(Path.Combine(directory, "demo.csv"));
            if (new FileInfo(Path.Combine(directory, "demo.json")).Length == 0) throw new IOException("Empty JSON export.");
        }
        finally { Directory.Delete(directory, true); }
        if (errors.Errors.Count > 0) throw new InvalidOperationException("WPF binding errors: " + string.Join("\n", errors.Errors));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), "PASS: startup, sampling, all five views, themes, recording, JSON/CSV export, no WPF binding errors.");
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
            "Unsaved demo recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
