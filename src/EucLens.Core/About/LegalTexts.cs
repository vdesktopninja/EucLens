using System.Reflection;

namespace EucLens.Core;

/// <summary>The license, the third-party notices and the release notes of this version: the repository's own files, embedded in the assembly by the build.</summary>
public static class LegalTexts
{
    public static string License => Read("EucLens.LICENSE.txt");
    public static string ThirdPartyNotices => Read("EucLens.THIRD_PARTY_NOTICES.md");
    public static string Changelog => Read("EucLens.CHANGELOG.md");

    /// <summary>The changelog section of a version ("## 0.5.1 — …" up to the next section), or a plain statement when this build has none.</summary>
    public static string ReleaseNotesFor(string version) => ReleaseNotesFor(Changelog, version);

    public static string ReleaseNotesFor(string changelog, string version)
    {
        var lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal) && l.AsSpan(3).StartsWith(version, StringComparison.Ordinal) && (l.Length == 3 + version.Length || !char.IsDigit(l[3 + version.Length]) && l[3 + version.Length] != '.'));
        if (start < 0) return $"The release notes of version {version} are not included in this build.";
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]).Trim();
    }

    private static string Read(string name)
    {
        using var stream = typeof(LegalTexts).Assembly.GetManifestResourceStream(name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
