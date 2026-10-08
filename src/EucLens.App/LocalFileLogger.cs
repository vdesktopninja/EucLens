using System.IO;
using Microsoft.Extensions.Logging;

namespace EucLens.App;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new LocalLogger(this, categoryName);
    public void Dispose() { }
    private void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(DesktopServices.DataDirectory);
                var path = Path.Combine(DesktopServices.DataDirectory, "euc-lens.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }
    private sealed class LocalLogger(LocalFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write($"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)} {exception}");
        }
    }
}
