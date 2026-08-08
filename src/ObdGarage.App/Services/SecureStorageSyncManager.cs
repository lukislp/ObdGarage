using ObdGarage.Application;
using ObdGarage.Core;
using Microsoft.Maui.Storage;

namespace ObdGarage.App.Services;

/// <summary>
/// MAUI-side <see cref="ISyncManager"/>: keeps the token + server URL in
/// <see cref="SecureStorage"/> (OS keychain/keystore, survives app restarts) instead of a plain
/// file (see the Web project's <c>SyncManager</c> for the file-based equivalent). Behaviorally
/// identical otherwise - same HttpClient-per-server-URL, first-login vehicle-ownership migration.
/// </summary>
public sealed class SecureStorageSyncManager : ISyncManager
{
    private const string ServerUrlKey = "sync_server_url";
    private const string EmailKey = "sync_email";
    private const string TokenKey = "sync_token";
    private const string UserIdKey = "sync_user_id";
    private const string LastSyncAtKey = "sync_last_sync_at";

    private readonly ISyncRepository<Vehicle> _vehicles;
    private readonly ISyncRepository<AdapterProfile> _adapterProfiles;
    private readonly ISyncRepository<OdometerReading> _odometerReadings;
    private readonly ISyncRepository<Trip> _trips;
    private readonly ISyncRepository<MaintenanceTask> _maintenanceTasks;
    private readonly ISyncRepository<FuelEntry> _fuelEntries;
    private readonly ISyncRepository<Expense> _expenses;
    private readonly IObdSampleStore _sampleStore;
    private readonly IClock _clock;
    private readonly AppState _state;
    private readonly string _syncStateFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private HttpClient? _http;
    private SyncService? _service;
    private string? _serverUrl;
    private string? _email;
    private string? _token;
    private Guid? _userId;
    private DateTimeOffset? _lastSyncAt;

    public SecureStorageSyncManager(
        ISyncRepository<Vehicle> vehicles,
        ISyncRepository<AdapterProfile> adapterProfiles,
        ISyncRepository<OdometerReading> odometerReadings,
        ISyncRepository<Trip> trips,
        ISyncRepository<MaintenanceTask> maintenanceTasks,
        ISyncRepository<FuelEntry> fuelEntries,
        ISyncRepository<Expense> expenses,
        IObdSampleStore sampleStore,
        IClock clock,
        AppState state,
        string syncStateFilePath)
    {
        _vehicles = vehicles;
        _adapterProfiles = adapterProfiles;
        _odometerReadings = odometerReadings;
        _trips = trips;
        _maintenanceTasks = maintenanceTasks;
        _fuelEntries = fuelEntries;
        _expenses = expenses;
        _sampleStore = sampleStore;
        _clock = clock;
        _state = state;
        _syncStateFilePath = syncStateFilePath;

        // Matches the blocking-load-at-startup pattern MauiProgram.cs already uses for DB
        // migration/JSON import - SecureStorage's API is async-only, but ISyncManager's
        // properties (shared with the Web host) are synchronous, and nothing needs sync state
        // before construction returns anyway.
        LoadAuthAsync().GetAwaiter().GetResult();
    }

    public bool IsLoggedIn => _token is not null && _userId is not null;
    public string? Email => _email;
    public DateTimeOffset? LastSyncAt => _lastSyncAt;
    public string ServerUrl => _serverUrl ?? _state.SyncServerUrl ?? SyncService.DefaultServerUrl;

    public async Task<AuthResult> RegisterAsync(string serverUrl, string email, string password, string inviteCode)
    {
        await _lock.WaitAsync();
        try
        {
            var service = GetService(serverUrl);
            var result = await service.RegisterAsync(email, password, inviteCode);
            if (result.Success)
            {
                _serverUrl = serverUrl;
                _email = email;
                _state.SyncServerUrl = serverUrl;
                _state.SyncEmail = email;
                await SaveAuthAsync();
            }
            return result;
        }
        catch (Exception ex)
        {
            return new AuthResult(false, ex.Message);
        }
        finally { _lock.Release(); }
    }

    public async Task<AuthResult> LoginAsync(string serverUrl, string email, string password)
    {
        await _lock.WaitAsync();
        try
        {
            var service = GetService(serverUrl);
            var result = await service.LoginAsync(email, password);
            if (!result.Success)
                return result;

            // Same ordering as the Web host: only persist auth once the ownership migration
            // has actually succeeded, so IsLoggedIn never reports true for a failed login.
            try
            {
                await MigrateVehicleOwnerAsync(service.UserId);
            }
            catch (Exception ex)
            {
                return new AuthResult(false, ex.Message);
            }

            _serverUrl = serverUrl;
            _email = email;
            _token = service.Token;
            _userId = service.UserId;
            _state.SyncServerUrl = serverUrl;
            _state.SyncEmail = email;
            await SaveAuthAsync();

            return result;
        }
        catch (Exception ex)
        {
            return new AuthResult(false, ex.Message);
        }
        finally { _lock.Release(); }
    }

    public async Task<SyncResult> SyncNowAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsLoggedIn)
                return new SyncResult(false, 0, 0, "Nicht angemeldet — bitte zuerst einloggen.");

            var service = GetService(ServerUrl);
            var result = await service.SyncAsync();
            if (result.Success)
            {
                _lastSyncAt = _clock.UtcNow;
                _state.LastSyncAt = _lastSyncAt;
                await SaveAuthAsync();
            }
            return result;
        }
        catch (Exception ex)
        {
            return new SyncResult(false, 0, 0, ex.Message);
        }
        finally { _lock.Release(); }
    }

    public async Task LogoutAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _token = null;
            _userId = null;
            await SaveAuthAsync();
            _service = null;
            _http?.Dispose();
            _http = null;

            _state.CurrentUserId = AppState.LocalUserId;
        }
        finally { _lock.Release(); }
    }

    private async Task MigrateVehicleOwnerAsync(Guid serverUserId)
    {
        foreach (var vehicle in await _vehicles.GetAllIncludingDeletedAsync())
        {
            if (vehicle.OwnerUserId == AppState.LocalUserId && serverUserId != AppState.LocalUserId)
            {
                vehicle.OwnerUserId = serverUserId;
                vehicle.Touch();
                await _vehicles.UpsertAsync(vehicle);
            }
        }
        _state.CurrentUserId = serverUserId;
    }

    private SyncService GetService(string serverUrl)
    {
        var baseUrl = serverUrl.Trim().TrimEnd('/') + "/";
        if (_service is null || _http?.BaseAddress?.ToString() != baseUrl)
        {
            _http?.Dispose();
            _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
            _service = new SyncService(
                _http, _vehicles, _adapterProfiles, _odometerReadings, _trips,
                _maintenanceTasks, _fuelEntries, _expenses, _sampleStore, _clock, _syncStateFilePath)
            {
                TokenChanged = (token, userId) =>
                {
                    _token = token;
                    _userId = userId;
                    SaveAuthAsync().GetAwaiter().GetResult();
                },
            };
            if (_token is { } token && _userId is { } userId)
                _service.UseToken(token, userId);
        }
        return _service;
    }

    private async Task LoadAuthAsync()
    {
        _serverUrl = await SecureStorage.Default.GetAsync(ServerUrlKey);
        _email = await SecureStorage.Default.GetAsync(EmailKey);
        _token = await SecureStorage.Default.GetAsync(TokenKey);

        var userIdText = await SecureStorage.Default.GetAsync(UserIdKey);
        _userId = Guid.TryParse(userIdText, out var userId) ? userId : null;

        var lastSyncText = await SecureStorage.Default.GetAsync(LastSyncAtKey);
        _lastSyncAt = DateTimeOffset.TryParse(lastSyncText, out var lastSync) ? lastSync : null;

        _state.SyncServerUrl = _serverUrl;
        _state.SyncEmail = _email;
        _state.LastSyncAt = _lastSyncAt;
        if (_userId is { } uid)
            _state.CurrentUserId = uid; // Login survives the restart
    }

    /// <summary>
    /// SecureStorage has no "remove key" no-op-if-clear behavior across platforms consistently,
    /// so a null value is stored as an empty string rather than calling Remove - GetAsync
    /// returning "" is treated the same as null everywhere it's read back (see LoadAuthAsync's
    /// Guid.TryParse/DateTimeOffset.TryParse, which already fail closed to null on "").
    /// </summary>
    private async Task SaveAuthAsync()
    {
        await SecureStorage.Default.SetAsync(ServerUrlKey, _serverUrl ?? "");
        await SecureStorage.Default.SetAsync(EmailKey, _email ?? "");
        await SecureStorage.Default.SetAsync(TokenKey, _token ?? "");
        await SecureStorage.Default.SetAsync(UserIdKey, _userId?.ToString() ?? "");
        await SecureStorage.Default.SetAsync(LastSyncAtKey, _lastSyncAt?.ToString("O") ?? "");
    }
}
