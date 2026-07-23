using System.Net.Http.Json;
using System.Text.Json;
using CarApp.Core;
using CarApp.Shared;

namespace CarApp.Application;

/// <summary>Ergebnis eines Auth-Aufrufs — Fehler als Text, kein Throw bei Offline.</summary>
public sealed record AuthResult(bool Success, string? Error = null);

/// <summary>Ergebnis eines Sync-Laufs (Pushed = vom Server akzeptierte Entitäten).</summary>
public sealed record SyncResult(bool Success, int Pushed, int Pulled, string? Error = null);

/// <summary>
/// Client-Seite des Offline-First-Syncs (Plan 2.3): Push aller Pending-Änderungen,
/// danach Pull aller Server-Änderungen seit dem letzten Sync (Last-Write-Wins per
/// ModifiedAt). Der Zeitstempel des letzten Syncs liegt in einer kleinen JSON-Datei.
/// Ist das Backend nicht erreichbar, liefern alle Methoden ein sauberes
/// Ergebnisobjekt statt einer Exception — die App arbeitet lokal weiter.
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

    public string? Token { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Hook, um ein frisches Token z.B. in SecureStorage zu übernehmen.</summary>
    public Action<string, Guid>? TokenChanged { get; set; }

    /// <summary>Vorhandenes Token wiederverwenden (z.B. aus SecureStorage beim App-Start).</summary>
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

    /// <summary>Push aller Pending-Entitäten, dann Pull seit letztem Sync. Reihenfolge: erst Fahrzeuge (Besitz), dann Kind-Entitäten.</summary>
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
            // Offline oder Server-Fehler: lokale Daten bleiben unangetastet, kein Crash.
            return new SyncResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>Schiebt einen Samples-Batch zum Server (append-only, kein Konfliktpotenzial).</summary>
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

    /// <summary>Schiebt lokale Samples aus dem Store (Zeitfenster) zum Server.</summary>
    public async Task<SyncPushResponse?> PushLocalSamplesAsync(Guid vehicleId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (sampleStore is null)
            return new SyncPushResponse(0, 0);
        var batch = await sampleStore.QueryAsync(vehicleId, null, from, to, ct).ConfigureAwait(false);
        return batch.Count == 0 ? new SyncPushResponse(0, 0) : await PushSamplesAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>Verlaufsabfrage gegen den Server. Null bei fehlender Verbindung oder fehlendem Zugriff.</summary>
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
            ?? new SyncPushResponse(0, 0);

        // Erfolgreich übertragen → lokal als Synced markieren (ModifiedAt bleibt unverändert).
        foreach (var e in pending)
        {
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
            // Last-Write-Wins: nur übernehmen, wenn der Server-Stand neuer ist.
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

    // --- Zeitstempel des letzten Syncs (kleine JSON-Datei) ------------------------------

    private sealed class SyncStateFile
    {
        public DateTimeOffset LastSync { get; set; }
    }

    private DateTimeOffset LoadLastSync()
    {
        try
        {
            if (!File.Exists(syncStateFile))
                return DateTimeOffset.MinValue;
            var state = JsonSerializer.Deserialize<SyncStateFile>(File.ReadAllText(syncStateFile), Json);
            return state?.LastSync ?? DateTimeOffset.MinValue;
        }
        catch (JsonException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private void SaveLastSync(DateTimeOffset ts) =>
        File.WriteAllText(syncStateFile, JsonSerializer.Serialize(new SyncStateFile { LastSync = ts }, Json));
}
