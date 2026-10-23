using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Static guards for the background architecture: services stay WPF-free and window-independent, the tray holds no views, exit is the single way out.</summary>
public sealed class BackgroundArchitectureTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Text(string relative) => File.ReadAllText(Path.Combine(Repo(), relative));

    [Fact]
    public void MonitoringAndRecordingServicesKnowNothingAboutWindowsOrWpf()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.Core/Monitoring")).Concat(Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.Core/Recording"))))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("System.Windows", text);
            Assert.DoesNotContain("Dispatcher", text);
            Assert.DoesNotContain("UserControl", text);
            // One cadence only: no extra high-frequency timers anywhere in the services.
            Assert.DoesNotContain("DispatcherTimer", text);
            Assert.DoesNotContain("System.Threading.Timer", text);
            Assert.DoesNotContain("System.Timers", text);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.Core"), "*.cs", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("new PeriodicTimer(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTrayHoldsNoViewsPagesOrCharts()
    {
        var tray = Text("src/EucLens.App/TrayIconService.cs");
        foreach (var forbidden in new[] { "Views", "UserControl", "LiveChart", "ChartData", "ChartObjects", "DashboardPage", "DiagnosticsPage", "PageViewModel" })
            Assert.DoesNotContain(forbidden, tray);
        var lifetime = Text("src/EucLens.App/ApplicationLifetimeService.cs");
        Assert.DoesNotContain("LiveChart", lifetime); Assert.DoesNotContain("ChartData", lifetime);
    }

    [Fact]
    public void TheSamplingLoopIsOwnedByTheCoordinatorAndStartedOnceByTheApplication()
    {
        var vm = Text("src/EucLens.App/ViewModels/MainViewModel.cs");
        Assert.Contains("monitoring.Coordinator.RunAsync(TickAsync, token)", vm);
        Assert.DoesNotContain("new PeriodicTimer", vm);
        var app = Text("src/EucLens.App/App.xaml.cs");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, @"vm\.RunAsync\("));
        Assert.DoesNotContain("window.Closing +=", app); // the window's closing is the lifetime service's business
        Assert.Contains("lifetime.Attach(window, tray)", app);
    }

    [Fact]
    public void ExitFinalizesRecordingsRemovesTheTrayIconAndIsTheOnlyWayOut()
    {
        var app = Text("src/EucLens.App/App.xaml.cs");
        Assert.Contains("monitoring.ShutdownAsync(RecordingEndReason.AppExit)", app);
        Assert.Contains("tray?.Dispose()", app);
        var lifetime = Text("src/EucLens.App/ApplicationLifetimeService.cs");
        Assert.Contains("e.Cancel = true;", lifetime);                       // closing never silently ends a hidden application
        Assert.Contains("Settings.CloseToTray", lifetime); Assert.Contains("Settings.MinimizeToTray", lifetime);
        Assert.Contains("ConfirmStopRecordingAndExit", lifetime);
        Assert.Contains("RecordingEndReason.SessionEnding", lifetime);        // logoff finalizes the recording
        Assert.Contains("SessionSwitchReason.RemoteDisconnect", lifetime);
        Assert.Contains("SessionSwitchReason.SessionLock", lifetime);
        var tray = Text("src/EucLens.App/TrayIconService.cs");
        foreach (var text in new[] { "Open EUC Lens", "Start Monitoring", "Pause Monitoring", "Resume Monitoring", "Start Recording", "Stop Recording", "Open Recordings Folder", "Settings", "Exit EUC Lens" })
            Assert.Contains($"\"{text}\"", tray);
    }

    [Fact]
    public void NothingStartsWithWindowsOrWithoutTheUsersConsent()
    {
        var all = string.Concat(Directory.GetFiles(Path.Combine(Repo(), "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).Select(File.ReadAllText));
        Assert.DoesNotContain("CurrentVersion\\Run", all);
        Assert.DoesNotContain("RegisterStartupTask", all);
        Assert.DoesNotContain("ServiceBase", all);
        Assert.False(BackgroundSettings.Defaults.RecordOnStart);
        Assert.False(BackgroundSettings.Defaults.MinimizeToTray || BackgroundSettings.Defaults.CloseToTray || BackgroundSettings.Defaults.Notifications);
        var manifest = Text("src/EucLens.App/app.manifest");
        Assert.Contains("asInvoker", manifest);   // no elevation
    }

    [Fact]
    public void WindowsSmokeTestAndWorkflowCoverTheBackgroundFeatures()
    {
        var harness = Text("src/EucLens.App/BackgroundValidation.cs");
        foreach (var marker in new[] { "CheckIcons", "CheckTrayStates", "CheckMinimizeRestoreAndHiddenRecording", "CheckCloseToTray", "CheckSingleInstance", "Soak(", "second monitoring loop",
            "close to tray hid the window", "repeated hide and restore", "hidden in the tray", "visible (Dashboard)", "samples lost" })
            Assert.Contains(marker, harness);
        // The 48-cycle navigation soak keeps its thresholds.
        var app = Text("src/EucLens.App/App.xaml.cs");
        Assert.Contains("for (var batch = 0; batch < 4; batch++)", app);
        var workflow = Text(".github/workflows/windows.yml");
        Assert.Contains("Verify the executable carries the EUC Lens icon", workflow);
        Assert.Contains("PASS: Exit finalized the running recording", workflow);
        Assert.Contains("background-validation.txt", workflow);
        Assert.Contains("artifacts/win-x64/visual-evidence/", workflow);
    }

    [Fact]
    public void SingleInstanceRefusesASecondOwnerAndWakesTheFirst()
    {
        if (!OperatingSystem.IsWindows()) return; // named events exist only on Windows; the CI runs this
        var name = @"Local\EucLens-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.TryAcquire(name, out _);
        Assert.NotNull(first);
        using var woken = new ManualResetEventSlim();
        first!.ActivationRequested += woken.Set;
        using var second = SingleInstance.TryAcquire(name, out var signalled);
        Assert.Null(second);
        Assert.True(signalled);
        Assert.True(woken.Wait(TimeSpan.FromSeconds(3)), "The first instance was not asked to show itself.");
        // Another name (another user or session) is independent.
        using var other = SingleInstance.TryAcquire(name + "-other", out _);
        Assert.NotNull(other);
        Assert.StartsWith(@"Local\", SingleInstance.DefaultName());
    }
}
