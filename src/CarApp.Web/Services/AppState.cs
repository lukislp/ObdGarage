namespace CarApp.Web.Services;

/// <summary>
/// Lokaler App-Zustand: impliziter lokaler Nutzer (bis zum ersten Login)
/// und die aktuellen Sync-Einstellungen (Seite /settings).
/// </summary>
public sealed class AppState
{
    /// <summary>Fester impliziter Nutzer — Besitzer aller Fahrzeuge vor dem ersten Login.</summary>
    public static readonly Guid LocalUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Aktive Nutzer-Id: anfangs <see cref="LocalUserId"/>, nach dem ersten Login die
    /// Server-UserId (der SyncManager migriert dabei die OwnerUserId der lokalen Fahrzeuge).
    /// </summary>
    public Guid CurrentUserId { get; set; } = LocalUserId;

    public string? SyncServerUrl { get; set; }
    public string? SyncEmail { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}
