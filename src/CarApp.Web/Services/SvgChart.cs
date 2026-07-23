using System.Globalization;
using System.Text;
using CarApp.Core;

namespace CarApp.Web.Services;

/// <summary>
/// Serverseitig gerenderter SVG-Linienchart für die Verlaufsansicht:
/// Linie in Akzentfarbe, Min/Max-Band bei aggregierten Samples,
/// dezente Gitterlinien, kleine graue Achsenbeschriftung. Kein externes Framework.
/// </summary>
public static class SvgChart
{
    private const int Width = 860;
    private const int Height = 320;
    private const int PadLeft = 56;
    private const int PadRight = 14;
    private const int PadTop = 14;
    private const int PadBottom = 30;

    public static string Render(IReadOnlyList<ObdSample> samples, string unit)
    {
        if (samples.Count == 0)
            return string.Empty;

        var inv = CultureInfo.InvariantCulture;
        var de = new CultureInfo("de-DE");

        var tMin = samples[0].Timestamp;
        var tMax = samples[^1].Timestamp;
        var spanTicks = Math.Max(1, (tMax - tMin).Ticks);

        var lo = samples.Min(s => s.MinValue ?? s.Value);
        var hi = samples.Max(s => s.MaxValue ?? s.Value);
        if (Math.Abs(hi - lo) < 1e-9) { hi += 1; lo -= 1; }
        var pad = (hi - lo) * 0.08;
        lo -= pad;
        hi += pad;

        double X(DateTimeOffset t) =>
            PadLeft + (double)(t - tMin).Ticks / spanTicks * (Width - PadLeft - PadRight);
        double Y(double v) =>
            PadTop + (1 - (v - lo) / (hi - lo)) * (Height - PadTop - PadBottom);

        string N(double v) => v.ToString("0.##", inv);

        var sb = new StringBuilder();
        sb.Append($"<svg class=\"chart\" viewBox=\"0 0 {Width} {Height}\" role=\"img\" ");
        sb.Append("preserveAspectRatio=\"xMidYMid meet\" xmlns=\"http://www.w3.org/2000/svg\">");

        // Horizontale Gitterlinien + y-Beschriftung
        const int yLines = 4;
        for (int i = 0; i <= yLines; i++)
        {
            var value = lo + (hi - lo) * i / yLines;
            var y = Y(value);
            sb.Append($"<line class=\"chart-grid\" x1=\"{PadLeft}\" y1=\"{N(y)}\" x2=\"{Width - PadRight}\" y2=\"{N(y)}\"/>");
            sb.Append($"<text class=\"chart-label\" x=\"{PadLeft - 6}\" y=\"{N(y + 3)}\" text-anchor=\"end\">");
            sb.Append(value.ToString(Math.Abs(hi - lo) < 10 ? "0.0" : "0", de));
            sb.Append("</text>");
        }

        // x-Beschriftung (4 Zeitmarken)
        var multiDay = (tMax - tMin) > TimeSpan.FromHours(24);
        const int xLabels = 4;
        for (int i = 0; i <= xLabels; i++)
        {
            var t = tMin + TimeSpan.FromTicks(spanTicks * i / xLabels);
            var x = X(t);
            sb.Append($"<line class=\"chart-grid\" x1=\"{N(x)}\" y1=\"{PadTop}\" x2=\"{N(x)}\" y2=\"{Height - PadBottom}\"/>");
            var label = t.ToLocalTime().ToString(multiDay ? "dd.MM. HH:mm" : "HH:mm", de);
            var anchor = i == 0 ? "start" : i == xLabels ? "end" : "middle";
            sb.Append($"<text class=\"chart-label\" x=\"{N(x)}\" y=\"{Height - PadBottom + 18}\" text-anchor=\"{anchor}\">{label}</text>");
        }

        // Min/Max-Band (nur wenn aggregierte Samples vorhanden sind)
        if (samples.Any(s => s.IsAggregated && s.MinValue is not null && s.MaxValue is not null))
        {
            var band = new StringBuilder("<path class=\"chart-band\" d=\"");
            var first = true;
            foreach (var s in samples)
            {
                var v = s.MaxValue ?? s.Value;
                band.Append(first ? 'M' : 'L').Append(N(X(s.Timestamp))).Append(' ').Append(N(Y(v)));
                first = false;
            }
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                var s = samples[i];
                var v = s.MinValue ?? s.Value;
                band.Append('L').Append(N(X(s.Timestamp))).Append(' ').Append(N(Y(v)));
            }
            band.Append("Z\"/>");
            sb.Append(band);
        }

        // Wertelinie (Avg bzw. Rohwert)
        var points = new StringBuilder();
        foreach (var s in samples)
            points.Append(N(X(s.Timestamp))).Append(',').Append(N(Y(s.Value))).Append(' ');
        sb.Append($"<polyline class=\"chart-line\" points=\"{points.ToString().TrimEnd()}\"/>");

        // Einheit oben links
        sb.Append($"<text class=\"chart-label\" x=\"{PadLeft - 6}\" y=\"{PadTop - 2}\" text-anchor=\"end\">{unit}</text>");

        sb.Append("</svg>");
        return sb.ToString();
    }
}
