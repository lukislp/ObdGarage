using System.Globalization;
using System.Text;
using CarApp.Core;

namespace CarApp.Web;

/// <summary>
/// Pure GET endpoints that are not Blazor component rendering: photo delivery
/// and CSV export. All mutations now run as interactive Blazor Server methods
/// directly in the respective components (see Components/Pages, Components/VehicleTabs).
/// </summary>
public static class Endpoints
{
    public static void MapCarAppEndpoints(this WebApplication app, string photosDir)
    {
        app.MapGet("/photos/{id:guid}", (Guid id) =>
        {
            var file = Directory.Exists(photosDir)
                ? Directory.EnumerateFiles(photosDir, id.ToString("N") + ".*").FirstOrDefault()
                : null;
            if (file is null)
                return Results.NotFound();
            var contentType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
            return Results.File(file, contentType);
        });

        app.MapGet("/api/vehicles/{id:guid}/trips.csv", async (Guid id, IRepository<Trip> trips) =>
        {
            var all = (await trips.GetAllAsync())
                .Where(t => t.VehicleId == id)
                .OrderBy(t => t.StartedAt)
                .ToList();

            var de = new CultureInfo("de-DE");
            var sb = new StringBuilder();
            sb.AppendLine("Start;Ende;Dauer (min);Distanz (km);Kategorie;Notiz");
            foreach (var t in all)
            {
                var duration = t.EndedAt is { } end ? (end - t.StartedAt).TotalMinutes : 0;
                sb.Append(t.StartedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", de)).Append(';');
                sb.Append(t.EndedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", de) ?? "").Append(';');
                sb.Append(duration.ToString("0", de)).Append(';');
                sb.Append(t.DistanceKm.ToString("0.00", de)).Append(';');
                sb.Append(CategoryName(t.Category)).Append(';');
                sb.Append('"').Append((t.Note ?? "").Replace("\"", "\"\"")).Append('"');
                sb.AppendLine();
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "fahrten.csv");
        });
    }

    public static string CategoryName(TripCategory category) => category switch
    {
        TripCategory.Business => "Geschäftlich",
        TripCategory.Commute => "Arbeitsweg",
        _ => "Privat",
    };
}
