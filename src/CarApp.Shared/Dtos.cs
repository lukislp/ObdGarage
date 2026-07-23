namespace CarApp.Shared;

/// <summary>Registrierung — nur mit gültigem Einladungscode (Heimserver, Plan 2.2a).</summary>
public sealed record RegisterRequest(string Email, string Password, string InviteCode);

public sealed record LoginRequest(string Email, string Password);

/// <summary>Bearer-Token (Base64Url aus 32 Zufallsbytes) + Nutzer-Id für die App.</summary>
public sealed record LoginResponse(string Token, Guid UserId);

/// <summary>Einheitliches Fehlerformat aller API-Endpunkte.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>
/// Antwort auf einen Push: wie viele Entitäten der Server übernommen hat und
/// wie viele (z.B. fremde Fahrzeuge) verworfen wurden.
/// </summary>
public sealed record SyncPushResponse(int Accepted, int Rejected);

/// <summary>
/// Pull-Antwort pro Entitätstyp: alle Änderungen seit <c>?since=</c> (inklusive
/// Soft-Deletes) plus die Serverzeit als Referenz für den nächsten Sync.
/// </summary>
public sealed class SyncEnvelope<T>
{
    public List<T> Items { get; set; } = [];
    public DateTimeOffset ServerTime { get; set; }
}
