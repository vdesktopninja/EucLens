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
