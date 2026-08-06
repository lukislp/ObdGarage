namespace CarApp.Web.Services;

/// <summary>
/// Local app state: implicit local user (until the first login)
/// and the current sync settings (page /settings).
/// </summary>
public sealed class AppState
{
    /// <summary>Fixed implicit user — owner of all vehicles before the first login.</summary>
    public static readonly Guid LocalUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Active user id: initially <see cref="LocalUserId"/>, after the first login the
    /// server user id (the SyncManager migrates the OwnerUserId of local vehicles in the process).
    /// </summary>
    public Guid CurrentUserId { get; set; } = LocalUserId;

    public string? SyncServerUrl { get; set; }
    public string? SyncEmail { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}
