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
public sealed record AdvancedMetricRow(string Category, string Name, string Value, string Unit, string Scope, string Status,
    string Source, string Timestamp, string Id);
/// <summary>Stable view item for a finding so per-second evidence updates keep the expanded state.</summary>
public sealed class FindingItem(FindingCard card) : ObservableObject
{
    private FindingCard card = card;
    private bool expanded;
    public string Key { get; } = card.RuleId + "|" + card.DetectedKey;
    public FindingCard Card { get => card; set { if (Set(ref card, value)) Raise(string.Empty); } }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public string Severity => card.Severity;
    public string Title => card.Title;
    public string Domain => card.Domain;
    public string Description => card.Description;
    public string Metrics => card.Metrics;
    public string Window => card.Window;
    public string Timeline => card.Timeline;
    public string Evidence => card.Evidence;
    public string DecisionRule => card.DecisionRule;
    public string Causes => card.Causes;
    public string Actions => card.Actions;
    public string Limitations => card.Limitations;
    public string RuleId => card.RuleId;
}
public sealed record HealthTile(string Domain, string State, string Coverage, string Evidence, string CoverageNote);
public sealed record HypothesisCard(string Statement, string Confidence, string Verification);
/// <summary>Display form of a finding: compact header plus expandable evidence, rule, causes, actions and limitations.</summary>
public sealed record FindingCard(string RuleId, string Severity, string Title, string Domain, string Description, string Metrics, string Window,
    string Timeline, string Evidence, string DecisionRule, string Causes, string Actions, string Limitations, bool IsActive, string DetectedKey)
{
    public static FindingCard From(EucLens.Core.DiagnosticFinding f)
    {
        static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var severity = f.IsActive ? f.Severity switch { EucLens.Core.Severity.Critical => "CRITICAL", EucLens.Core.Severity.Warning => "WARNING", _ => "INFO" } : "RECOVERED";
        var domain = f.Domain switch
        {
            EucLens.Core.DiagnosticDomain.Guest => "VDI guest", EucLens.Core.DiagnosticDomain.Gpu => "GPU", EucLens.Core.DiagnosticDomain.DataQuality => "Data quality",
            _ => f.Domain.ToString()
        };
        var window = f.ObservationSeconds > 0 ? $"{f.ObservationSeconds:0} s window · {f.ValidSamples} valid of {f.TotalSamples} samples" : "Inventory-based";
        var timeline = f.IsActive ? $"Detected {Time(f.DetectedAt)} · last confirmed {Time(f.LastConfirmedAt)}"
            : $"Detected {Time(f.DetectedAt)} · ended {Time(f.RecoveredAt!.Value)} — {f.RecoveryReason}";
        return new(f.RuleId, severity, f.Title, domain, f.Description,
            string.Join(" · ", f.Evidence.Take(3).Select(e => $"{e.Metric}: {e.Value}")), window, timeline,
            string.Join(Environment.NewLine, f.Evidence.Select(e => $"• {e.Metric}: {e.Value}{(e.Detail is null ? "" : $" ({e.Detail})")}")),
            f.DecisionRule, string.Join(Environment.NewLine, f.PossibleCauses.Select(c => $"• {c}")),
            string.Join(Environment.NewLine, f.RecommendedActions.Select((a, i) => $"{i + 1}. {a}")), f.Limitations, f.IsActive,
            f.DetectedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
public abstract class PageViewModel(MainViewModel monitor, string name, string glyph)
{
    public MainViewModel Monitor { get; } = monitor;
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
}
public sealed class DashboardPage(MainViewModel monitor) : PageViewModel(monitor, "Dashboard", "\uE80F");
public sealed class DiagnosticsPage(MainViewModel monitor) : PageViewModel(monitor, "Diagnostics", "\uE9D9");
public sealed class RecorderPage(MainViewModel monitor) : PageViewModel(monitor, "Session Recorder", "\uE7C8");
public sealed class SystemPage(MainViewModel monitor) : PageViewModel(monitor, "System Information", "\uE770");
public sealed class AdvancedPage(MainViewModel monitor) : PageViewModel(monitor, "Advanced Metrics", "\uE9F9");
public sealed class SettingsPage(MainViewModel monitor) : PageViewModel(monitor, "Settings", "\uE713");
