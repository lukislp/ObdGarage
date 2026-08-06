using System.Diagnostics;
using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using CarApp.Server;
using Microsoft.AspNetCore.Builder;

namespace CarApp.TestRunner;

/// <summary>
/// Regression tests for two bugs found during a security review of
/// ServerApp.cs / TokenStore.cs / UserStore.cs / SyncService.cs, both now fixed:
///
///  1) UserStore.VerifyAsync used to leak account existence via response timing — PBKDF2
///     (210,000 iterations) only ran when the email was known, so /api/v1/auth/login
///     answered measurably faster for unregistered emails than for registered ones
///     with a wrong password. Fixed by always running an equivalent-cost dummy hash on
///     the unknown-email path.
///
///  2) SyncService.PushAsync (CarApp.Application) used to mark EVERY locally-pending
///     entity as SyncState.Synced after a push, regardless of whether the server
///     actually accepted it. A legitimate local change the server rejected — e.g.
///     because its VehicleId didn't (yet, or anymore) resolve to an owned vehicle —
///     was silently and permanently treated as synced. Fixed via SyncPushResponse.
///     RejectedIds: only entities the server actually accepted are marked Synced.
/// </summary>
public static class SecurityTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        await UserStoreTimingSideChannel(check);
        await SyncPushRetriesRejectedEntityInsteadOfLosingItAsync(check);
    }

    // --- Bug 1: login timing reveals account existence -----------------------------

    private static async Task UserStoreTimingSideChannel(Action<string, bool> check)
    {
        Console.WriteLine("UserStore Timing-Seitenkanal (Account-Enumeration ueber /auth/login):");

        var dir = Path.Combine(Path.GetTempPath(), "carapp-userstore-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new UserStore(Path.Combine(dir, "users.json"));
            await store.RegisterAsync("alice@example.com", "correct-horse-battery-staple");

            // Warm up JIT/file-cache for both code paths before measuring.
            await store.VerifyAsync("alice@example.com", "warmup-wrong-password");
            await store.VerifyAsync("nobody-warmup@example.com", "warmup-wrong-password");

            const int iterations = 20;

            var swKnown = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
                await store.VerifyAsync("alice@example.com", $"wrong-password-{i}");
            swKnown.Stop();

            var swUnknown = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
                await store.VerifyAsync($"nobody-{i}@example.com", "irrelevant-password");
            swUnknown.Stop();

            var avgKnownMs = swKnown.Elapsed.TotalMilliseconds / iterations;
            var avgUnknownMs = swUnknown.Elapsed.TotalMilliseconds / iterations;
            Console.WriteLine($"  bekannte E-Mail, falsches Passwort: {avgKnownMs:F3} ms/Aufruf (fuehrt PBKDF2 aus)");
            Console.WriteLine($"  unbekannte E-Mail:                  {avgUnknownMs:F3} ms/Aufruf (kein PBKDF2)");

            // Fixed: VerifyAsync now runs an equivalent-cost dummy hash on the unknown-email
            // path too, so the unknown path should take a comparable amount of time to the
            // known path instead of returning near-instantly.
            check($"Login-Antwortzeit verraet KEINE Account-Existenz mehr (bekannt {avgKnownMs:F3} ms vs. unbekannt {avgUnknownMs:F3} ms)",
                avgUnknownMs > avgKnownMs * 0.5);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // --- Bug 2 (fixed): rejected pushes stay Pending and are retried ---------------

    private static async Task SyncPushRetriesRejectedEntityInsteadOfLosingItAsync(Action<string, bool> check)
    {
        Console.WriteLine("SyncService: serverseitig abgelehnter Push bleibt Pending und wird retried:");

        var serverDir = Path.Combine(Path.GetTempPath(), "carapp-server-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverDir);
        var clientDir = Path.Combine(Path.GetTempPath(), "carapp-client-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clientDir);

        var app = ServerApp.BuildApp([], serverDir);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var baseUrl = app.Urls.First();

        try
        {
            var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            var vehicles = new JsonFileRepository<Vehicle>(clientDir);
            var trips = new JsonFileRepository<Trip>(clientDir);
            var sync = new SyncService(
                http,
                vehicles,
                new JsonFileRepository<AdapterProfile>(clientDir),
                new JsonFileRepository<OdometerReading>(clientDir),
                trips,
                new JsonFileRepository<MaintenanceTask>(clientDir),
                new JsonFileRepository<FuelEntry>(clientDir),
                new JsonFileRepository<Expense>(clientDir),
                sampleStore: null,
                new SystemClock(),
                Path.Combine(clientDir, "syncstate.json"));

            var reg = await sync.RegisterAsync("carol@example.com", "geheim789", "CARAPP-2026");
            check("Setup: Registrierung erfolgreich", reg.Success);
            var login = await sync.LoginAsync("carol@example.com", "geheim789");
            check("Setup: Login erfolgreich", login.Success);

            // Legitimate local edit, but its VehicleId does not (yet) resolve to any vehicle
            // this user owns (e.g. the vehicle hasn't been synced yet). This is NOT an attack
            // — it's an ordinary "server rejects this item for now" case that PushAsync must
            // handle by leaving it Pending so it can be retried once the vehicle exists.
            var vehicleId = Guid.NewGuid();
            var orphanTrip = new Trip
            {
                VehicleId = vehicleId, // no such vehicle exists for this user yet
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                EndedAt = DateTimeOffset.UtcNow,
                DistanceKm = 42,
                Category = TripCategory.Private,
            };
            await trips.UpsertAsync(orphanTrip);
            check("Vorbedingung: Trip ist lokal Pending vor dem Sync",
                (await trips.GetAsync(orphanTrip.Id))?.SyncState == SyncState.Pending);

            var result = await sync.SyncAsync();
            check($"Sync meldet 0 akzeptierte Entitaeten (war {result.Pushed})", result.Success && result.Pushed == 0);

            var afterPush = await trips.GetAsync(orphanTrip.Id);
            // FIXED: SyncService.PushAsync now only marks entities Synced that the server's
            // RejectedIds does NOT list, so this rejected trip correctly stays Pending instead
            // of being silently (and permanently) treated as synced.
            check("Fix bestaetigt: serverseitig abgelehnter Trip bleibt lokal Pending",
                afterPush?.SyncState == SyncState.Pending);

            // The underlying problem gets resolved (the vehicle is finally created/synced) -
            // the retry should now actually succeed instead of the edit being lost forever.
            var vehicle = new Vehicle { Id = vehicleId, Name = "Carols Auto", OwnerUserId = sync.UserId };
            await vehicles.UpsertAsync(vehicle);
            var result2 = await sync.SyncAsync();
            check($"Nach Behebung: naechster Sync pusht Fahrzeug + Trip erfolgreich (gepusht: {result2.Pushed}, erwartet 2)",
                result2.Success && result2.Pushed == 2);

            var afterRetry = await trips.GetAsync(orphanTrip.Id);
            check("Trip ist nach erfolgreichem Retry lokal als Synced markiert",
                afterRetry?.SyncState == SyncState.Synced);

            // Confirm it actually made it to the server this time: a second device for the
            // same user pulls everything and should now see both the vehicle and the trip.
            var clientDir2 = Path.Combine(Path.GetTempPath(), "carapp-client-sec2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(clientDir2);
            var http2 = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            var trips2 = new JsonFileRepository<Trip>(clientDir2);
            var sync2 = new SyncService(
                http2,
                new JsonFileRepository<Vehicle>(clientDir2),
                new JsonFileRepository<AdapterProfile>(clientDir2),
                new JsonFileRepository<OdometerReading>(clientDir2),
                trips2,
                new JsonFileRepository<MaintenanceTask>(clientDir2),
                new JsonFileRepository<FuelEntry>(clientDir2),
                new JsonFileRepository<Expense>(clientDir2),
                sampleStore: null,
                new SystemClock(),
                Path.Combine(clientDir2, "syncstate.json"));
            await sync2.LoginAsync("carol@example.com", "geheim789");
            await sync2.SyncAsync();
            check("Bestaetigung: Trip ist jetzt auf dem Server gelandet (kein Datenverlust)",
                (await trips2.GetAllIncludingDeletedAsync()).Any(t => t.Id == orphanTrip.Id));

            try { Directory.Delete(clientDir2, recursive: true); } catch (IOException) { }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(serverDir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(clientDir, recursive: true); } catch (IOException) { }
        }
    }
}
