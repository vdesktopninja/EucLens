using System.Globalization;
using System.Text.RegularExpressions;

namespace EucLens.Core;

/// <summary>
/// A semantic version (semver.org 2.0.0) as a value that compares by its numbers, not by its text: 0.10.0 is newer than 0.9.0, and 1.0.0-rc.1 is older than 1.0.0.
/// Build metadata ("+abc") is accepted and ignored in comparisons. A leading "v" is accepted because release tags carry it.
/// </summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    [GeneratedRegex(@"\Av?(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})(?:-([0-9A-Za-z-]{1,24}(?:\.[0-9A-Za-z-]{1,24}){0,7}))?(?:\+[0-9A-Za-z.-]{1,40})?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();

    private readonly string[] pre;
    private ReleaseVersion(int major, int minor, int patch, string[] pre) { Major = major; Minor = minor; Patch = patch; this.pre = pre; }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => pre.Length > 0;

    /// <summary>"1.2.3" or "1.2.3-rc.1" (no "v", no build metadata).</summary>
    public override string ToString() => pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{string.Join('.', pre)}";

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = Zero;
        if (text is not { Length: > 0 and <= 80 }) return false;
        Match match;
        try { match = Pattern().Match(text); } catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;
        var identifiers = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        // A numeric identifier has no leading zero (semver §9); "-" alone is not an identifier.
        if (identifiers.Any(i => i.All(char.IsAsciiDigit) && i.Length > 1 && i[0] == '0')) return false;
        version = new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), identifiers);
        return true;
    }

    public static ReleaseVersion Zero { get; } = new(0, 0, 0, []);

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        // A version with a pre-release part is older than the same version without one.
        if (pre.Length == 0 || other.pre.Length == 0) return other.pre.Length.CompareTo(pre.Length);
        for (var i = 0; i < Math.Min(pre.Length, other.pre.Length); i++)
        {
            var a = pre[i]; var b = other.pre[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            c = (an, bn) switch
            {
                (true, true) => CompareNumbers(a, b),
                (true, false) => -1,       // numeric identifiers are older than alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0) return c < 0 ? -1 : 1;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }

    // Digits only, so compare by length and then text: no overflow however long the identifier.
    private static int CompareNumbers(string a, string b) { var x = a.TrimStart('0'); var y = b.TrimStart('0'); return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y); }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', pre));
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
}
