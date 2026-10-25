using System.Reflection;

namespace EucLens.Core;

/// <summary>The application version, read from the assembly so that the build (Directory.Build.props) is the only place it is written.</summary>
public static class AppVersion
{
    /// <summary>For example "0.4.4". Recordings are stamped with it.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}
