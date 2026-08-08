using System.Globalization;
using ObdGarage.Application;
using ObdGarage.Obd.Pids;

namespace ObdGarage.UI;

/// <summary>Formatting/parsing helpers for the UI (German display, robust form parsing).</summary>
public static class Fmt
{
    private static readonly CultureInfo De = new("de-DE");

    /// <summary>Decimal places per PID key for display.</summary>
    public static int DecimalsFor(string pidKey) => pidKey switch
    {
        "module_voltage" => 1,
        "maf" => 1,
        "odometer" => 1,
        _ => 0,
    };

    public static string Reading(ObdReading reading)
    {
        var pid = StandardPids.ByKey(reading.PidKey);
        var value = reading.Value.ToString("N" + DecimalsFor(reading.PidKey), De);
        return pid is null ? value : $"{value} {pid.Unit}";
    }

    public static string Km(double? km) =>
        km is { } v ? v.ToString("N0", De) + " km" : "–";

    public static string Duration(TimeSpan span)
    {
        // TimeSpan.Minutes/.Seconds carry the same sign as the whole span (e.g. -90s has
        // Minutes == -1 AND Seconds == -30, not a "wrapped" positive value), so a negative
        // span - EndedAt ever preceding StartedAt, e.g. from a last-write-wins sync merge or
        // a mid-trip clock adjustment - would render as a nonsensical double-negative string.
        // A duration is conceptually never negative, so treat one as "unknown" (zero).
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : $"{span.Minutes} min {span.Seconds:00} s";
    }

    /// <summary>Parses numbers from form fields — accepts both period and comma.</summary>
    public static double? ParseDouble(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static decimal? ParseDecimal(string? raw) =>
        ParseDouble(raw) is { } d ? (decimal)d : null;

    public static int? ParseInt(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static DateOnly? ParseDate(string? raw) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    public static DateTimeOffset? ParseDateTimeLocal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var dt)
            ? new DateTimeOffset(dt)
            : null;
    }
}
