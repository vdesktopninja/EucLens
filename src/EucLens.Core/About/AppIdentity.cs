using System.Reflection;
using System.Runtime.InteropServices;

namespace EucLens.Core;

/// <summary>
/// Who the application is, read back from the metadata the build wrote into the assembly (Directory.Build.props): the same values Windows Explorer shows under
/// Properties → Details, so the About page and the executable cannot disagree. Nothing here is typed a second time.
/// </summary>
public sealed record AppIdentity(string Product, string Description, string Author, string Publisher, string Copyright, string License, string RepositoryUrl, string Website, string Version, string? Commit)
{
    /// <summary>The commit as shown to people: the first seven characters, or null when the build did not record one (a local build).</summary>
    /// <summary>Author and brand as one line: "Dominik Jakubowski, VDI Ninja".</summary>
    public string AuthorLine => $"{Author}, {Publisher}";

    public string? ShortCommit => Commit is null ? null : Commit[..7];

    /// <summary>The version, with the abbreviated commit when the build recorded one: "1.2.3 (727acd1)".</summary>
    public string VersionWithBuild => ShortCommit is null ? Version : $"{Version} ({ShortCommit})";

    /// <summary>What the application runs on: stated, not detected, because it is what the build supports. The architecture is the running process's own.</summary>
    public string Platform => $"Windows 10 (version 2004) or later and Windows 11, {ArchitectureName(RuntimeInformation.ProcessArchitecture)}";

    public static string ArchitectureName(Architecture architecture) => architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "ARM64", Architecture.X86 => "x86", _ => architecture.ToString() };

    public static AppIdentity Current { get; } = Read(typeof(AppIdentity).Assembly);

    /// <summary>Reads the identity of an assembly built with the shared properties; a missing value falls back to a neutral word instead of failing.</summary>
    public static AppIdentity Read(Assembly assembly)
    {
        string Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
        var commit = Metadata("CommitSha");
        return new(
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "EUC Lens",
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "",
            Metadata("Author"),
            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "",
            assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "",
            Metadata("License"),
            Metadata("RepositoryUrl"),
            Metadata("Website"),
            AppVersion.Current,
            IsCommit(commit) ? commit.ToLowerInvariant() : null);
    }

    /// <summary>A commit is 7 to 40 hexadecimal characters; anything else is not displayed.</summary>
    public static bool IsCommit(string? text) => text is { Length: >= 7 and <= 40 } && text.All(Uri.IsHexDigit);
}
