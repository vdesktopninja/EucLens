using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;

namespace EucLens.App;

/// <summary>
/// Windows validation of the redesigned Session Recorder page, run by --smoke-test with the real controls and the real recorder (synthetic measurements, a real recording on disk): the status and tiles through a recording,
/// the actions pressed through UI Automation, a repeated Start, pausing monitoring, the chart's empty state, the saved-recordings table (sorting, selection without scrolling or navigation, keyboard), the storage details,
/// the typography checks in every state and at the narrowest width, and screenshots in both themes. The scale cases of the layout matrix only simulate space; this section adds the narrowest real window.
/// </summary>
internal static class RecorderValidation
{
    private static readonly StringBuilder Report = new();

    private static void Log(string directory, string line)
    {
        Report.AppendLine(line);
        SmokeLog.Write("recorder: " + line);
        File.WriteAllText(Path.Combine(directory, "recorder-validation.txt"), Report.ToString());
    }

    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static Task Idle(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitFor(Func<bool> condition, string what, int milliseconds = 15_000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new InvalidOperationException($"Timed out waiting for {what}."); await Task.Delay(25); }
    }

    private static Button Named(Window window, string name) =>
        DesktopValidation.Descendants<Button>(window).Single(b => b.IsVisible && (AutomationProperties.GetName(b) == name || (b.Content as string) == name));

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<string> VisibleTexts(Window window) => DesktopValidation.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text);

    private static ListBox RecordingsList(Window window) => DesktopValidation.Descendants<ListBox>(window).Single(l => l.Name == "RecordingsList");

    internal static async Task RunAsync(Window window, MainViewModel vm, ISessionRecorder recorder, string directory)
    {
        Directory.CreateDirectory(directory);
        Report.Clear();
        var failures = new List<string>();
        var client = (FrameworkElement)window.Content;
        var originalTheme = vm.IsDark;
        var originalSize = (window.Width, window.Height);
        var page = vm.Pages.OfType<RecorderPage>().Single();
        var workspace = vm.Recorder;
        var saved = vm.Background.SavedRecordings;

        async Task Section(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Log(directory, $"FAIL: {name} | {ex.GetType().Name}: {ex.Message} | {HistoryValidation.FirstFrame(ex)}");
                try { DesktopValidation.Capture(window, client, Path.Combine(directory, $"FAILED-recorder-{name.Replace(' ', '-')}.png")); } catch (Exception) { /* the failure itself is already recorded */ }
            }
        }

        // Start from a known state: no recording running, monitoring on, the page shown.
        if (recorder.IsRecording) await recorder.StopAsync();
        if (vm.Monitoring.Coordinator.State != MonitoringState.Running) vm.Monitoring.ResumeMonitoring();
        vm.SelectedPage = page;
        await Idle(window); await Task.Delay(200);
        await vm.Background.RefreshRecordingsAsync();

        // A real recording to list and select: a few seconds of the synthetic measurements, saved by the real writer.
        await Section("prepare a saved recording", async () =>
        {
            vm.SelectedPage = page; await Idle(window);
            Invoke(Named(window, "Start recording"));
            await WaitFor(() => recorder.IsRecording && recorder.SampleCount >= 3, "three recorded samples");
            Invoke(Named(window, "Stop recording"));
            await WaitFor(() => !recorder.IsRecording, "the recording to stop");
            await recorder.StopAsync();
            await WaitFor(() => { workspace.Refresh(); return workspace.Status != RecorderStatusKind.Finalizing; }, "the recording to be finalized");
            await vm.Background.RefreshRecordingsAsync();
            Require(saved.Items.Count >= 1, "No saved recording is listed after a real recording.");
            Log(directory, $"PASS: a real recording was started and stopped through the page's own buttons and is listed ({saved.Items.Count} saved recording(s))");
        });

        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            vm.IsDark = dark;
            vm.SelectedPage = page; await Idle(window); await Task.Delay(150);
            var scroll = DesktopValidation.PageHost(window)!;

            await Section($"status and tiles {theme}", async () =>
            {
                Require(!recorder.IsRecording, "A recording is running before the check starts.");
                var tiles = DesktopValidation.Descendants<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                foreach (var label in new[] { "Recording", "Duration", "Samples" }) Require(tiles.Contains(label), $"The tile '{label}' is missing.");
                Require(tiles.Contains(workspace.StatusLabel) && tiles.Contains(workspace.DurationTile.Value) && tiles.Contains(workspace.SamplesTile.Value), "The tile values are not the view model's.");
                Require(tiles.Any(t => t.StartsWith("Monitoring active · Real Session", StringComparison.Ordinal) || t.StartsWith("Monitoring active · Synthetic test data", StringComparison.Ordinal)), "The monitoring summary is missing.");
                foreach (var name in new[] { "Session recorder", "Live connection latency", "Round-trip time (RTT) · latest 600 samples", "Saved recordings" }) Require(tiles.Contains(name), $"The text '{name}' is missing.");
                Require(workspace.Status is RecorderStatusKind.Ready or RecorderStatusKind.Completed, $"Unexpected status {workspace.Status} with no recording running.");
                Require(Named(window, "Start recording").IsEnabled && !Named(window, "Stop recording").IsEnabled, "Start must be enabled and Stop disabled while no recording runs.");
                Log(directory, $"PASS: status and tiles ({theme}): '{workspace.StatusLabel}', duration {workspace.DurationTile.Value}, samples {workspace.SamplesTile.Value}; the monitoring summary reads '{workspace.MonitoringSummary}'; Start enabled, Stop disabled");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-ready-{theme}.png"));
            });

            await Section($"chart empty state {theme}", async () =>
            {
                vm.RecordingChart.Clear(); workspace.Refresh(); await Idle(window); await Task.Delay(100);
                var overlay = DesktopValidation.Descendants<Border>(window).Single(b => AutomationProperties.GetName(b) == "Chart empty state");
                Require(overlay.IsVisible && overlay.ActualHeight > 0, "The empty state is not shown for an empty chart.");
                Require(VisibleTexts(window).Any(t => t.StartsWith("No RTT samples available yet.", StringComparison.Ordinal)), "The empty-state text is missing.");
                var chart = DesktopValidation.Descendants<LiveChartsCore.SkiaSharpView.WPF.CartesianChart>(window).Single();
                Require(chart.ActualHeight is >= 180 and <= 240, $"The chart is {chart.ActualHeight:0} DIPs high.");
                Require(vm.RecordingChart.Points.Count == 0, "Something was drawn for an empty chart.");
                Log(directory, $"PASS: chart empty state ({theme}): 'No RTT samples available yet.' over a chart {chart.ActualHeight:0} DIPs high (180 to 240 required), nothing drawn");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-empty-chart-{theme}.png"));
            });

            await Section($"active recording {theme}", async () =>
            {
                var markers = vm.Background.LastIncidentText;
                Invoke(Named(window, "Start recording"));
                await WaitFor(() => recorder.IsRecording && recorder.SampleCount >= 4 && vm.RecordingChart.Points.Any(p => p.Value is not null), "a recording with RTT values");
                var id = recorder.SinkStatus?.RecordingId;
                Require(workspace.Status == RecorderStatusKind.Recording && workspace.StatusLabel == "Recording", $"Status is {workspace.Status}.");
                Require(!Named(window, "Start recording").IsEnabled && Named(window, "Stop recording").IsEnabled, "Start must be disabled and Stop enabled while recording.");
                // A repeated press of Start (the second click of a double click) reaches a disabled button: nothing happens.
                var refused = false;
                try { Invoke(Named(window, "Start recording")); } catch (ElementNotEnabledException) { refused = true; }
                Require(refused && recorder.SinkStatus?.RecordingId == id, "A second Start was not refused.");
                // Chart: the values are shown, the empty state is gone.
                await Idle(window);
                Require(!DesktopValidation.Descendants<Border>(window).Single(b => AutomationProperties.GetName(b) == "Chart empty state").IsVisible, "The empty state stays over a chart with values.");
                Require(VisibleTexts(window).Any(t => t == "Recording") && VisibleTexts(window).Any(t => t == workspace.SamplesTile.Value), "The tiles do not follow the recording.");
                // Duration and samples change without a page refresh of their own.
                var samples = recorder.SampleCount;
                await WaitFor(() => recorder.SampleCount > samples, "another sample");
                await Idle(window);
                Require(workspace.SamplesTile.Value == recorder.SampleCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) || workspace.SamplesTile.Value == samples.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) || int.Parse(workspace.SamplesTile.Value.Replace(",", "").Replace(".", "").Replace(" ", ""), System.Globalization.CultureInfo.InvariantCulture) >= samples, "The samples tile did not follow the recorder.");
                var detail = markers;
                Invoke(Named(window, "Mark performance issue"));
                await WaitFor(() => vm.Background.LastIncidentText != detail && vm.Background.LastIncidentText.Length > 0, "the incident confirmation");
                Require(vm.Background.LastIncidentText.StartsWith("Last incident: no.", StringComparison.Ordinal), "The incident was not confirmed: " + vm.Background.LastIncidentText);
                Log(directory, $"PASS: active recording ({theme}): Recording with {workspace.SamplesTile.Value} samples and a growing duration; Start disabled, Stop enabled, a repeated Start refused (same recording id); the chart shows values and no empty state; 'Mark performance issue' confirmed: {vm.Background.LastIncidentText}");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-active-{theme}.png"));
            });

            await Section($"monitoring paused during a recording {theme}", async () =>
            {
                Require(recorder.IsRecording, "No recording is running.");
                var label = workspace.MonitoringActionLabel;
                Require(label == "Pause monitoring", $"The monitoring action reads '{label}'.");
                Invoke(Named(window, "Pause monitoring"));
                await WaitFor(() => vm.Monitoring.Coordinator.State == MonitoringState.Paused, "monitoring to pause");
                await Idle(window); await Task.Delay(100);
                Require(recorder.IsRecording && workspace.Status == RecorderStatusKind.Recording, "Pausing monitoring stopped the recording.");
                Require(workspace.StatusTile.Detail == "Monitoring paused" && VisibleTexts(window).Any(t => t.StartsWith("Monitoring paused · ", StringComparison.Ordinal)), "The page does not say monitoring is paused.");
                Require(VisibleTexts(window).Any(t => t.Contains("gap", StringComparison.Ordinal)), "The gap explanation is missing.");
                Require(Named(window, "Resume monitoring").IsEnabled, "Resume monitoring is not available.");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-paused-{theme}.png"));
                Invoke(Named(window, "Resume monitoring"));
                await WaitFor(() => vm.Monitoring.Coordinator.State == MonitoringState.Running, "monitoring to resume");
                Log(directory, $"PASS: monitoring paused during a recording ({theme}): the recording kept running and the page said 'Monitoring paused' with the gap explanation; Resume monitoring was available and worked");
            });

            await Section($"stop and complete {theme}", async () =>
            {
                Invoke(Named(window, "Stop recording"));
                await WaitFor(() => !recorder.IsRecording, "the recording to stop");
                await recorder.StopAsync();
                await WaitFor(() => { workspace.Refresh(); return workspace.Status != RecorderStatusKind.Finalizing; }, "finalization");
                await vm.Background.RefreshRecordingsAsync();
                await Idle(window);
                Require(workspace.Status == RecorderStatusKind.Completed, $"Status is {workspace.Status} after stopping.");
                Require(Named(window, "Start recording").IsEnabled && !Named(window, "Stop recording").IsEnabled, "Start/Stop did not return to their ready state.");
                Log(directory, $"PASS: stop ({theme}): Completed, Start enabled again, the recording is in the saved list ({saved.Items.Count} recording(s))");
            });

            await Section($"saved recordings {theme}", async () =>
            {
                Require(saved.Items.Count >= 1, "No saved recordings to select.");
                var list = RecordingsList(window);
                list.BringIntoView(); await Idle(window);
                scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset)); await Idle(window);
                // Bring the table into view first, so that a selection that scrolls the page would show as a change of offset.
                var table = list.Parent as FrameworkElement;
                table?.BringIntoView(); await Idle(window); await Task.Delay(100);
                var offset = scroll.VerticalOffset;
                saved.SelectedItem = null; await Idle(window);
                Require(!Named(window, "Analyze recording").IsEnabled, "Analyze is enabled with nothing selected.");
                Require(VisibleTexts(window).Any(t => t == SavedRecordingsViewModel.SelectHint), "The reason the actions are unavailable is not shown.");
                list.SelectedIndex = 0; await Idle(window); await Task.Delay(100);
                Require(vm.SelectedPage == page, "Selecting a recording navigated away.");
                Require(Math.Abs(scroll.VerticalOffset - offset) < 1, $"Selecting a recording scrolled the page ({offset:0} to {scroll.VerticalOffset:0}).");
                Require(saved.SelectedItem is not null && Named(window, "Analyze recording").IsEnabled && Named(window, "Export JSON").IsEnabled, "The actions of the selected recording are not available.");
                foreach (var name in new[] { "Show file", "Open folder" }) Require(DesktopValidation.Descendants<Button>(window).Any(b => b.IsVisible && (b.Content as string) == name && b.IsEnabled), $"'{name}' is not enabled for the selected recording.");
                Require(((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected, "The selected row is not highlighted.");
                Require(VisibleTexts(window).Any(t => t == saved.SelectionSummary && t.Length > 0), "The selection summary is not shown.");
                // Sorting through the real header buttons.
                Invoke(Named(window, "Sort by duration")); await Idle(window);
                Require(saved.SortKey == RecordingSort.Duration && saved.SortDescending, "Sort by duration did not apply.");
                Invoke(Named(window, "Sort by size")); await Idle(window);
                Invoke(Named(window, "Sort by recording date")); await Idle(window);
                Require(saved.SortKey == RecordingSort.Recorded && saved.SortDescending, "Sort by date did not return to newest first.");
                Require(saved.Items.Zip(saved.Items.Skip(1), (a, b) => a.StartedUtc >= b.StartedUtc).All(x => x), "The list is not newest first.");
                Require(saved.SelectedItem is not null, "Sorting lost the selection.");
                // Keyboard: arrow keys move the selection and never the page (real focus where this runner's window can take it, as the other keyboard checks do).
                var keyboard = "real focus movement NOT verified (the window cannot take focus on this runner); structural checks only";
                window.Activate();
                for (var i = 0; i < 20 && !window.IsActive; i++) await Task.Delay(50);
                if (saved.Items.Count > 1 && window.IsActive && list.ItemContainerGenerator.ContainerFromItem(saved.SelectedItem) is ListBoxItem current && ReferenceEquals(Keyboard.Focus(current), current))
                {
                    var before = saved.SelectedItem;
                    DesktopValidation.RaiseKey(current, Keyboard.FocusedElement is ListBoxItem item && saved.Items.IndexOf(before!) < saved.Items.Count - 1 ? Key.Down : Key.Up);
                    await Idle(window);
                    Require(!ReferenceEquals(before, saved.SelectedItem), "The arrow key did not move the selection.");
                    Require(Math.Abs(scroll.VerticalOffset - offset) < 1, "The arrow key scrolled the page.");
                    keyboard = "an arrow key moved the selection and not the page";
                }
                Require(Named(window, "Analyze recording").IsTabStop && Named(window, "Analyze recording").Focusable, "Analyze recording is not reachable by keyboard.");
                Log(directory, $"PASS: saved recordings ({theme}): no actions without a selection (and the reason shown); selecting a row highlighted it, navigated nowhere, opened nothing and left the page at offset {offset:0}; Analyze, Export JSON, Export CSV, Show file and Open folder became available; sorting through the real headers kept the selection; {keyboard}");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-saved-{theme}.png"));
            });

            await Section($"storage details and typography {theme}", async () =>
            {
                var expander = DesktopValidation.Descendants<Expander>(window).Single(e => e.IsVisible && AutomationProperties.GetName(e) == "Storage and recording details");
                Require(!expander.IsExpanded, "The storage details are not collapsed by default.");
                expander.IsExpanded = true; await Idle(window); await Task.Delay(100);
                var texts = VisibleTexts(window).ToList();
                Require(texts.Any(t => t == workspace.FolderText) && texts.Any(t => t.StartsWith("Storage quota", StringComparison.Ordinal)) && texts.Any(t => t.Contains("euclens.recording/1", StringComparison.Ordinal)) && texts.Any(t => t.Contains("exact length as a gap", StringComparison.Ordinal)) && texts.Any(t => t.Contains("non-persistent VDI", StringComparison.Ordinal)), "A storage explanation is missing from the details.");
                TypographyValidation.Reset();
                TypographyValidation.CheckPage(window, $"recorder with a selection and the details open ({theme})");
                Require(TypographyValidation.Problems.Count == 0, "Typography problems: " + string.Join(" || ", TypographyValidation.Problems.Take(10)));
                scroll.ScrollToBottom(); await Idle(window); await Task.Delay(100);
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-storage-{theme}.png"));
                scroll.ScrollToTop();
                expander.IsExpanded = false; await Idle(window);
                Log(directory, $"PASS: storage details ({theme}): collapsed by default; open they show the folder, quota and retention, the recording format, pause semantics and the VDI note; no text clipped, overlapping or outside its card");
            });
        }

        // The narrowest real window the application allows, with a selection and every state shown, in both themes.
        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            await Section($"narrow window {theme}", async () =>
            {
                vm.IsDark = dark;
                vm.SelectedPage = page; await Idle(window);
                window.Width = Math.Max(window.MinWidth, 760); window.Height = Math.Max(window.MinHeight, 560);
                await Idle(window); await Task.Delay(250);
                if (saved.Items.Count > 0) saved.SelectedItem = saved.Items[0];
                vm.RecordingChart.Clear(); workspace.Refresh();
                var expander = DesktopValidation.Descendants<Expander>(window).Single(e => e.IsVisible && AutomationProperties.GetName(e) == "Storage and recording details");
                expander.IsExpanded = true; await Idle(window); await Task.Delay(200);
                TypographyValidation.Reset();
                TypographyValidation.CheckPage(window, $"recorder at the narrowest window ({theme})");
                var host = DesktopValidation.PageHost(window)!;
                Require(host.ExtentWidth <= host.ViewportWidth + 1, $"The page scrolls sideways ({host.ExtentWidth:0} in {host.ViewportWidth:0}).");
                Require(TypographyValidation.Problems.Count == 0, "Typography problems: " + string.Join(" || ", TypographyValidation.Problems.Take(10)));
                Require(Named(window, "Start recording").ActualWidth > 0 && Named(window, "Analyze recording").ActualWidth > 0, "A primary action is not shown.");
                DesktopValidation.Capture(window, client, Path.Combine(directory, $"recorder-narrow-{theme}.png"));
                expander.IsExpanded = false;
                Log(directory, $"PASS: narrow window ({theme}, {window.Width:0}×{window.Height:0} DIPs, the minimum width): every state fits, nothing clipped or overlapping, no sideways scrolling, the actions stay reachable");
            });
            window.Width = originalSize.Width; window.Height = originalSize.Height;
            await Idle(window);
        }

        vm.IsDark = originalTheme;
        saved.SelectedItem = null;
        vm.SelectedPage = vm.Pages[0];
        await Idle(window);
        if (failures.Count == 0) Log(directory, "PASS: recorder validation complete");
        else throw new InvalidOperationException($"{failures.Count} Session Recorder check(s) failed: " + string.Join(" | ", failures));
    }
}
