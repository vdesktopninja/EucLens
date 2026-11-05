using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace EucLens.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true; Refresh();
        try { await execute(); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record Choice<T>(T Value, string Label);
public sealed record MetricCard(string Label, string Value, string Detail);

/// <summary>
/// One tile of a compact overview: a label, the value that matters, a short qualifier and optionally the severity icon (<see cref="Icon"/> is a SeverityIcon tag, empty for none).
/// Value, qualifier and icon can change while the tile is shown (a tile that waits for an analysis).
/// </summary>
public sealed class HistoryTile : ObservableObject
{
    private string value, detail, icon;

    public HistoryTile(string label, string value, string detail, string icon, string toolTip) { Label = label; this.value = value; this.detail = detail; this.icon = icon; ToolTip = toolTip; }

    public string Label { get; }
    public string Value { get => value; private set => Set(ref this.value, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public string Icon { get => icon; private set { if (Set(ref icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => icon.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public string ToolTip { get; }
    public bool HasToolTip => ToolTip.Length > 0;
    public string AutomationName => Detail.Length > 0 ? $"{Label}: {Value}, {Detail}" : $"{Label}: {Value}";

    internal void Set(string newValue, string newDetail, string newIcon)
    {
        Value = newValue; Detail = newDetail; Icon = newIcon;
        Raise(nameof(HasDetail)); Raise(nameof(AutomationName));
    }
}
public sealed record AdvancedMetricRow(string Category, string Name, string Value, string Unit, string Scope, string Status,
    string Source, string Timestamp, string Id);
public abstract class PageViewModel(MainViewModel monitor, string name, string glyph)
{
    public MainViewModel Monitor { get; } = monitor;
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
}
public sealed class DashboardPage(MainViewModel monitor) : PageViewModel(monitor, "Dashboard", "\uE80F");
public sealed class DiagnosticsPage(MainViewModel monitor) : PageViewModel(monitor, "Diagnostics", "\uE9D9");
public sealed class RecorderPage(MainViewModel monitor) : PageViewModel(monitor, "Session Recorder", "\uE7C8");
public sealed class SessionHistoryPage(MainViewModel monitor) : PageViewModel(monitor, "Session History", "\uE81C");
public sealed class SystemPage(MainViewModel monitor) : PageViewModel(monitor, "System Information", "\uE770");
public sealed class AdvancedPage(MainViewModel monitor) : PageViewModel(monitor, "Advanced Metrics", "\uE9F9");
public sealed class SettingsPage(MainViewModel monitor) : PageViewModel(monitor, "Settings", "\uE713");
public sealed class AboutPage(MainViewModel monitor) : PageViewModel(monitor, "About", "\uE946")
{
    public AboutViewModel About => Monitor.About;
}
