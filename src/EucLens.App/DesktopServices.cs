using System.IO;
using System.Text.Json;
using System.Windows;
using EucLens.Core;
using Microsoft.Win32;

namespace EucLens.App;

public sealed class DesktopServices : IDesktopServices
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EucLens");
    public string? SelectExportPath(bool json)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export DEMO recording", FileName = $"EucLens-demo-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = json ? "JSON recording (*.json)|*.json" : "CSV metrics (*.csv)|*.csv",
            DefaultExt = json ? ".json" : ".csv", AddExtension = true, OverwritePrompt = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public bool ConfirmNewRecording() => MessageBox.Show(Application.Current.MainWindow,
        "Start a new recording and discard the current in-memory samples? Export first to keep them.",
        "New demo recording", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void ApplyTheme(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        dictionaries[0] = theme;
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
        var result = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(path));
        if (result is not null && (!Enum.IsDefined(result.Protocol) || !Enum.IsDefined(result.Scenario) || !Enum.IsDefined(result.Workload)))
            throw new InvalidDataException("Unknown demo preference value.");
        return result;
    }
}
