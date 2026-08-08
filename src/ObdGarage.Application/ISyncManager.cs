namespace ObdGarage.Application;

/// <summary>
/// Host-agnostic wrapper around <see cref="SyncService"/>: keeps auth state (token, server
/// URL, email, user id, last-sync timestamp) persisted across app restarts, and performs the
/// one-time local-vehicle-ownership migration on first login. Each host implements its own
/// persistence mechanism appropriate to its platform (e.g. a JSON file on Web, SecureStorage
/// on MAUI) - shared UI components (see ObdGarage.UI) only ever depend on this interface, never
/// on a concrete host implementation.
/// </summary>
public interface ISyncManager
{
    bool IsLoggedIn { get; }
    string? Email { get; }
    DateTimeOffset? LastSyncAt { get; }
    string ServerUrl { get; }

    Task<AuthResult> RegisterAsync(string serverUrl, string email, string password, string inviteCode);
    Task<AuthResult> LoginAsync(string serverUrl, string email, string password);
    Task<SyncResult> SyncNowAsync();
    Task LogoutAsync();
}
