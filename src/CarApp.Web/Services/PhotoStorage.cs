namespace CarApp.Web.Services;

/// <summary>Directory for vehicle photos (data/photos) — shared by VehicleForm and /photos/{id}.</summary>
public sealed record PhotoStorage(string Directory);
