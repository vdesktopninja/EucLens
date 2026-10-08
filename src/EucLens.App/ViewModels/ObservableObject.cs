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
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await execute(); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record Choice<T>(T Value, string Label);
public sealed record MetricCard(string Label, string Value, string Detail);
public sealed record DiagnosticCard(string Severity, string Title, string Description, string Evidence, string Cause, string Action);
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
public sealed class SettingsPage(MainViewModel monitor) : PageViewModel(monitor, "Settings", "\uE713");
