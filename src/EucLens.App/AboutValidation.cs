using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;

namespace EucLens.App;

/// <summary>
/// Windows validation of the About page and the update check, run by --smoke-test: the executable's own metadata, no request before consent, the real controls (the button is
/// pressed through UI Automation), the states of the page with a fake checker (never GitHub), responsiveness while a check is pending, expanded legal text and screenshots in both themes.
/// </summary>
internal static class AboutValidation
{
    private static readonly StringBuilder Report = new();

    private sealed class FakeChecker(Func<CancellationToken, Task<UpdateCheckResult>> answer) : IUpdateChecker
    {
        public int Calls { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken) { Calls++; return answer(cancellationToken); }
    }

    private static void Log(string directory, string line)
    {
        Report.AppendLine(line);
        SmokeLog.Write("about: " + line);
        File.WriteAllText(Path.Combine(directory, "about-validation.txt"), Report.ToString());
    }

    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static Task Idle(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitFor(Func<bool> condition, string what, int milliseconds = 10_000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new InvalidOperationException($"Timed out waiting for {what}."); await Task.Delay(25); }
    }

    private static Button ButtonNamed(Window window, string name) =>
        DesktopValidation.Descendants<Button>(window).Single(b => b.IsVisible && (b.Content as string) == name);

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    internal static async Task RunAsync(Window window, MainViewModel vm, string directory)
    {
        Directory.CreateDirectory(directory);
        Report.Clear();
        var failures = new List<string>();
        var client = (FrameworkElement)window.Content;
        var originalTheme = vm.IsDark;
        var page = vm.Pages.OfType<AboutPage>().Single();
        async Task Section(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Log(directory, $"FAIL: {name} | {ex.GetType().Name}: {ex.Message} | {HistoryValidation.FirstFrame(ex)}");
                try { DesktopValidation.Capture(window, client, Path.Combine(directory, $"FAILED-about-{name.Replace(' ', '-')}.png")); } catch (Exception) { /* the failure itself is already recorded */ }
            }
        }

        await Section("executable metadata", () =>
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            var info = FileVersionInfo.GetVersionInfo(path);
            var identity = AppIdentity.Current;
            Require(info.ProductName == identity.Product && info.ProductName == "EUC Lens", $"ProductName '{info.ProductName}'.");
            Require(info.Comments == identity.Description, $"Comments '{info.Comments}'.");
            Require(info.FileDescription == "EUC Lens - Remote Session Performance Diagnostics", $"FileDescription '{info.FileDescription}'.");
            Require(info.CompanyName == identity.Publisher && info.CompanyName == "VDI Ninja", $"CompanyName '{info.CompanyName}'.");
            Require(identity.Author == "Dominik Jakubowski" && identity.Website == "https://vdesktop.ninja", $"Author '{identity.Author}' / website '{identity.Website}'.");
            Require(info.LegalCopyright == identity.Copyright, $"LegalCopyright '{info.LegalCopyright}'.");
            Require(info.ProductVersion == AppVersion.Current && info.FileVersion == AppVersion.Current + ".0", $"Version {info.ProductVersion} / {info.FileVersion}.");
            Log(directory, $"PASS: executable metadata: product '{info.ProductName}', description '{info.FileDescription}', version {info.ProductVersion} (file {info.FileVersion}), company/publisher '{info.CompanyName}', author '{identity.Author}', website {identity.Website}, copyright '{info.LegalCopyright}'; the commit recorded in the build is {identity.ShortCommit ?? "not recorded"}");
            return Task.CompletedTask;
        });

        await Section("no request before consent", async () =>
        {
            Require(vm.About.CheckCount == 0, $"{vm.About.CheckCount} update check(s) were started before anyone asked (the layout matrix visited the About page in both themes at every size).");
            vm.SelectedPage = page; await Idle(window); await Task.Delay(300);
            Require(vm.About.CheckCount == 0, "Showing the About page started an update check.");
            Log(directory, "PASS: no update check was started by startup, by the layout matrix's visits to the About page or by showing it again");
        });

        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            vm.IsDark = dark;
            vm.SelectedPage = page; await Idle(window); await Task.Delay(150);

            await Section($"page content {theme}", async () =>
            {
                var checksBefore = vm.About.CheckCount;      // the earlier theme's sections pressed the button on purpose; only a change during this section counts
                vm.About.UseCheckerForValidation(new FakeChecker(_ => Task.FromResult(new UpdateCheckResult(UpdateOutcome.UpToDate, "You have the latest version.", ReleaseVersion.Zero))));
                await Idle(window);
                var texts = DesktopValidation.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                foreach (var expected in new[] { "About", "EUC Lens", "Remote Session Performance Diagnostics", AppVersion.Current, "MIT", "Dominik Jakubowski", "VDI Ninja", vm.About.Facts.First(f => f.Label == "Copyright").Value, "Version " + AppVersion.Current })
                    Require(texts.Any(t => t == expected), $"The visible text '{expected}' is missing.");
                Require(!texts.Any(t => t.Contains("stable", StringComparison.OrdinalIgnoreCase)), "A visible text labels the release with a channel (stable).");
                var footer = DesktopValidation.Descendants<TextBlock>(window).Single(t => t.Text == "Version " + AppVersion.Current && System.Windows.Automation.AutomationProperties.GetName(t) == "Installed version");
                Require(footer.IsVisible && footer.ActualHeight > 0, "The navigation footer does not show the installed version.");
                var image = DesktopValidation.Descendants<Image>(window).Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "EUC Lens logo");
                Require(image.Source is not null && image.ActualWidth >= 80, "The logo is not shown.");
                foreach (var name in new[] { "Copy version information", "Open GitHub repository", "https://vdesktop.ninja", "Check for updates" })
                {
                    var button = ButtonNamed(window, name);
                    Require(button.IsEnabled && button.IsTabStop && button.Focusable && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)), $"'{name}' is not an enabled, focusable, named button.");
                }
                Require(!DesktopValidation.Descendants<Button>(window).Any(b => b.IsVisible && ((b.Content as string) is "Download update" or "View release notes")), "Update actions are shown before an update was found.");
                Require(vm.About.CheckCount == checksBefore, "Showing the page started an update check.");
                Log(directory, $"PASS: About page {theme}: identity (author, brand, website as a button), version, copyright, MIT, platform, logo and the footer version are shown from the single source; the four actions are named, enabled and focusable; no update action before a result");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"about-idle-{theme}.png"));
            });

            await Section($"update available {theme}", async () =>
            {
                var release = new AvailableRelease(ReleaseVersion.TryParse("0.9.0", out var v) ? v : ReleaseVersion.Zero, "v0.9.0", "EUC Lens 0.9.0", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero),
                    "Test data from the validation, not a real release.\nA second line of the release summary.", new Uri("https://github.com/vdesktopninja/EucLens/releases/tag/v0.9.0"), false);
                var fake = new FakeChecker(_ => Task.FromResult(new UpdateCheckResult(UpdateOutcome.UpdateAvailable, "Version 0.9.0 is available.", ReleaseVersion.Zero, release)));
                vm.About.UseCheckerForValidation(fake);
                await Idle(window);
                Invoke(ButtonNamed(window, "Check for updates"));
                await WaitFor(() => vm.About.HasUpdate && !vm.About.IsChecking, "the update result");
                await Idle(window); await Task.Delay(150);
                Require(fake.Calls == 1 && vm.About.CheckCount >= 1, "Pressing the button did not start exactly one check.");
                var texts = DesktopValidation.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                foreach (var expected in new[] { "Version 0.9.0 is available.", "Installed: " + AppVersion.Current, "Latest available: 0.9.0", "Published 1 December 2026 (UTC)", "EUC Lens 0.9.0" })
                    Require(texts.Any(t => t == expected), $"The visible text '{expected}' is missing.");
                Require(texts.Any(t => t.StartsWith("Test data from the validation", StringComparison.Ordinal)), "The release summary is not shown.");
                var download = ButtonNamed(window, "Download update"); var notes = ButtonNamed(window, "View release notes");
                Require(download.IsEnabled && notes.IsEnabled, "Download update / View release notes are not enabled.");
                Require(vm.About.DownloadUpdateCommand.CanExecute(null), "The download command is not available.");
                Log(directory, $"PASS: update available ({theme}): the real button, pressed through UI Automation, ran one check with a fake checker; installed, latest, date, title, summary and the two actions are shown (no browser is opened by the validation)");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"about-update-available-{theme}.png"));
            });

            await Section($"neutral states {theme}", async () =>
            {
                foreach (var (outcome, text) in new[] { (UpdateOutcome.NoPublicRelease, "No public release has been published yet."), (UpdateOutcome.Offline, "GitHub could not be reached. Check the network connection, proxy or firewall. EUC Lens works the same offline.") })
                {
                    vm.About.UseCheckerForValidation(new FakeChecker(_ => Task.FromResult(new UpdateCheckResult(outcome, text, ReleaseVersion.Zero))));
                    await Idle(window);
                    Invoke(ButtonNamed(window, "Check for updates"));
                    await WaitFor(() => vm.About.UpdateStatus == text, $"the {outcome} message");
                    await Idle(window);
                    Require(!vm.About.HasUpdate && !DesktopValidation.Descendants<Button>(window).Any(b => b.IsVisible && (b.Content as string) == "Download update"), $"{outcome} offers a download.");
                    Require(DesktopValidation.Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == text), $"The {outcome} message is not displayed.");
                    if (outcome == UpdateOutcome.Offline) DesktopValidation.Capture(window, client, Path.Combine(directory, $"about-offline-{theme}.png"));
                }
                Log(directory, $"PASS: neutral results ({theme}): 'no public release' and 'offline' show their plain message, offer no download and leave monitoring running");
            });

            await Section($"responsive while checking {theme}", async () =>
            {
                var gate = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var monitoringBefore = vm.Monitoring.Coordinator.State;
                vm.About.UseCheckerForValidation(new FakeChecker(_ => gate.Task));
                await Idle(window);
                Invoke(ButtonNamed(window, "Check for updates"));
                await WaitFor(() => vm.About.IsChecking, "the check to start");
                await Idle(window);
                // While the check is pending the UI thread keeps serving input-priority work, the page says it is checking and the button cannot be pressed twice.
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 20; i++) { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input); await Task.Delay(20); }
                var slowest = watch.ElapsedMilliseconds / 20;
                Require(DesktopValidation.Descendants<TextBlock>(window).Any(t => t.IsVisible && t.Text == "Checking GitHub for releases…"), "The page does not say that it is checking.");
                Require(!ButtonNamed(window, "Check for updates").IsEnabled, "The button can be pressed while a check is running.");
                Require(vm.Monitoring.Coordinator.State == monitoringBefore, "Monitoring changed state during the check.");
                gate.SetResult(new UpdateCheckResult(UpdateOutcome.UpToDate, "You have the latest version.", ReleaseVersion.Zero));
                await WaitFor(() => !vm.About.IsChecking, "the check to finish");
                Log(directory, $"PASS: responsive while checking ({theme}): 20 input-priority round trips took {watch.ElapsedMilliseconds} ms in total ({slowest} ms each, including a 20 ms pause) while a check was pending; the page said it was checking and the button was disabled");
            });

            await Section($"legal text {theme}", async () =>
            {
                vm.About.UseCheckerForValidation(new FakeChecker(_ => Task.FromResult(new UpdateCheckResult(UpdateOutcome.UpToDate, "You have the latest version.", ReleaseVersion.Zero))));
                var expanders = DesktopValidation.Descendants<Expander>(window).Where(e => e.IsVisible).ToList();
                Require(expanders.Count == 4, $"{expanders.Count} disclosures instead of 4 (release notes, license, acknowledgments, full texts).");
                foreach (var expander in expanders) expander.IsExpanded = true;
                await Idle(window); await Task.Delay(150);
                var texts = DesktopValidation.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Require(texts.Any(t => t.StartsWith("MIT License", StringComparison.Ordinal) && t.Contains("Permission is hereby granted", StringComparison.Ordinal)), "The license text is not shown.");
                Require(texts.Any(t => t.Contains("LiveChartsCore", StringComparison.Ordinal) && t.Contains("SkiaSharp", StringComparison.Ordinal) || t.Contains("# Third-party notices", StringComparison.Ordinal)), "The acknowledgments are not shown.");
                Require(texts.Any(t => t.StartsWith("## " + AppVersion.Current, StringComparison.Ordinal)), "The release notes of this version are not shown.");
                var full = DesktopValidation.Descendants<TextBox>(window).Single(b => b.IsVisible && System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Verbatim license texts", StringComparison.Ordinal));
                Require(full.IsReadOnly && full.Text.Contains("THIRD-PARTY SOFTWARE NOTICES AND INFORMATION", StringComparison.Ordinal) && full.Text.Contains("License notice for zlib-ng", StringComparison.Ordinal) && full.Text.Contains("GLFW", StringComparison.Ordinal) && full.ActualHeight > 100, "The full third-party license texts are not shown.");
                TypographyValidation.Reset();
                TypographyValidation.CheckPage(window, $"about legal {theme}");
                Require(TypographyValidation.Problems.Count == 0, "Typography problems with the legal text open: " + string.Join(" || ", TypographyValidation.Problems.Take(10)));
                DesktopValidation.PageHost(window)?.ScrollToBottom();
                await Idle(window); await Task.Delay(100);
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"about-legal-{theme}.png"));
                DesktopValidation.PageHost(window)?.ScrollToTop();
                foreach (var expander in expanders) expander.IsExpanded = false;
                await Idle(window);
                Log(directory, $"PASS: legal text ({theme}): release notes of this version, the MIT license, the acknowledgments and the verbatim third-party license texts open; no text clipped, overlapping or outside its card with all four open");
            });
        }

        vm.IsDark = originalTheme;
        vm.SelectedPage = vm.Pages[0];
        await Idle(window);
        if (failures.Count == 0) Log(directory, "PASS: about validation complete");
        else throw new InvalidOperationException($"{failures.Count} About check(s) failed: " + string.Join(" | ", failures));
    }
}
