using System.Windows;
using System.Windows.Threading;
using EucLens.App.ViewModels;
using EucLens.Core;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace EucLens.App;

/// <summary>What the tray menu can ask the application to do.</summary>
internal sealed record TrayActions(Action Open, Action OpenSettings, Func<Task> Exit);

/// <summary>
/// The notification-area icon. It shows the real operational state (see <see cref="TrayStateResolver"/>) and offers the same controls
/// as the application: open, start/pause/resume monitoring, start/stop recording, recordings folder, settings and exit. It holds
/// no window, page or chart; it reads <see cref="MonitoringService.Status"/> and acts through the view model's commands.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon icon = new();
    private readonly WinForms.ContextMenuStrip menu = new();
    private readonly Dictionary<string, Drawing.Icon> icons = [];
    private readonly MonitoringService monitoring;
    private readonly MainViewModel vm;
    private readonly Dispatcher dispatcher;
    private readonly TrayActions actions;
    private readonly WinForms.ToolStripMenuItem openItem = new("Open EUC Lens");
    private readonly WinForms.ToolStripMenuItem monitorItem = new("Pause Monitoring");
    private readonly WinForms.ToolStripMenuItem recordItem = new("Start Recording");
    private readonly WinForms.ToolStripMenuItem folderItem = new("Open Recordings Folder");
    private readonly WinForms.ToolStripMenuItem settingsItem = new("Settings");
    private readonly WinForms.ToolStripMenuItem exitItem = new("Exit EUC Lens");
    private bool refreshQueued;
    private bool disposed;

    public TrayIconService(MonitoringService monitoring, MainViewModel vm, Dispatcher dispatcher, TrayActions actions)
    {
        this.monitoring = monitoring; this.vm = vm; this.dispatcher = dispatcher; this.actions = actions;
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        menu.Items.AddRange([openItem, new WinForms.ToolStripSeparator(), monitorItem, recordItem, new WinForms.ToolStripSeparator(), folderItem, settingsItem, new WinForms.ToolStripSeparator(), exitItem]);
        menu.Opening += (_, _) => UpdateMenu();
        openItem.Click += (_, _) => actions.Open();
        monitorItem.Click += (_, _) => ToggleMonitoring();
        recordItem.Click += (_, _) => ToggleRecording();
        folderItem.Click += (_, _) => vm.Background.OpenRecordingsFolderCommand.Execute(null);
        settingsItem.Click += (_, _) => actions.OpenSettings();
        exitItem.Click += async (_, _) => await actions.Exit();
        icon.ContextMenuStrip = menu;
        // Left click and double click both open the window, the usual notification-area convention; right click opens the menu.
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) actions.Open(); };
        icon.DoubleClick += (_, _) => actions.Open();
        monitoring.StatusChanged += _ => QueueRefresh();
        Apply(TrayStateResolver.Resolve(monitoring.Status));
        icon.Visible = true;
    }

    public TrayAppearance? Appearance { get; private set; }
    public WinForms.NotifyIcon NotifyIcon => icon;
    public WinForms.ContextMenuStrip Menu => menu;
    public Drawing.Icon? CurrentIcon => icon.Icon;

    private void QueueRefresh()
    {
        if (disposed) return;
        lock (this) { if (refreshQueued) return; refreshQueued = true; }
        dispatcher.BeginInvoke(() =>
        {
            lock (this) refreshQueued = false;
            if (!disposed) Refresh();
        });
    }

    /// <summary>Re-reads the real state; also used by tests after they changed it.</summary>
    public void Refresh() => Apply(TrayStateResolver.Resolve(monitoring.Status));

    private void Apply(TrayAppearance appearance)
    {
        if (appearance == Appearance) return;
        Appearance = appearance;
        icon.Icon = IconFor(appearance.IconKey);
        icon.Text = appearance.Tooltip;
    }

    private Drawing.Icon IconFor(string key)
    {
        if (icons.TryGetValue(key, out var cached)) return cached;
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Tray/tray-{key}.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException($"Tray icon resource tray-{key}.ico is missing.");
        using var stream = resource.Stream;
        // The ICO holds 16, 20, 24 and 32 px; Windows' own small-icon size picks the entry that fits the display scaling.
        var created = new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        icons[key] = created;
        return created;
    }

    public void UpdateMenu()
    {
        var status = monitoring.Status;
        var state = monitoring.Coordinator.State;
        monitorItem.Text = state switch { MonitoringState.Running => "Pause Monitoring", MonitoringState.Paused => "Resume Monitoring", _ => "Start Monitoring" };
        recordItem.Text = status.IsRecording ? "Stop Recording" : "Start Recording";
        recordItem.Enabled = status.IsRecording || state == MonitoringState.Running;
    }

    public IReadOnlyList<(string Text, bool Enabled)> MenuState() =>
        [.. menu.Items.OfType<WinForms.ToolStripMenuItem>().Select(i => (i.Text ?? "", i.Enabled))];

    private void ToggleMonitoring()
    {
        switch (monitoring.Coordinator.State)
        {
            case MonitoringState.Running: monitoring.PauseMonitoring(); break;
            case MonitoringState.Paused: monitoring.ResumeMonitoring(); break;
            default: monitoring.StartMonitoring(); break;
        }
    }

    private void ToggleRecording()
    {
        if (monitoring.Status.IsRecording) { vm.StopRecordingCommand.Execute(null); return; }
        vm.StartRecordingCommand.Execute(null);
        // The window may be hidden, so a failure to start is shown here.
        if (!monitoring.Status.IsRecording) ShowBalloon("EUC Lens", vm.Status, WinForms.ToolTipIcon.Warning);
    }

    public void ShowNotification(NotificationRequest request) =>
        ShowBalloon(request.Title, request.Body, request.Kind == NotificationKind.Finding ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Error);

    /// <summary>The one-time explanation shown the first time the window is hidden to the tray.</summary>
    public void ShowHint() => ShowBalloon("EUC Lens is still running",
        "Monitoring" + (monitoring.Status.IsRecording ? " and the recording continue" : " continues") + " in the notification area. Right-click the icon to pause, stop recording or exit.", WinForms.ToolTipIcon.Info);

    private void ShowBalloon(string title, string text, WinForms.ToolTipIcon kind)
    {
        if (disposed) return;
        icon.BalloonTipTitle = title; icon.BalloonTipText = text; icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(8000);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Hide first so Windows removes the icon at once instead of leaving it until the mouse moves over it.
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        foreach (var cached in icons.Values) cached.Dispose();
    }
}
