using System.Globalization;
using System.Text;

namespace EucLens.Core;

/// <summary>
/// Field-level safety for CSV that people open in spreadsheets. Two separate concerns, kept apart:
/// <list type="number">
/// <item><b>Structure</b> (RFC 4180): a field with a comma, a double quote, CR, LF or surrounding whitespace is enclosed in double quotes and inner quotes are doubled.</item>
/// <item><b>Formula protection</b>: recorded strings (annotations, counter units, titles, session metadata) are untrusted. A field that a spreadsheet could read as a formula
/// gets one leading apostrophe, which spreadsheets treat as a text marker. Surrounding a formula with quotes does not protect it: the quotes are removed by the CSV reader.</item>
/// </list>
/// A text field is protected when it begins with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c> (also their full-width forms), when it begins with a tab, carriage return or
/// line feed, or when those characters follow leading whitespace, control or invisible format characters (a hidden prefix such as space, tab, no-break space, zero-width space or
/// a byte-order mark). Numbers written by the exporter are not text and are never prefixed: a negative number is a number.
/// The apostrophe is the one representation change; the JSON export keeps every string exactly as recorded.
/// </summary>
public static class CsvField
{
    /// <summary>Spreadsheets cap a cell at 32,767 characters; longer text is cut here and says so. The JSON export is never truncated.</summary>
    public const int MaximumTextLength = 32_000;
    public const string TruncationMarker = "…[truncated]";

    private static readonly HashSet<char> Triggers = ['=', '+', '-', '@', '＝', '＋', '－', '＠'];

    private static bool IsInvisible(char c) =>
        char.IsWhiteSpace(c) || char.IsControl(c) || c == ' ' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>True when a spreadsheet could interpret this text as a formula, even behind invisible leading characters.</summary>
    public static bool NeedsFormulaGuard(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] is '\t' or '\r' or '\n') return true;
        foreach (var c in value)
        {
            if (IsInvisible(c)) continue;
            return Triggers.Contains(c);
        }
        return false;
    }

    /// <summary>Only the formula protection: the value, with one leading apostrophe when it needs it.</summary>
    public static string Guard(string value) => NeedsFormulaGuard(value) ? "'" + value : value;

    /// <summary>An untrusted text field, ready to place between delimiters: sanitized, protected against formulas, bounded, and quoted when its content requires it.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = Sanitize(value);
        text = Guard(text);
        if (text.Length > MaximumTextLength)
        {
            var cut = MaximumTextLength - TruncationMarker.Length;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut] + TruncationMarker;
        }
        return Quote(text);
    }

    /// <summary>RFC 4180 quoting only; the caller has already protected the value.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return "";
        var needs = value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
        return needs ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>Control characters other than tab, CR and LF cannot be shown in a cell and can confuse parsers: they become a space. Lone surrogates become U+FFFD so the file stays valid UTF-8.</summary>
    private static string Sanitize(string value)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var replacement = c is '\t' or '\r' or '\n' ? (char?)null
                : char.IsControl(c) ? ' '
                : char.IsHighSurrogate(c) && !(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) ? '�'
                : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(value[i - 1])) ? '�' : (char?)null;
            if (replacement is null) { builder?.Append(c); continue; }
            builder ??= new StringBuilder(value, 0, i, value.Length);
            builder.Append(replacement.Value);
        }
        return builder?.ToString() ?? value;
    }

    /// <summary>The same text with every unpaired surrogate replaced by U+FFFD, so it can be encoded as UTF-8 (JSON writers refuse invalid UTF-16).</summary>
    public static string ToValidUtf16(string value)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var bad = char.IsHighSurrogate(c) ? !(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(value[i - 1]));
            if (!bad) { builder?.Append(c); continue; }
            builder ??= new StringBuilder(value, 0, i, value.Length);
            builder.Append('\uFFFD');
        }
        return builder?.ToString() ?? value;
    }

    /// <summary>A number the exporter itself produced: invariant, round-trippable, empty when it is not a finite value (never zero, never NaN).</summary>
    public static string Number(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "";
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
