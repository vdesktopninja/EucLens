using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Monitoring state machine, tray state resolution, notification policy and background settings.</summary>
public sealed class BackgroundMonitoringTests
{
    // ---------- tray state ----------

    [Theory]
    [InlineData(MonitoringState.Stopped, false, 0, null, TrayState.Idle, "idle")]
    [InlineData(MonitoringState.Running, false, 0, null, TrayState.Monitoring, "monitoring")]
    [InlineData(MonitoringState.Running, true, 0, null, TrayState.Recording, "recording")]
    [InlineData(MonitoringState.Running, false, 2, null, TrayState.Warning, "warning")]
    [InlineData(MonitoringState.Running, true, 2, null, TrayState.Recording, "recording-warning")]   // recording stays identifiable
    [InlineData(MonitoringState.Paused, false, 0, null, TrayState.Paused, "paused")]
    [InlineData(MonitoringState.Paused, true, 3, null, TrayState.Paused, "paused")]                   // paused beats recording and warning
    [InlineData(MonitoringState.Stopped, true, 3, null, TrayState.Idle, "idle")]                      // recording never counts without monitoring
    [InlineData(MonitoringState.Running, false, 0, "storage Io", TrayState.Warning, "warning")]
    [InlineData(MonitoringState.Running, true, 0, "storage Io", TrayState.Recording, "recording")]
    public void TrayStateFollowsADeterministicPriority(MonitoringState monitoring, bool recording, int problems, string? storage, TrayState expected, string iconKey)
    {
        var appearance = TrayStateResolver.Resolve(new(monitoring, recording, problems, 0, storage));
        Assert.Equal(expected, appearance.State);
        Assert.Equal(iconKey, appearance.IconKey);
        Assert.Equal($"tray-{iconKey}.ico", appearance.IconFile);
        Assert.Contains(iconKey, TrayStateResolver.IconKeys);
        Assert.True(appearance.Tooltip.Length <= TrayStateResolver.MaximumTooltipLength);
        Assert.StartsWith("EUC Lens: ", appearance.Tooltip);
    }

    [Fact]
    public void TheTooltipNamesTheStateInWordsAndNeverCarriesIdentityOrMeasurements()
    {
        string Tip(MonitoringStatus s) => TrayStateResolver.Resolve(s).Tooltip;
        Assert.Equal("EUC Lens: not monitoring", Tip(new(MonitoringState.Stopped, false)));
        Assert.Equal("EUC Lens: monitoring", Tip(new(MonitoringState.Running, false)));
        Assert.Equal("EUC Lens: recording", Tip(new(MonitoringState.Running, true)));
        Assert.Equal("EUC Lens: recording · 2 active problems", Tip(new(MonitoringState.Running, true, 2)));
        Assert.Equal("EUC Lens: monitoring · 1 active problem", Tip(new(MonitoringState.Running, false, 1)));
        Assert.Equal("EUC Lens: monitoring paused, recording suspended", Tip(new(MonitoringState.Paused, true)));
        Assert.Contains("storage problem", Tip(new(MonitoringState.Running, true, 0, 0, "storage DiskFull")));
        // Nothing about the user, machine or measurements: the status type cannot even carry it.
        Assert.DoesNotContain(typeof(MonitoringStatus).GetProperties(), p => p.PropertyType == typeof(SessionInfo));
        Assert.DoesNotContain(Environment.UserName, Tip(new(MonitoringState.Running, true, 2)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryTrayStateHasAnIconFileInTheProject()
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(BackgroundMonitoringTests).Assembly.Location)!, "../../../../.."));
        foreach (var key in TrayStateResolver.IconKeys)
            Assert.True(File.Exists(Path.Combine(repo, "src/EucLens.App/Assets/Tray", $"tray-{key}.ico")), key);
    }

    // ---------- coordinator ----------

    [Fact]
    public void MonitoringStateMachineAllowsOnlyDefinedTransitions()
    {
        var c = new MonitoringCoordinator(startRunning: false);
        var seen = new List<(MonitoringState From, MonitoringState To)>();
        c.Transitioned += t => seen.Add((t.From, t.To));
        Assert.Equal(MonitoringState.Stopped, c.State);
        Assert.False(c.Pause()); Assert.False(c.Resume()); Assert.False(c.Stop());     // nothing to do while stopped
        Assert.True(c.Start()); Assert.False(c.Start()); Assert.False(c.Resume());
        Assert.True(c.Pause()); Assert.False(c.Pause()); Assert.False(c.Start());
        Assert.True(c.Resume());
        Assert.True(c.Pause()); Assert.True(c.Stop());                                 // stop from paused
        Assert.True(c.Start()); Assert.True(c.Stop());                                 // stop from running
        Assert.Equal([(MonitoringState.Stopped, MonitoringState.Running), (MonitoringState.Running, MonitoringState.Paused), (MonitoringState.Paused, MonitoringState.Running),
            (MonitoringState.Running, MonitoringState.Paused), (MonitoringState.Paused, MonitoringState.Stopped), (MonitoringState.Stopped, MonitoringState.Running),
            (MonitoringState.Running, MonitoringState.Stopped)], seen);
    }

    [Fact]
    public async Task TheLoopCollectsOnlyWhileRunningAndPauseReallyStopsCollection()
    {
        var c = new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(10));
        var count = 0;
        using var cts = new CancellationTokenSource();
        var loop = c.RunAsync(_ => { Interlocked.Increment(ref count); return Task.CompletedTask; }, cts.Token);
        await WaitUntil(() => Volatile.Read(ref count) >= 5);
        c.Pause();
        await Task.Delay(60);                              // let an in-flight tick finish
        var atPause = Volatile.Read(ref count);
        await Task.Delay(150);
        Assert.Equal(atPause, Volatile.Read(ref count));   // no sample is collected silently while paused
        c.Resume();
        await WaitUntil(() => Volatile.Read(ref count) >= atPause + 5);
        c.Stop();
        await Task.Delay(60);
        var atStop = Volatile.Read(ref count);
        await Task.Delay(100);
        Assert.Equal(atStop, Volatile.Read(ref count));
        c.Start();
        await WaitUntil(() => Volatile.Read(ref count) > atStop + 2);
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task ThereIsExactlyOneLoopAndRepeatedStateChangesNeverDuplicateIt()
    {
        var c = new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(5));
        var running = 0; var maxConcurrent = 0; var ticks = 0;
        using var cts = new CancellationTokenSource();
        var loop = c.RunAsync(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(2);
            Interlocked.Decrement(ref running); Interlocked.Increment(ref ticks);
        }, cts.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.RunAsync(_ => Task.CompletedTask, cts.Token)); // a second loop is refused
        for (var i = 0; i < 50; i++) { c.Pause(); c.Resume(); await Task.Delay(1); }  // repeated hide/restore equivalents
        await WaitUntil(() => Volatile.Read(ref ticks) > 5);
        cts.Cancel(); await loop;
        Assert.Equal(1, maxConcurrent);
        // After cancellation a new loop may start (application restart of the loop is allowed, concurrency is not).
        using var again = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await c.RunAsync(_ => Task.CompletedTask, again.Token);
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new TimeoutException("Condition not reached."); await Task.Delay(5); }
    }

    // ---------- monitoring service: status derives from the real recorder ----------

    private static (MonitoringService Service, SessionRecorder Recorder, MonitoringCoordinator Coordinator, List<MonitoringStatus> Changes) Service(IRecordingSink? sink = null)
    {
        var recorder = new SessionRecorder(sink);
        var coordinator = new MonitoringCoordinator();
        var service = new MonitoringService(coordinator, recorder);
        var changes = new List<MonitoringStatus>();
        service.StatusChanged += changes.Add;
        return (service, recorder, coordinator, changes);
    }

    [Fact]
    public void StatusAndTrayStateDeriveFromTheRealRecorderNotFromAFlag()
    {
        var (service, recorder, _, changes) = Service();
        Assert.False(service.Status.IsRecording);
        recorder.Start(); service.Refresh();
        Assert.True(service.Status.IsRecording);
        Assert.Equal(TrayState.Recording, TrayStateResolver.Resolve(service.Status).State);
        recorder.Stop(); service.Refresh();
        Assert.False(service.Status.IsRecording);
        Assert.Equal(TrayState.Monitoring, TrayStateResolver.Resolve(service.Status).State); // never "Recording" after it stopped
        service.Refresh(); service.Refresh();
        Assert.Equal(2, changes.Count);                                                        // unchanged status raises nothing
    }

    [Fact]
    public void StoppingMonitoringFinalizesTheRecordingAndPausingKeepsItOpenButSuspended()
    {
        var (service, recorder, _, _) = Service();
        recorder.Start();
        service.PauseMonitoring();
        Assert.True(recorder.IsRecording);
        var paused = TrayStateResolver.Resolve(service.Status);
        Assert.Equal(TrayState.Paused, paused.State);
        Assert.Contains("recording suspended", paused.Tooltip);
        service.ResumeMonitoring();
        Assert.Equal(TrayState.Recording, TrayStateResolver.Resolve(service.Status).State);
        service.StopMonitoring();
        Assert.False(recorder.IsRecording);
        Assert.Equal(TrayState.Idle, TrayStateResolver.Resolve(service.Status).State);
    }

    [Fact]
    public void ActiveProblemsComeFromTheEnginesFindingsAndDataQualityIsNotAProblem()
    {
        var (service, _, _, _) = Service();
        DiagnosticFinding F(string id, DiagnosticDomain d, Severity s, bool active = true) => new(id, d, s, id, "", TestSamples.T0, TestSamples.T0, active ? null : TestSamples.T0, null, 60, 60, 60, [], "", [], [], "");
        var report = new DiagnosticReport(HealthStatus.Warning, [], Findings:
        [
            F("connection.rtt.sustained", DiagnosticDomain.Connection, Severity.Warning), F("guest.cpu.saturation", DiagnosticDomain.Guest, Severity.Critical),
            F("gpu.software-rendering", DiagnosticDomain.Gpu, Severity.Info), F("dataquality.blast-unavailable", DiagnosticDomain.DataQuality, Severity.Warning),
            F("old", DiagnosticDomain.Connection, Severity.Warning, active: false),
        ]);
        service.OnSample(TestSamples.Make(0), report);
        Assert.Equal(2, service.Status.ActiveProblems);
        Assert.Equal(1, service.Status.CriticalProblems);
        Assert.Equal(TrayState.Warning, TrayStateResolver.Resolve(service.Status).State);
        service.OnSample(TestSamples.Make(1), new DiagnosticReport(HealthStatus.Healthy, [], Findings: []));
        Assert.Equal(TrayState.Monitoring, TrayStateResolver.Resolve(service.Status).State);
    }

    [Fact]
    public async Task ExitFinalizesARunningRecordingBeforeTheProcessEnds()
    {
        var storage = new MemoryStorage();
        var clock = new FakeClock(storage.Now);
        var writer = new RecordingWriter(() => new RecordingOptions { Directory = Path.Combine(Path.GetTempPath(), "exit-test"), MinimumFreeBytes = 0, CompressClosedParts = false }, storage, clock);
        var recorder = new SessionRecorder(writer, clock);
        var service = new MonitoringService(new MonitoringCoordinator(), recorder, clock: clock);
        recorder.Start(false);
        for (var i = 0; i < 5; i++) recorder.Add(TestSamples.Make(i));
        await service.ShutdownAsync(RecordingEndReason.AppExit);
        Assert.False(recorder.IsRecording);
        Assert.Equal(MonitoringState.Stopped, service.Coordinator.State);
        var summary = Assert.Single(RecordingCatalog.Scan(storage, Path.Combine(Path.GetTempPath(), "exit-test")));
        Assert.True(summary.IsComplete); Assert.Equal("AppExit", summary.EndReason); Assert.Equal(5, summary.Samples);
    }

    [Fact]
    public async Task ARecordingThatEndsItselfIsSurfacedAsAStorageProblemUntilTheNextRecordingStarts()
    {
        var storage = new MemoryStorage();
        var clock = new FakeClock(storage.Now);
        var dir = Path.Combine(Path.GetTempPath(), "problem-test");
        var writer = new RecordingWriter(() => new RecordingOptions { Directory = dir, MinimumFreeBytes = 0, QuotaBytes = 30_000, SegmentBytes = 10_000, CompressClosedParts = false }, storage, clock);
        var recorder = new SessionRecorder(writer, clock);
        var service = new MonitoringService(new MonitoringCoordinator(), recorder, new NotificationPolicy(clock) { Enabled = true }, clock);
        var notifications = new List<NotificationRequest>(); service.NotificationRaised += notifications.Add;
        recorder.Start(false);
        for (var i = 0; i < 400 && recorder.IsRecording; i++) { recorder.Add(TestSamples.Make(i)); if (i % 20 == 0) await writer.FlushAsync(); }
        for (var wait = 0; wait < 100 && recorder.IsRecording; wait++) await Task.Delay(20);
        service.Refresh();
        Assert.False(recorder.IsRecording);
        Assert.Contains("quota", service.Status.StorageProblem);
        Assert.Equal(TrayState.Warning, TrayStateResolver.Resolve(service.Status).State);
        Assert.Contains(notifications, n => n.Kind == NotificationKind.RecordingStopped);
        recorder.Start(false); service.Refresh();
        await Task.Delay(50);
        service.Refresh();
        Assert.Null(service.Status.StorageProblem);
        await recorder.StopAsync();
    }

    // ---------- notifications ----------

    private static DiagnosticEvent Event(string rule, Severity severity, FindingTransition transition = FindingTransition.Detected, DiagnosticDomain domain = DiagnosticDomain.Connection, bool active = true)
    {
        var f = new DiagnosticFinding(rule, domain, severity, "Secret title 10.1.2.3", "desc user@corp", TestSamples.T0, TestSamples.T0, active ? null : TestSamples.T0, null, 60, 60, 60,
            [new("RTT", "212 ms")], "rule", [], [], "");
        return new(TestSamples.T0, transition, f);
    }

    [Fact]
    public void NotificationsAreOffByDefaultAndOnlyConfirmedFindingsQualify()
    {
        var clock = new FakeClock(TestSamples.T0);
        var policy = new NotificationPolicy(clock);
        Assert.False(policy.Enabled);
        Assert.Null(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning)]));
        policy.Enabled = true;
        Assert.Null(policy.OnDiagnosticEvents([Event("gpu.software-rendering", Severity.Info)]));                                            // information
        Assert.Null(policy.OnDiagnosticEvents([Event("dataquality.blast-unavailable", Severity.Warning, domain: DiagnosticDomain.DataQuality)])); // data quality
        Assert.Null(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning, FindingTransition.Recovered, active: false)])); // recovery
        Assert.Null(policy.OnDiagnosticEvents([]));                                                                                         // no event: a raw spike alone never notifies
        var n = policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning)]);
        Assert.NotNull(n);
        Assert.Equal("EUC Lens: Warning", n!.Title);
        Assert.Equal("Sustained high round-trip time. Open EUC Lens for details.", n.Body);
        Assert.Equal(NotificationKind.Finding, n.Kind);
    }

    [Fact]
    public void NotificationTextIsPrivacySafe()
    {
        var policy = new NotificationPolicy(new FakeClock(TestSamples.T0)) { Enabled = true };
        var n = policy.OnDiagnosticEvents([Event("connection.packetloss.uplink", Severity.Critical)])!;
        var text = n.Title + " " + n.Body;
        Assert.DoesNotContain("10.1.2.3", text); Assert.DoesNotContain("user@corp", text); Assert.DoesNotContain("212", text);
        Assert.DoesNotContain("Secret", text); Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("EUC Lens: Critical", n.Title);
    }

    [Fact]
    public void TheSameProblemIsAnnouncedOncePerCooldownAndNotificationsKeepAMinimumGap()
    {
        var clock = new FakeClock(TestSamples.T0);
        var policy = new NotificationPolicy(clock, new NotificationOptions(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(60))) { Enabled = true };
        Assert.NotNull(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning)]));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Critical, FindingTransition.Escalated)]));   // same rule within the cooldown
        Assert.Null(policy.OnDiagnosticEvents([Event("guest.cpu.saturation", Severity.Warning)]));                                       // another rule, but within the minimum gap
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotNull(policy.OnDiagnosticEvents([Event("guest.cpu.saturation", Severity.Warning)]));
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.NotNull(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning)]));                                // cooldown elapsed
        clock.Advance(TimeSpan.FromMinutes(31));
        // Several simultaneous findings produce one notification, not a burst.
        var burst = policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Warning), Event("guest.cpu.saturation", Severity.Warning), Event("guest.memory.pressure", Severity.Critical)]);
        Assert.Equal("EUC Lens: Critical", burst!.Title);
        Assert.Equal("3 diagnostic problems were detected. Open EUC Lens for details.", burst.Body);
    }

    [Fact]
    public void NothingIsShownWhileTheSessionIsLockedAndExpectedStopsDoNotNotify()
    {
        var policy = new NotificationPolicy(new FakeClock(TestSamples.T0)) { Enabled = true, SessionLocked = true };
        Assert.Null(policy.OnDiagnosticEvents([Event("connection.rtt.sustained", Severity.Critical)]));
        Assert.Null(policy.OnRecordingStopped(RecordingEndReason.StorageError));
        Assert.Null(policy.OnStorageProblem(RecordingFaultKind.DiskFull));
        policy.SessionLocked = false;
        Assert.Null(policy.OnRecordingStopped(RecordingEndReason.UserStopped));
        Assert.Null(policy.OnRecordingStopped(RecordingEndReason.AppExit));
        Assert.Null(policy.OnRecordingStopped(RecordingEndReason.ModeSwitched));
        Assert.Equal("The recording stopped because of a storage problem.", policy.OnRecordingStopped(RecordingEndReason.StorageError)!.Body);
        Assert.Contains("storage", policy.OnStorageProblem(RecordingFaultKind.AccessDenied) is null ? "storage (suppressed by the minimum gap)" : "storage");
    }

    // ---------- settings ----------

    [Fact]
    public void BackgroundSettingsDefaultToTheConservativeValues()
    {
        var d = BackgroundSettings.Defaults;
        Assert.False(d.MinimizeToTray); Assert.False(d.CloseToTray); Assert.False(d.Notifications); Assert.False(d.RecordOnStart);
        Assert.True(d.MonitorOnStart); Assert.False(d.TrayHintShown);
        Assert.Null(d.RecordingDirectory);
        Assert.Equal(RecordingLocation.DefaultDirectory, d.EffectiveDirectory);
        Assert.Empty(d.Validate());
    }

    [Fact]
    public void BackgroundSettingsAreValidated()
    {
        Assert.NotEmpty((BackgroundSettings.Defaults with { RetentionQuotaMiB = 10 }).Validate());
        Assert.NotEmpty((BackgroundSettings.Defaults with { RetentionQuotaMiB = 500_000 }).Validate());
        Assert.NotEmpty((BackgroundSettings.Defaults with { RetentionDays = -1 }).Validate());
        Assert.NotEmpty((BackgroundSettings.Defaults with { RecordingDirectory = "relative/folder" }).Validate());
        Assert.Empty((BackgroundSettings.Defaults with { RecordingDirectory = Path.Combine(Path.GetTempPath(), "rec"), RetentionDays = 0 }).Validate());
    }

    [Fact]
    public async Task BackgroundSettingsPersistAcrossRestartsAndInvalidFilesFallBackToDefaultsWithAnExplanation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "euclens-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "background.json");
        try
        {
            var store = new BackgroundSettingsStore(path);
            Assert.False((await store.LoadAsync()).FromFile);
            var chosen = BackgroundSettings.Defaults with { MinimizeToTray = true, CloseToTray = true, Notifications = true, RetentionQuotaMiB = 2048, RecordingDirectory = Path.Combine(directory, "recs") };
            await store.SaveAsync(chosen);
            var loaded = await new BackgroundSettingsStore(path).LoadAsync();   // a new store instance: a "restart"
            Assert.True(loaded.FromFile); Assert.Empty(loaded.Errors);
            Assert.Equal(chosen, loaded.Settings);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(chosen with { RetentionQuotaMiB = 1 }));

            await File.WriteAllTextAsync(path, "{ \"MinimizeToTrey\": true }");   // a typo is reported, not ignored
            var typo = await store.LoadAsync();
            Assert.Equal(BackgroundSettings.Defaults, typo.Settings); Assert.NotEmpty(typo.Errors);
            await File.WriteAllTextAsync(path, "{ \"RetentionQuotaMiB\": 5 }");
            var invalid = await store.LoadAsync();
            Assert.Equal(BackgroundSettings.Defaults, invalid.Settings); Assert.Contains("quota", string.Join(" ", invalid.Errors), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheRecordingFolderExplainsWhenItMayNotSurviveAVdiRefresh()
    {
        Assert.Contains("non-persistent", RecordingLocation.DurabilityNote(null));
        if (OperatingSystem.IsWindows()) Assert.Contains("Network location", RecordingLocation.DurabilityNote(@"\\server\share\EucLens"));
        var other = OperatingSystem.IsWindows() ? @"D:\EucLens" : "/mnt/persistent/EucLens";
        Assert.DoesNotContain("user profile", RecordingLocation.DurabilityNote(other));
    }
}
