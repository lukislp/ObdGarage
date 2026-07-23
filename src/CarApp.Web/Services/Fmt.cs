using System.Globalization;
using CarApp.Application;
using CarApp.Obd.Pids;

namespace CarApp.Web.Services;

/// <summary>Formatierung/Parsing-Helfer für die UI (deutsche Anzeige, robustes Form-Parsing).</summary>
public static class Fmt
{
    private static readonly CultureInfo De = new("de-DE");

    /// <summary>Nachkommastellen pro PID-Key für die Anzeige.</summary>
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

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : $"{span.Minutes} min {span.Seconds:00} s";

    /// <summary>Parst Zahlen aus Formularfeldern — akzeptiert Punkt und Komma.</summary>
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
