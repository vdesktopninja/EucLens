using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>Compiles production VM/contracts without WPF; no chart rendering or native Windows execution.</summary>
public sealed class ViewModelRecordingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportFromPreviousRecordingDoesNotMarkNewRecordingSaved(bool json)
    {
        await using var provider = new MockMetricsProvider();
        var recorder = new DeferredRecorder();
        var vm = Create(provider, recorder);
        await vm.InitializeAsync(default);
        var sample = await provider.CollectAsync();
        vm.StartRecordingCommand.Execute(null);
        for (var i = 0; i < 100; i++) recorder.Add(sample);
        vm.StopRecordingCommand.Execute(null);
        var command = json ? vm.ExportJsonCommand : vm.ExportCsvCommand;
        var export = command.ExecuteAsync();
        Assert.False(command.CanExecute(null));
        vm.StartRecordingCommand.Execute(null);
        recorder.Add(sample);
        vm.StopRecordingCommand.Execute(null);
        Assert.True(vm.HasUnexportedSamples);
        recorder.Exports[0].SetResult();
        await export;
        Assert.True(vm.HasUnexportedSamples);
        Assert.Contains("previous", vm.Status);
        Assert.Equal(1, vm.SampleCount);
    }
    [Fact]
    public async Task ExportWhileRecordingOnlySavesCapturedSamples()
    {
        await using var provider = new MockMetricsProvider();
        var recorder = new DeferredRecorder();
        var vm = Create(provider, recorder);
        await vm.InitializeAsync(default);
        var sample = await provider.CollectAsync();
        vm.StartRecordingCommand.Execute(null);
        recorder.Add(sample);
        var export = vm.ExportJsonCommand.ExecuteAsync();
        recorder.Add(sample);
        recorder.Exports[0].SetResult();
        await export;
        Assert.True(vm.HasUnexportedSamples);
        vm.StopRecordingCommand.Execute(null);
        var fullExport = vm.ExportJsonCommand.ExecuteAsync();
        recorder.Exports[1].SetResult();
        await fullExport;
        Assert.False(vm.HasUnexportedSamples);
    }
    [Fact]
    public async Task OlderExportCompletingLaterDoesNotRegressSavedCount()
    {
        await using var provider = new MockMetricsProvider();
        var recorder = new DeferredRecorder();
        var vm = Create(provider, recorder);
        await vm.InitializeAsync(default);
        var sample = await provider.CollectAsync();
        vm.StartRecordingCommand.Execute(null);
        recorder.Add(sample);
        var older = vm.ExportJsonCommand.ExecuteAsync();
        recorder.Add(sample);
        var newer = vm.ExportCsvCommand.ExecuteAsync();
        recorder.Exports[1].SetResult(); await newer;
        Assert.False(vm.HasUnexportedSamples);
        recorder.Exports[0].SetResult(); await older;
        Assert.False(vm.HasUnexportedSamples);
    }
    [Fact]
    public async Task FailedExportKeepsWarningAndReenablesCommand()
    {
        await using var provider = new MockMetricsProvider();
        var recorder = new DeferredRecorder();
        var vm = Create(provider, recorder);
        await vm.InitializeAsync(default);
        vm.StartRecordingCommand.Execute(null); recorder.Add(await provider.CollectAsync()); vm.StopRecordingCommand.Execute(null);
        var export = vm.ExportJsonCommand.ExecuteAsync();
        recorder.Exports[0].SetException(new IOException("Destination unavailable"));
        await export;
        Assert.True(vm.HasUnexportedSamples);
        Assert.True(vm.ExportJsonCommand.CanExecute(null));
        Assert.Contains("Export failed", vm.Status);
    }
    [Fact]
    public async Task OverviewUsesCurrentBandwidthAndSampleSessionIdentity()
    {
        await using var provider = new MockMetricsProvider();
        var recorder = new SessionRecorder();
        var vm = Create(provider, recorder);
        await vm.InitializeAsync(default);
        provider.Configure(RemoteProtocol.OmnissaBlast, DemoScenario.Healthy, WorkloadContext.InteractiveGraphics);
        vm.UseSyntheticSource(provider);
        vm.StartRecordingCommand.Execute(null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.UpdatedAt)) cancellation.Cancel();
        };
        await vm.RunAsync(cancellation.Token);
        var sample = Assert.Single(recorder.Samples);
        Assert.Equal(new[] { "RTT", "FPS", "BANDWIDTH", "GPU", "CPU" }, vm.OverviewCards.Select(card => card.Label));
        Assert.Equal(sample.Connection.BandwidthConsumption.Display, vm.OverviewCards[2].Value);
        Assert.Contains("Mbps", vm.OverviewCards[2].Value);
        Assert.Contains("Omnissa Blast", vm.SessionIdentity);
        Assert.Contains(sample.Session.Transport, vm.SessionIdentity);
        Assert.Contains($"Session {sample.Session.SessionId}", vm.SessionIdentity);
        Assert.Equal(sample.Connection.RoundTripTime.Display, vm.NetworkReadout);
        Assert.Equal(sample.Graphics.FramesPerSecond.Display, vm.GraphicsReadout);
    }
    private static MainViewModel Create(IMetricsProvider provider, ISessionRecorder recorder) =>
        new MainViewModel(provider, new DiagnosticEngine([]), recorder, new FakeDesktop(), NullLogger<MainViewModel>.Instance).WithSynthetic(provider);
    private sealed class FakeDesktop : IDesktopServices
    {
        public string? SelectExportPath(bool json) => "unused-by-fake";
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
    }
    private sealed class DeferredRecorder : ISessionRecorder
    {
        private readonly SessionRecorder inner = new();
        public List<TaskCompletionSource> Exports { get; } = [];
        public bool IsRecording => inner.IsRecording;
        public int SampleCount => inner.SampleCount;
        public TimeSpan Duration => inner.Duration;
        public IReadOnlyList<SessionSnapshot> Samples => inner.Samples;
        public void Start() => inner.Start();
        public void Stop() => inner.Stop();
        public void Add(SessionSnapshot sample) => inner.Add(sample);
        private Task Export()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exports.Add(completion);
            return completion.Task;
        }
        public Task ExportJsonAsync(string path, CancellationToken cancellationToken = default) => Export();
        public Task ExportCsvAsync(string path, CancellationToken cancellationToken = default) => Export();
    }
}
