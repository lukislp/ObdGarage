using System.Net.Http.Json;
using System.Text.Json;
using ObdGarage.Core;
using ObdGarage.Shared;

namespace ObdGarage.Application;

/// <summary>Result of an auth call — error as text, no throw when offline.</summary>
public sealed record AuthResult(bool Success, string? Error = null);

/// <summary>Result of a sync run (Pushed = entities accepted by the server).</summary>
public sealed record SyncResult(bool Success, int Pushed, int Pulled, string? Error = null);

/// <summary>
/// Client side of offline-first sync (Plan 2.3): push all pending changes,
/// then pull all server changes since the last sync (Last-Write-Wins per
/// ModifiedAt). The timestamp of the last sync is stored in a small JSON file.
/// If the backend is unreachable, all methods return a clean result object
/// instead of throwing an exception — the app keeps working locally.
/// </summary>
public sealed class SyncService(
    HttpClient http,
    ISyncRepository<Vehicle> vehicles,
    ISyncRepository<AdapterProfile> adapterProfiles,
    ISyncRepository<OdometerReading> odometerReadings,
    ISyncRepository<Trip> trips,
    ISyncRepository<MaintenanceTask> maintenanceTasks,
    ISyncRepository<FuelEntry> fuelEntries,
    ISyncRepository<Expense> expenses,
    IObdSampleStore? sampleStore,
    IClock clock,
    string syncStateFile)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Default home-server URL, used when a host hasn't configured its own.</summary>
    public const string DefaultServerUrl = "http://localhost:5299";

    public string? Token { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Hook to adopt a fresh token, e.g. into SecureStorage.</summary>
    public Action<string, Guid>? TokenChanged { get; set; }

    /// <summary>Reuse an existing token (e.g. from SecureStorage at app start).</summary>
    public void UseToken(string token, Guid userId)
    {
        Token = token;
        UserId = userId;
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, string inviteCode,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("api/v1/auth/register",
                new RegisterRequest(email, password, inviteCode), Json, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? new AuthResult(true)
                : new AuthResult(false, await ReadErrorAsync(resp, ct).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult(false, ex.Message);
        }
    }

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("api/v1/auth/login",
                new LoginRequest(email, password), Json, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new AuthResult(false, await ReadErrorAsync(resp, ct).ConfigureAwait(false));

            var login = await resp.Content.ReadFromJsonAsync<LoginResponse>(Json, ct).ConfigureAwait(false);
            if (login is null)
                return new AuthResult(false, "Unerwartete Antwort vom Server.");

            UseToken(login.Token, login.UserId);
            TokenChanged?.Invoke(login.Token, login.UserId);
            return new AuthResult(true);
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult(false, ex.Message);
        }
    }

    /// <summary>Push all pending entities, then pull since the last sync. Order: vehicles first (ownership), then child entities.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        if (Token is null)
            return new SyncResult(false, 0, 0, "Nicht angemeldet — bitte zuerst einloggen.");

        try
        {
            var pushed = 0;
            pushed += await PushAsync("vehicles", vehicles, ct).ConfigureAwait(false);
            pushed += await PushAsync("adapterprofiles", adapterProfiles, ct).ConfigureAwait(false);
            pushed += await PushAsync("odometerreadings", odometerReadings, ct).ConfigureAwait(false);
            pushed += await PushAsync("trips", trips, ct).ConfigureAwait(false);
            pushed += await PushAsync("maintenancetasks", maintenanceTasks, ct).ConfigureAwait(false);
            pushed += await PushAsync("fuelentries", fuelEntries, ct).ConfigureAwait(false);
            pushed += await PushAsync("expenses", expenses, ct).ConfigureAwait(false);

            var since = LoadLastSync();
            var pulled = 0;
            var newest = since;
            foreach (var pull in new Func<DateTimeOffset, CancellationToken, Task<(int, DateTimeOffset)>>[]
            {
                (s, c) => PullAsync("vehicles", vehicles, s, c),
                (s, c) => PullAsync("adapterprofiles", adapterProfiles, s, c),
                (s, c) => PullAsync("odometerreadings", odometerReadings, s, c),
                (s, c) => PullAsync("trips", trips, s, c),
                (s, c) => PullAsync("maintenancetasks", maintenanceTasks, s, c),
                (s, c) => PullAsync("fuelentries", fuelEntries, s, c),
                (s, c) => PullAsync("expenses", expenses, s, c),
            })
            {
                var (count, serverTime) = await pull(since, ct).ConfigureAwait(false);
                pulled += count;
                if (serverTime > newest)
                    newest = serverTime;
            }

            SaveLastSync(newest);
            return new SyncResult(true, pushed, pulled);
        }
        catch (HttpRequestException ex)
        {
            // Offline or server error: local data remains untouched, no crash.
            return new SyncResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>Pushes a samples batch to the server (append-only, no conflict potential).</summary>
    public async Task<SyncPushResponse?> PushSamplesAsync(IReadOnlyList<ObdSample> batch,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("api/v1/samples", batch, Json, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<SyncPushResponse>(Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Pushes local samples from the store (time window) to the server.</summary>
    public async Task<SyncPushResponse?> PushLocalSamplesAsync(Guid vehicleId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (sampleStore is null)
            return new SyncPushResponse(0, 0, []);
        var batch = await sampleStore.QueryAsync(vehicleId, null, from, to, ct).ConfigureAwait(false);
        return batch.Count == 0 ? new SyncPushResponse(0, 0, []) : await PushSamplesAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes every not-yet-pushed sample for every local vehicle, tracking a per-vehicle
    /// watermark (in the same small state file as <see cref="LoadLastSync"/>) so repeated calls
    /// - e.g. a retry once connectivity to the home server returns - never re-send samples the
    /// server has already accepted. Safe to call any time, including when offline: on failure
    /// (server unreachable) a vehicle's watermark is simply left untouched and that vehicle's
    /// samples are retried on the next call, with no data loss and no duplicate pushes.
    /// </summary>
    public async Task<SyncPushResponse> PushPendingSamplesAsync(CancellationToken ct = default)
    {
        if (Token is null || sampleStore is null)
            return new SyncPushResponse(0, 0, []);

        var state = LoadState();
        var totalAccepted = 0;
        var totalRejected = 0;
        var allRejectedIds = new List<Guid>();
        var now = clock.UtcNow;

        foreach (var vehicle in await vehicles.GetAllIncludingDeletedAsync(ct).ConfigureAwait(false))
        {
            var from = state.LastSamplePushByVehicle.TryGetValue(vehicle.Id, out var last)
                ? last
                : DateTimeOffset.MinValue;

            var batch = await sampleStore.QueryAsync(vehicle.Id, null, from, now, ct).ConfigureAwait(false);
            if (batch.Count == 0)
                continue;

            var result = await PushSamplesAsync(batch, ct).ConfigureAwait(false);
            if (result is null)
                continue; // offline/error - watermark stays put, retried on the next call

            totalAccepted += result.Accepted;
            totalRejected += result.Rejected;
            allRejectedIds.AddRange(result.RejectedIds);

            // Advance past everything in this batch regardless of accept/reject - samples have
            // no per-item retry state to flip (unlike PushAsync<T>'s entities), so a permanently
            // rejected sample (e.g. ownership mismatch) would otherwise be resent forever.
            // +1 tick: QueryAsync's "from" bound is inclusive (Timestamp >= from), so watermark
            // == the last pushed sample's own timestamp would re-select and re-push that exact
            // sample next time - which the server then rejects with a UNIQUE constraint failure
            // on its Id (confirmed live via ObdGarage.TestRunner before this fix).
            state.LastSamplePushByVehicle[vehicle.Id] = batch.Max(s => s.Timestamp).AddTicks(1);
        }

        SaveState(state);
        return new SyncPushResponse(totalAccepted, totalRejected, allRejectedIds);
    }

    /// <summary>History query against the server. Null if there's no connection or no access.</summary>
    public async Task<IReadOnlyList<ObdSample>?> QuerySamplesAsync(Guid vehicleId, string? pidKey,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        try
        {
            var f = from ?? DateTimeOffset.MinValue;
            var t = to ?? clock.UtcNow;
            var url = $"api/v1/samples?vehicleId={vehicleId}" +
                      $"&pidKey={Uri.EscapeDataString(pidKey ?? "")}" +
                      $"&from={Uri.EscapeDataString(f.ToString("O"))}" +
                      $"&to={Uri.EscapeDataString(t.ToString("O"))}";
            var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<List<ObdSample>>(Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<int> PushAsync<T>(string name, ISyncRepository<T> repo, CancellationToken ct)
        where T : SyncEntity
    {
        var pending = (await repo.GetAllIncludingDeletedAsync(ct).ConfigureAwait(false))
            .Where(e => e.SyncState == SyncState.Pending)
            .ToList();
        if (pending.Count == 0)
            return 0;

        var resp = await http.PostAsJsonAsync($"api/v1/sync/{name}", pending, Json, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Push {name}: HTTP {(int)resp.StatusCode}");

        var result = await resp.Content.ReadFromJsonAsync<SyncPushResponse>(Json, ct).ConfigureAwait(false)
            ?? new SyncPushResponse(0, 0, []);

        // Only entities the server actually accepted are marked Synced; a rejected one
        // (e.g. its VehicleId doesn't resolve to a vehicle this user owns) stays Pending
        // so it's retried on the next sync instead of being silently forgotten forever.
        var rejected = result.RejectedIds.ToHashSet();
        foreach (var e in pending)
        {
            if (rejected.Contains(e.Id))
                continue;
            e.SyncState = SyncState.Synced;
            await repo.UpsertAsync(e, ct).ConfigureAwait(false);
        }
        return result.Accepted;
    }

    private async Task<(int Applied, DateTimeOffset ServerTime)> PullAsync<T>(string name,
        ISyncRepository<T> repo, DateTimeOffset since, CancellationToken ct) where T : SyncEntity
    {
        var url = $"api/v1/sync/{name}?since={Uri.EscapeDataString(since.ToString("O"))}";
        var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Pull {name}: HTTP {(int)resp.StatusCode}");

        var envelope = await resp.Content.ReadFromJsonAsync<SyncEnvelope<T>>(Json, ct).ConfigureAwait(false);
        if (envelope is null)
            return (0, clock.UtcNow);

        var local = (await repo.GetAllIncludingDeletedAsync(ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var applied = 0;
        foreach (var item in envelope.Items)
        {
            // Last-Write-Wins: only apply if the server version is newer.
            if (local.TryGetValue(item.Id, out var mine) && mine.ModifiedAt >= item.ModifiedAt)
                continue;
            item.SyncState = SyncState.Synced;
            await repo.UpsertAsync(item, ct).ConfigureAwait(false);
            applied++;
        }
        return (applied, envelope.ServerTime);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct).ConfigureAwait(false);
            return err?.Error ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (JsonException)
        {
            return $"HTTP {(int)resp.StatusCode}";
        }
    }

    // --- Sync state (small JSON file: last entity sync + per-vehicle sample watermarks) ---

    private sealed class SyncStateFile
    {
        public DateTimeOffset LastSync { get; set; }

        /// <summary>Timestamp of the newest sample already pushed, per vehicle - absent/older
        /// entries mean "not pushed yet". Missing entirely on state files written before this
        /// field existed - deserializes to an empty dictionary, not a crash.</summary>
        public Dictionary<Guid, DateTimeOffset> LastSamplePushByVehicle { get; set; } = new();
    }

    private DateTimeOffset LoadLastSync() => LoadState().LastSync;

    private void SaveLastSync(DateTimeOffset ts)
    {
        var state = LoadState();
        state.LastSync = ts;
        SaveState(state);
    }

    private SyncStateFile LoadState()
    {
        try
        {
            if (!File.Exists(syncStateFile))
                return new SyncStateFile();
            return JsonSerializer.Deserialize<SyncStateFile>(File.ReadAllText(syncStateFile), Json)
                ?? new SyncStateFile();
        }
        catch (JsonException)
        {
            return new SyncStateFile();
        }
    }

    private void SaveState(SyncStateFile state) =>
        File.WriteAllText(syncStateFile, JsonSerializer.Serialize(state, Json));
}
