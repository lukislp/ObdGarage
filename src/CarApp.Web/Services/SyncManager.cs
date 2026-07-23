using System.Text.Json;
using CarApp.Application;
using CarApp.Core;

namespace CarApp.Web.Services;

/// <summary>
/// Web-seitige Hülle um <see cref="SyncService"/>: hält Token + Server-URL in
/// <c>data/sync-auth.json</c> (übersteht App-Neustarts), erzeugt den HttpClient
/// passend zur Server-URL und stellt beim ersten Login die OwnerUserId der lokal
/// angelegten Fahrzeuge auf die Server-UserId um — sonst verwirft der Server sie beim Push.
/// </summary>
public sealed class SyncManager
{
    public const string DefaultServerUrl = "http://localhost:5299";

    private sealed class AuthFile
    {
        public string? ServerUrl { get; set; }
        public string? Email { get; set; }
        public string? Token { get; set; }
        public Guid? UserId { get; set; }
        public DateTimeOffset? LastSyncAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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
    private readonly string _authFilePath;
    private readonly string _syncStateFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private HttpClient? _http;
    private SyncService? _service;
    private AuthFile _auth = new();

    public SyncManager(
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
        string dataDir)
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
        _authFilePath = Path.Combine(dataDir, "sync-auth.json");
        _syncStateFilePath = Path.Combine(dataDir, "sync-state.json");
        LoadAuth();
    }

    public bool IsLoggedIn => _auth.Token is not null && _auth.UserId is not null;
    public string? Email => _auth.Email;
    public DateTimeOffset? LastSyncAt => _auth.LastSyncAt;
    public string ServerUrl => _auth.ServerUrl ?? _state.SyncServerUrl ?? DefaultServerUrl;

    public async Task<AuthResult> RegisterAsync(string serverUrl, string email, string password, string inviteCode)
    {
        await _lock.WaitAsync();
        try
        {
            var service = GetService(serverUrl);
            var result = await service.RegisterAsync(email, password, inviteCode);
            if (result.Success)
            {
                _auth.ServerUrl = serverUrl;
                _auth.Email = email;
                _state.SyncServerUrl = serverUrl;
                _state.SyncEmail = email;
                SaveAuth();
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

            _auth.ServerUrl = serverUrl;
            _auth.Email = email;
            _auth.Token = service.Token;
            _auth.UserId = service.UserId;
            _state.SyncServerUrl = serverUrl;
            _state.SyncEmail = email;
            SaveAuth();

            await MigrateVehicleOwnerAsync(service.UserId);
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
                _auth.LastSyncAt = _clock.UtcNow;
                _state.LastSyncAt = _auth.LastSyncAt;
                SaveAuth();
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
            _auth.Token = null;
            _auth.UserId = null;
            SaveAuth();
            _service = null;
            _http?.Dispose();
            _http = null;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Einmalige Zuordnung beim ersten Login: alle Fahrzeuge des impliziten lokalen
    /// Nutzers gehören ab jetzt dem Server-Konto (sonst Rejected beim Push).
    /// </summary>
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

    /// <summary>HttpClient + SyncService passend zur Server-URL (bei URL-Wechsel neu aufbauen).</summary>
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
                    _auth.Token = token;
                    _auth.UserId = userId;
                    SaveAuth();
                },
            };
            if (_auth is { Token: { } token, UserId: { } userId })
                _service.UseToken(token, userId);
        }
        return _service;
    }

    private void LoadAuth()
    {
        try
        {
            if (File.Exists(_authFilePath))
                _auth = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(_authFilePath), Json) ?? new AuthFile();
        }
        catch (JsonException)
        {
            _auth = new AuthFile();
        }

        _state.SyncServerUrl = _auth.ServerUrl;
        _state.SyncEmail = _auth.Email;
        _state.LastSyncAt = _auth.LastSyncAt;
        if (_auth.UserId is { } userId)
            _state.CurrentUserId = userId; // Login übersteht den Neustart
    }

    private void SaveAuth() =>
        File.WriteAllText(_authFilePath, JsonSerializer.Serialize(_auth, Json));
}
