using System.IO;

namespace EucLens.App;

/// <summary>
/// Progress log of the --smoke-test run. It is uploaded with the CI evidence even when the run fails, hangs or is killed,
/// so the phase and the last completed step are always known.
/// </summary>
internal static class SmokeLog
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "smoke-progress.txt");

    public static void Write(string message)
    {
        try { lock (Gate) File.AppendAllText(Path, $"{DateTime.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (IOException) { /* logging must never fail the validation it documents */ }
        catch (UnauthorizedAccessException) { }
    }
}
