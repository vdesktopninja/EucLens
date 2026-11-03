using System.IO;
using System.Text.Json;
using System.Windows;
using EucLens.Core;
using Microsoft.Win32;

namespace EucLens.App;

public sealed class DesktopServices : IDesktopServices
{
    /// <summary>Smoke tests run unattended; a modal confirmation would block them.</summary>
    public bool AutoConfirmNewRecording { get; init; }
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EucLens");
    /// <summary>The services of the smoke run, for the validation that has to answer the save dialog; null in normal use.</summary>
    internal static DesktopServices? SmokeInstance { get; set; }
    /// <summary>Set by the smoke test so unattended runs never wait for a save dialog; receives (json, file stem) and returns the path, or null to cancel.</summary>
    public Func<bool, string, string?>? ExportPathProvider { get; set; }
    public string? SelectExportPath(bool json, string title, string fileStem)
    {
        if (ExportPathProvider is { } provider) return provider(json, fileStem);
        var dialog = new SaveFileDialog
        {
            Title = title, FileName = fileStem,
            Filter = json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            DefaultExt = json ? ".json" : ".csv", AddExtension = true, OverwritePrompt = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public string? SelectExportPath(bool json)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export recording", FileName = $"EucLens-recording-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = json ? "JSON recording (*.json)|*.json" : "CSV metrics (*.csv)|*.csv",
            DefaultExt = json ? ".json" : ".csv", AddExtension = true, OverwritePrompt = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public bool ConfirmNewRecording() => AutoConfirmNewRecording || MessageBox.Show(Application.Current.MainWindow,
        "Start a new recording and discard the current in-memory samples? Export first to keep them.",
        "New recording", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void ApplyTheme(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        dictionaries[0] = theme;
    }
    /// <summary>Set by the smoke test so unattended runs never wait for a dialog.</summary>
    public bool AutoConfirmExit { get; init; }
    public string? SelectFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the recordings folder", InitialDirectory = initialDirectory ?? "", Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
    public bool OpenPath(string path, bool selectFile = false)
    {
        try
        {
            var info = selectFile ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"");
            info.UseShellExecute = true;
            System.Diagnostics.Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }
    public bool ConfirmStopRecordingAndExit() => AutoConfirmExit || MessageBox.Show(Application.Current.MainWindow,
        "A recording is running. Stop it, save the file and exit EUC Lens?\n\nChoose No to keep EUC Lens and the recording running.",
        "Exit EUC Lens", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public bool CopyText(string text)
    {
        // Another process can hold the clipboard open; report failure instead of throwing.
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }
    public async Task SavePreferencesAsync(Preferences preferences)
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "preferences.json");
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<Preferences?> LoadPreferencesAsync()
    {
        var path = Path.Combine(DataDirectory, "preferences.json");
        if (!File.Exists(path)) return null;
        // Properties of earlier versions (data source, demo protocol and scenario, workload) are ignored: see Preferences.
        return JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(path));
    }
}
