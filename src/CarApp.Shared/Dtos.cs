namespace CarApp.Shared;

/// <summary>Registration — only with a valid invite code (home server, plan 2.2a).</summary>
public sealed record RegisterRequest(string Email, string Password, string InviteCode);

public sealed record LoginRequest(string Email, string Password);

/// <summary>Bearer token (Base64Url from 32 random bytes) + user ID for the app.</summary>
public sealed record LoginResponse(string Token, Guid UserId);

/// <summary>Uniform error format for all API endpoints.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>
/// Response to a push: how many entities the server accepted and
/// how many (e.g. vehicles belonging to another user) were rejected.
/// </summary>
public sealed record SyncPushResponse(int Accepted, int Rejected);

/// <summary>
/// Pull response per entity type: all changes since <c>?since=</c> (including
/// soft-deletes) plus the server time as a reference for the next sync.
/// </summary>
public sealed class SyncEnvelope<T>
{
    public List<T> Items { get; set; } = [];
    public DateTimeOffset ServerTime { get; set; }
}
