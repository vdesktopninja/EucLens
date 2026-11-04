using System.Globalization;

namespace EucLens.Core;

/// <summary>
/// Time handling for history. Everything is stored, compared and plotted in UTC; local text is converted per instant, so a recording that crosses a
/// daylight-saving change shows the right wall-clock time for every event and stays in order.
/// </summary>
public static class HistoryTime
{
    public static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo? zone = null) => TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local);

    /// <summary>"14:23:18"; an instant whose local time occurs twice (the hour a clock is set back) carries its offset, so the two stay distinguishable.</summary>
    public static string Format(DateTimeOffset utc, TimeZoneInfo? zone = null, bool withDate = false, bool milliseconds = false)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var text = local.ToString((withDate ? "yyyy-MM-dd " : "") + (milliseconds ? "HH:mm:ss.fff" : "HH:mm:ss"), CultureInfo.InvariantCulture);
        return zone.IsAmbiguousTime(local.DateTime) ? $"{text} (UTC{FormatOffset(local.Offset)})" : text;
    }

    /// <summary>True for a zone that is UTC itself (no offset, no daylight saving): its times are labelled UTC, every other zone's local time.</summary>
    public static bool IsUtc(TimeZoneInfo zone) => zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime;

    /// <summary>"UTC" or "local": what the shown times are, as one word.</summary>
    public static string Word(TimeZoneInfo zone) => IsUtc(zone) ? "UTC" : "local";

    /// <summary>
    /// One sentence that says unambiguously what the shown times are: UTC, or local time with its offset. When the clocks change inside the recording (daylight saving), both offsets are
    /// named, because the same wall-clock time then occurs twice or not at all. The stored instants are UTC and are never reinterpreted: only the labels follow the zone.
    /// </summary>
    public static string Basis(TimeZoneInfo zone, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (IsUtc(zone)) return "Times are UTC.";
        var first = zone.GetUtcOffset(startUtc); var last = zone.GetUtcOffset(endUtc);
        var changed = first != last;
        // Two changes can cancel out between the ends: look at the span in half-hour steps (at most two weeks of it).
        if (!changed && endUtc > startUtc) for (var at = startUtc; at < endUtc && at < startUtc.AddDays(14); at += TimeSpan.FromMinutes(30)) if (zone.GetUtcOffset(at) != first) { changed = true; break; }
        return changed
            ? $"Times are local time: UTC{FormatOffset(first)} at the start of this recording, UTC{FormatOffset(last)} at the end (the clocks changed during it)."
            : $"Times are local time (UTC{FormatOffset(first)}).";
    }

    public static string FormatOffset(TimeSpan offset) => (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>Axis label for a chart whose x values are UTC ticks.</summary>
    public static string AxisLabel(double ticks, TimeZoneInfo? zone = null, bool seconds = true)
    {
        if (!(ticks > 0 && ticks <= DateTime.MaxValue.Ticks)) return "";
        var local = ToLocal(new DateTimeOffset(new DateTime((long)ticks, DateTimeKind.Utc)), zone);
        return local.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
    }

    public static string FormatSpan(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return $"{span.TotalSeconds:0.#} s";
    }
}
