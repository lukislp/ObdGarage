using System.Net;
using System.Net.Http.Headers;
using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using CarApp.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarApp.TestRunner;

/// <summary>
/// End-to-end tests for login + offline-first sync: server in-process (ServerApp.BuildApp),
/// clients as SyncService with their own local data directory (each client = "one device").
/// </summary>
public static class SyncTests
{
    private sealed record ClientCtx(
        string Dir,
        SyncService Sync,
        EfSyncRepository<Vehicle> Vehicles,
        EfSyncRepository<Trip> Trips,
        EfObdSampleStore Samples,
        HttpClient Http);

    private static ClientCtx NewClient(string baseUrl)
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbFactory = new SqliteDbContextFactory(Path.Combine(dir, "carapp.db"));
        using (var migrationDb = dbFactory.CreateDbContext())
            migrationDb.Database.Migrate();

        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var vehicles = new EfSyncRepository<Vehicle>(dbFactory);
        var trips = new EfSyncRepository<Trip>(dbFactory);
        var store = new EfObdSampleStore(dbFactory);
        var sync = new SyncService(
            http,
            vehicles,
            new EfSyncRepository<AdapterProfile>(dbFactory),
            new EfSyncRepository<OdometerReading>(dbFactory),
            trips,
            new EfSyncRepository<MaintenanceTask>(dbFactory),
            new EfSyncRepository<FuelEntry>(dbFactory),
            new EfSyncRepository<Expense>(dbFactory),
            store,
            new SystemClock(),
            Path.Combine(dir, "syncstate.json"));
        return new ClientCtx(dir, sync, vehicles, trips, store, http);
    }

    public static async Task RunAsync(Action<string, bool> check)
    {
        Console.WriteLine("Sync-Backend (In-Process-Server):");

        var serverDir = Path.Combine(Path.GetTempPath(), "carapp-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverDir);
        var dirs = new List<string> { serverDir };

        var app = ServerApp.BuildApp([], serverDir);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var baseUrl = app.Urls.First(); // actual address after startup (port was assigned by the OS)

        try
        {
            using var raw = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            var health = await raw.GetAsync("api/v1/health");
            check("Health-Endpoint erreichbar", health.IsSuccessStatusCode);

            // --- Registration + login -----------------------------------------------
            var a = NewClient(baseUrl);
            dirs.Add(a.Dir);
            var badInvite = await a.Sync.RegisterAsync("alice@example.com", "geheim123", "FALSCHER-CODE");
            check("Registrierung mit falschem Einladungscode abgelehnt", !badInvite.Success);
            var reg = await a.Sync.RegisterAsync("alice@example.com", "geheim123", "CARAPP-2026");
            check("Registrierung mit korrektem Einladungscode", reg.Success);
            var dup = await a.Sync.RegisterAsync("Alice@Example.com", "anders", "CARAPP-2026");
            check("Doppelte E-Mail (case-insensitiv) abgelehnt", !dup.Success);

            var badPw = await a.Sync.LoginAsync("alice@example.com", "falschesPasswort");
            check("Login mit falschem Passwort abgelehnt", !badPw.Success && a.Sync.Token is null);
            var login = await a.Sync.LoginAsync("alice@example.com", "geheim123");
            check("Login liefert Token + UserId",
                login.Success && !string.IsNullOrEmpty(a.Sync.Token) && a.Sync.UserId != Guid.Empty);

            // --- Auth requirement: without/with wrong token → 401 ---------------------------
            var noToken = await raw.GetAsync("api/v1/sync/vehicles");
            check("Sync ohne Token → 401", noToken.StatusCode == HttpStatusCode.Unauthorized);
            using var wrongToken = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            wrongToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "voellig-falsches-token");
            var wrong = await wrongToken.GetAsync("api/v1/sync/vehicles");
            check("Sync mit falschem Token → 401", wrong.StatusCode == HttpStatusCode.Unauthorized);

            // --- Push/pull roundtrip ---------------------------------------------------
            var vehicle = new Vehicle { Name = "Golf", OwnerUserId = a.Sync.UserId, LastKnownOdometerKm = 42_000 };
            await a.Vehicles.UpsertAsync(vehicle);
            var trip = new Trip
            {
                VehicleId = vehicle.Id,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                EndedAt = DateTimeOffset.UtcNow,
                DistanceKm = 12.3,
                Category = TripCategory.Business,
            };
            await a.Trips.UpsertAsync(trip);

            var sync1 = await a.Sync.SyncAsync();
            check($"Erster Sync: 2 Entitäten gepusht (war {sync1.Pushed})", sync1.Success && sync1.Pushed == 2);
            check("Lokale Entitäten nach Push auf Synced",
                (await a.Vehicles.GetAllIncludingDeletedAsync()).All(v => v.SyncState == SyncState.Synced) &&
                (await a.Trips.GetAllIncludingDeletedAsync()).All(t => t.SyncState == SyncState.Synced));

            var a2 = NewClient(baseUrl); // fresh device, same user
            dirs.Add(a2.Dir);
            await a2.Sync.LoginAsync("alice@example.com", "geheim123");
            var sync2 = await a2.Sync.SyncAsync();
            var a2Vehicles = await a2.Vehicles.GetAllAsync();
            var a2Trips = await a2.Trips.GetAllAsync();
            check("Pull auf frisches Gerät: Fahrzeug identisch",
                sync2.Success && a2Vehicles.Count == 1 &&
                a2Vehicles[0].Id == vehicle.Id && a2Vehicles[0].Name == "Golf" &&
                a2Vehicles[0].LastKnownOdometerKm == 42_000);
            check("Pull auf frisches Gerät: Trip identisch",
                a2Trips.Count == 1 && a2Trips[0].Id == trip.Id &&
                Math.Abs(a2Trips[0].DistanceKm - 12.3) < 1e-9 &&
                a2Trips[0].Category == TripCategory.Business);
            check("Gepullte Entitäten sind Synced",
                a2Vehicles[0].SyncState == SyncState.Synced && a2Trips[0].SyncState == SyncState.Synced);

            // --- User separation ----------------------------------------------------------
            var b = NewClient(baseUrl);
            dirs.Add(b.Dir);
            await b.Sync.RegisterAsync("bob@example.com", "geheim456", "CARAPP-2026");
            await b.Sync.LoginAsync("bob@example.com", "geheim456");
            var bSync = await b.Sync.SyncAsync();
            check("Nutzer B sieht Fahrzeuge von A nicht",
                bSync.Success && (await b.Vehicles.GetAllAsync()).Count == 0);

            // B tries to push foreign data (trip on A's vehicle + hijacking A's vehicle).
            var foreignTrip = new Trip { VehicleId = vehicle.Id, DistanceKm = 999 };
            await b.Trips.UpsertAsync(foreignTrip);
            var hijack = new Vehicle
            {
                Id = vehicle.Id, Name = "Gekapert", OwnerUserId = b.Sync.UserId,
                ModifiedAt = DateTimeOffset.UtcNow.AddHours(2),
            };
            await b.Vehicles.UpsertAsync(hijack);
            var bPush = await b.Sync.SyncAsync();
            check($"Fremde Entitäten werden serverseitig verworfen (akzeptiert: {bPush.Pushed})", bPush.Pushed == 0);

            var a3 = NewClient(baseUrl);
            dirs.Add(a3.Dir);
            await a3.Sync.LoginAsync("alice@example.com", "geheim123");
            await a3.Sync.SyncAsync();
            check("A-Fahrzeug nach Kaper-Versuch unverändert",
                (await a3.Vehicles.GetAllAsync()).Single().Name == "Golf");
            check("Fremder Trip wurde nicht übernommen",
                (await a3.Trips.GetAllAsync()).All(t => t.Id != foreignTrip.Id));

            // --- Last-Write-Wins ---------------------------------------------------------
            var vNew = (await a2.Vehicles.GetAllAsync()).Single();
            vNew.Name = "Golf GTI";
            vNew.Touch();
            await a2.Vehicles.UpsertAsync(vNew);
            await a2.Sync.SyncAsync();

            var vStale = (await a.Vehicles.GetAllAsync()).Single();
            vStale.Name = "Veralteter Stand";
            vStale.ModifiedAt = DateTimeOffset.UtcNow.AddHours(-1); // deliberately older than the server state
            vStale.SyncState = SyncState.Pending;
            await a.Vehicles.UpsertAsync(vStale);
            await a.Sync.SyncAsync();
            check("LWW: ältere Version überschreibt neuere nicht",
                (await a.Vehicles.GetAllAsync()).Single().Name == "Golf GTI");

            // --- Soft-delete is transferred ----------------------------------------------
            await a2.Trips.DeleteAsync(trip.Id);
            await a2.Sync.SyncAsync();
            await a.Sync.SyncAsync();
            check("Soft-Delete kommt beim anderen Gerät an",
                await a.Trips.GetAsync(trip.Id) is null && (await a.Trips.GetAllAsync()).Count == 0);
            check("Gelöschter Trip bleibt als Tombstone erhalten",
                (await a.Trips.GetAllIncludingDeletedAsync()).Any(t => t.Id == trip.Id && t.IsDeleted));

            // --- Samples: batch push + query ----------------------------------------------
            var now = DateTimeOffset.UtcNow;
            var batch = Enumerable.Range(0, 5).Select(i => new ObdSample
            {
                VehicleId = vehicle.Id,
                PidKey = i < 3 ? "rpm" : "speed",
                Timestamp = now.AddSeconds(-i),
                Value = 1000 + i,
            }).ToList();
            var pushRes = await a.Sync.PushSamplesAsync(batch);
            check("Samples-Batch gepusht (5 akzeptiert)", pushRes is { Accepted: 5, Rejected: 0 });

            var rpm = await a.Sync.QuerySamplesAsync(vehicle.Id, "rpm", now.AddMinutes(-1), now.AddMinutes(1));
            check($"Samples-Query filtert nach PidKey (war {rpm?.Count})",
                rpm is { Count: 3 } && rpm.All(s => s.PidKey == "rpm"));
            var allSamples = await a.Sync.QuerySamplesAsync(vehicle.Id, null, now.AddMinutes(-1), now.AddMinutes(1));
            check("Samples-Query ohne PidKey-Filter liefert alle", allSamples is { Count: 5 });

            await a.Samples.AppendBatchAsync(
                [new ObdSample { VehicleId = vehicle.Id, PidKey = "coolant_temp", Timestamp = now, Value = 90 }]);
            var localPush = await a.Sync.PushLocalSamplesAsync(vehicle.Id, now.AddMinutes(-1), now.AddMinutes(1));
            check("Lokale Samples aus dem Store gepusht", localPush is { Accepted: >= 1 });
            var coolant = await a.Sync.QuerySamplesAsync(vehicle.Id, "coolant_temp", now.AddMinutes(-1), now.AddMinutes(1));
            check("Server kennt das coolant_temp-Sample", coolant is { Count: 1 });

            var bQuery = await b.Sync.QuerySamplesAsync(vehicle.Id, null, now.AddMinutes(-1), now.AddMinutes(1));
            check("B kann Samples von A nicht abfragen (403)", bQuery is null);
            var bForeignSamples = await b.Sync.PushSamplesAsync(
                [new ObdSample { VehicleId = vehicle.Id, PidKey = "rpm", Timestamp = now, Value = 1 }]);
            check("Fremde Samples werden verworfen", bForeignSamples is { Accepted: 0, Rejected: 1 });

            // --- Offline robustness ---------------------------------------------------------
            var offline = NewClient("http://127.0.0.1:1"); // nothing is listening there
            dirs.Add(offline.Dir);
            offline.Sync.UseToken("dummy-token", Guid.NewGuid());
            var offSync = await offline.Sync.SyncAsync();
            check("Offline: SyncAsync liefert Fehlerobjekt statt Crash",
                !offSync.Success && offSync.Error is not null);
            var offLogin = await offline.Sync.LoginAsync("x@y.z", "pw");
            check("Offline: LoginAsync liefert Fehlerobjekt statt Crash", !offLogin.Success);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            // SqliteConnection pools its underlying OS file handle by default - without
            // clearing the pool first, the directory deletes below fail with "file in use".
            SqliteConnection.ClearAllPools();
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, recursive: true); }
                catch (IOException) { /* Cleanup is best effort */ }
            }
        }
    }
}
