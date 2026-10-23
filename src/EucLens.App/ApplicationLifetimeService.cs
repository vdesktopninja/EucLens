using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using Microsoft.Win32;

namespace EucLens.App;

/// <summary>
/// Separates the life of the application from the life of its window. Monitoring, evaluation and recording belong to the services
/// created at startup; this class only decides what hiding, minimizing, restoring, closing and exiting mean:
/// <list type="bullet">
/// <item>Minimize to tray (option): the window is hidden, nothing else changes.</item>
/// <item>Close to tray (option): the close button hides the window; only Exit from the tray menu ends the application.</item>
/// <item>Exit: finalizes an active recording, stops monitoring, removes the tray icon and ends the process.</item>
/// </list>
/// It also forwards Windows session events (lock, unlock, remote disconnect and reconnect, logoff) to the monitoring service.
/// </summary>
internal sealed class ApplicationLifetimeService : IDisposable
{
    private readonly Application app;
    private readonly MainViewModel vm;
    private readonly MonitoringService monitoring;
    private readonly IDesktopServices desktop;
    private readonly Func<Task> endProcess;
    private readonly bool unattended;
    private Window? window;
    private TrayIconService? tray;
    private int exiting;

    public ApplicationLifetimeService(Application app, MainViewModel vm, MonitoringService monitoring, IDesktopServices desktop, Func<Task> endProcess, bool unattended)
    {
        this.app = app; this.vm = vm; this.monitoring = monitoring; this.desktop = desktop; this.endProcess = endProcess; this.unattended = unattended;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        app.SessionEnding += OnSessionEnding;
    }

    public bool IsExiting => Volatile.Read(ref exiting) == 1;
    public BackgroundSettings Settings => vm.Background.Current;
    /// <summary>The window is hidden in the notification area (not merely minimized to the taskbar).</summary>
    public bool IsHiddenToTray => window is { IsVisible: false };
    public int TrayHints { get; private set; }

    public void Attach(Window mainWindow, TrayIconService trayIcon)
    {
        window = mainWindow; tray = trayIcon;
        mainWindow.StateChanged += (_, _) => OnStateChanged();
        mainWindow.IsVisibleChanged += (_, _) => UpdatePresentation();
        mainWindow.Closing += OnClosing;
        UpdatePresentation();
    }

    private void UpdatePresentation() => vm.SetPresenting(window is { IsVisible: true, WindowState: not WindowState.Minimized });

    private void OnStateChanged()
    {
        if (window is { WindowState: WindowState.Minimized } && Settings.MinimizeToTray && !IsExiting) HideToTray();
        UpdatePresentation();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsExiting) return;
        e.Cancel = true;
        if (Settings.CloseToTray) HideToTray(); else _ = ExitAsync();
    }

    /// <summary>Hides the window. Monitoring, evaluation and recording continue; the tray icon stays.</summary>
    public void HideToTray()
    {
        if (window is null || !window.IsVisible && window.WindowState != WindowState.Minimized) return;
        window.Hide();
        UpdatePresentation();
        if (!Settings.TrayHintShown)
        {
            TrayHints++;
            tray?.ShowHint();
            _ = vm.Background.MarkTrayHintShownAsync();
        }
    }

    /// <summary>Shows the window again with the current state; no service is recreated.</summary>
    public void Restore()
    {
        if (window is null || IsExiting) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Windows may refuse to move a background window to the front; this raises it without leaving it topmost.
        window.Topmost = true; window.Topmost = false;
        UpdatePresentation();
    }

    public void ShowSettings()
    {
        vm.SelectedPage = vm.Pages.OfType<SettingsPage>().Single();
        Restore();
    }

    /// <summary>
    /// Ends the application. An in-memory recording that was not exported and a running persisted recording are asked about first;
    /// an accepted exit finalizes the recording file before the process ends.
    /// </summary>
    public async Task ExitAsync()
    {
        if (IsExiting) return;
        if (!unattended)
        {
            if (vm.HasUnexportedSamples)
            {
                Restore();
                if (MessageBox.Show(window, "There are unexported recording samples. Exit and discard them? Choose No to return and export.", "Unsaved recording",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            else if (monitoring.Status.IsRecording && !desktop.ConfirmStopRecordingAndExit()) return;
        }
        if (Interlocked.Exchange(ref exiting, 1) == 1) return;
        try
        {
            await monitoring.ShutdownAsync(RecordingEndReason.AppExit);
            await endProcess();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            await endProcess();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var kind = e.Reason switch
        {
            SessionSwitchReason.SessionLock => (SessionEventKind?)SessionEventKind.Locked,
            SessionSwitchReason.SessionUnlock => SessionEventKind.Unlocked,
            SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect => SessionEventKind.RemoteDisconnected,
            SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect => SessionEventKind.RemoteReconnected,
            _ => null
        };
        if (kind is { } k) monitoring.OnSessionEvent(k);
    }

    /// <summary>Windows is logging off or shutting down: the recording is finalized before the process is terminated.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (Interlocked.Exchange(ref exiting, 1) == 1) return;
        try { monitoring.ShutdownAsync(RecordingEndReason.SessionEnding).Wait(TimeSpan.FromSeconds(4)); }
        catch (AggregateException) { /* nothing more can be done while Windows ends the session */ }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        app.SessionEnding -= OnSessionEnding;
    }
}
