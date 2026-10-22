using EucLens.Core;

namespace EucLens.App.Presentation;

/// <summary>The severity/state vocabulary of the Diagnostics page; each kind has one vector icon and one colour.</summary>
public enum SeverityKind { Info, Warning, Critical, Healthy, Recovered, NoData }

/// <summary>
/// Maps diagnostic states to <see cref="SeverityKind"/>. WPF-free so the mapping is unit-tested. The kind name is the value of
/// <c>Tag</c> on the <c>SeverityIcon</c> style and the suffix of the <c>SeverityGeometry*</c> resource in Foundations.xaml.
/// Classification is not decided here: the engine's severity and the finding's active state are only translated.
/// </summary>
public static class SeverityKinds
{
    public static readonly IReadOnlyList<SeverityKind> All = Enum.GetValues<SeverityKind>();

    /// <summary>Icon of a finding row: a recovered finding is always "Recovered", otherwise its engine severity.</summary>
    public static SeverityKind ForFinding(Severity severity, bool isActive) =>
        !isActive ? SeverityKind.Recovered : severity switch { Severity.Critical => SeverityKind.Critical, Severity.Warning => SeverityKind.Warning, _ => SeverityKind.Info };

    /// <summary>Icon of a health state label ("Healthy", "Warning", "Critical", "Insufficient Data") or finding state text.</summary>
    public static SeverityKind ForState(string? state) => state switch
    {
        "Healthy" => SeverityKind.Healthy,
        "Warning" => SeverityKind.Warning,
        "Critical" => SeverityKind.Critical,
        "Recovered" => SeverityKind.Recovered,
        "Info" or "Information" or "Informational" => SeverityKind.Info,
        _ => SeverityKind.NoData
    };

    public static string GeometryKey(SeverityKind kind) => "SeverityGeometry" + kind;
}
