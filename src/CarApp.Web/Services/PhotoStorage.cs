namespace CarApp.Web.Services;

/// <summary>Verzeichnis für Fahrzeugfotos (data/photos) — gemeinsam genutzt von VehicleForm und /photos/{id}.</summary>
public sealed record PhotoStorage(string Directory);
