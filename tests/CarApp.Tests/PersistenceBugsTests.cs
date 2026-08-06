using System.Text.Json;
using CarApp.Core;
using CarApp.Data;
using CarApp.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CarApp.Tests;

internal sealed class FakeClock2(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// Regression tests for bugs found in the persistence layer
/// (src/CarApp.Data/JsonlObdSampleStore.cs, src/CarApp.Web/Services/SyncManager.cs), all fixed.
/// </summary>
public class JsonlObdSampleStoreBugTests
{
    /// <summary>
    /// FIXED (JsonlObdSampleStore.cs, ReadAllAsync): appends use File.AppendAllLinesAsync
    /// directly against the live .jsonl file (no tmp+rename — reasonable for a high-frequency
    /// append-only log, per the class's own doc comment), so a process crash mid-append (power
    /// loss, container OOM-kill) can leave a torn/partial trailing line behind. ReadAllAsync now
    /// wraps the per-line Deserialize in a try/catch and skips lines that fail to parse instead
    /// of letting one bad line take down the whole read - the live dashboard's history chart
    /// (VerlaufTab.razor -> IObdSampleStore.QueryAsync) stays usable even after a crash mid-poll.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SkipsTornTrailingLine_KeepsRestOfHistoryReadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vehicleId = Guid.NewGuid();
            var store = new JsonlObdSampleStore(dir);
            var now = DateTimeOffset.UtcNow;

            // 50 perfectly good samples get appended over time (simulating a poll loop).
            var good = Enumerable.Range(0, 50)
                .Select(i => new ObdSample
                {
                    VehicleId = vehicleId,
                    PidKey = "rpm",
                    Timestamp = now.AddSeconds(-i),
                    Value = 1000 + i,
                })
                .ToList();
            await store.AppendBatchAsync(good);

            // Sanity check: the store is fully readable before the crash.
            var before = await store.QueryAsync(vehicleId, null, now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(50, before.Count);

            // Simulate a crash exactly mid-append: a torn, truncated final line gets appended
            // directly to the file (this is what File.AppendAllLinesAsync leaves behind if the
            // process dies after writing part of a line but before the trailing newline/rest of
            // the JSON is flushed to disk).
            var file = Path.Combine(dir, $"samples-{vehicleId:N}.jsonl");
            await File.AppendAllTextAsync(file, "{\"VehicleId\":\"" + vehicleId + "\",\"PidKey\":\"spee");

            // Fixed: ReadAllAsync now wraps the per-line Deserialize in a try/catch and skips
            // lines that fail to parse instead of letting the exception propagate - so all 50
            // good samples remain readable despite the torn trailing line.
            var after = await store.QueryAsync(vehicleId, null, now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(50, after.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

public class SyncManagerBugTests
{
    private static SyncManager NewManager(string dataDir, AppState? state = null)
    {
        var dbFactory = new SqliteDbContextFactory(Path.Combine(dataDir, "carapp.db"));
        using (var migrationDb = dbFactory.CreateDbContext())
            migrationDb.Database.Migrate(); // idempotent - safe to call again for a "restart"

        return new SyncManager(
            new EfSyncRepository<Vehicle>(dbFactory),
            new EfSyncRepository<AdapterProfile>(dbFactory),
            new EfSyncRepository<OdometerReading>(dbFactory),
            new EfSyncRepository<Trip>(dbFactory),
            new EfSyncRepository<MaintenanceTask>(dbFactory),
            new EfSyncRepository<FuelEntry>(dbFactory),
            new EfSyncRepository<Expense>(dbFactory),
            new EfObdSampleStore(dbFactory),
            new FakeClock2(DateTimeOffset.UtcNow),
            state ?? new AppState(),
            dataDir);
    }

    /// <summary>
    /// FIXED (SyncManager.cs, LogoutAsync): logout used to clear the token/user id from the
    /// auth file but never reset AppState.CurrentUserId back to AppState.LocalUserId, so every
    /// write path in the UI that stamps new/edited data with an owner (e.g. VehicleForm.razor:
    /// "OwnerUserId = State.CurrentUserId") kept using the stale server user id after logout -
    /// silently orphaning vehicles created afterwards. LogoutAsync now resets CurrentUserId too.
    /// </summary>
    [Fact]
    public async Task LogoutAsync_ResetsCurrentUserId_SoVehiclesCreatedAfterLogoutStayLocal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-sync-logout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = new AppState();
            Assert.Equal(AppState.LocalUserId, state.CurrentUserId); // initial: implicit local user

            // Simulate a successful prior login (bypassing the network call — directly write what
            // LoginAsync would have persisted, then load it, exactly like an app restart after
            // being logged in).
            var serverUserId = Guid.NewGuid();
            var authPath = Path.Combine(dir, "sync-auth.json");
            File.WriteAllText(authPath, JsonSerializer.Serialize(new
            {
                ServerUrl = "http://localhost:5299",
                Email = "driver@example.com",
                Token = "session-token",
                UserId = serverUserId,
            }));
            var manager = NewManager(dir, state);
            Assert.True(manager.IsLoggedIn);
            Assert.Equal(serverUserId, state.CurrentUserId); // LoadAuth correctly restores this

            // User logs out.
            await manager.LogoutAsync();
            Assert.False(manager.IsLoggedIn);

            // Fixed: LogoutAsync now also resets AppState.CurrentUserId back to LocalUserId, so
            // any vehicle created after logout is correctly attributed to the local user again
            // instead of being silently stamped with the stale server account's id.
            Assert.Equal(AppState.LocalUserId, state.CurrentUserId);
        }
        finally
        {
            // SqliteConnection pools its underlying OS file handle by default - without
            // clearing the pool first, the directory delete below fails with "file in use".
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// FIXED (SyncManager.cs, SaveAuth): now mirrors JsonFileRepository.SaveAsync's tmp-file +
    /// atomic File.Move(overwrite) pattern instead of writing straight to the live
    /// sync-auth.json. A process kill mid-write can therefore never leave the live file
    /// truncated/half-written — the rename either completes fully (new state) or never happens
    /// at all (old state untouched). This verifies the mechanism itself: after a SaveAuth call,
    /// no dangling ".tmp" artifact remains and the live file is always complete, valid JSON.
    /// </summary>
    [Fact]
    public async Task SaveAuth_WritesAtomically_NoDanglingTempFileOrTornWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-sync-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var authPath = Path.Combine(dir, "sync-auth.json");
            var tmpPath = authPath + ".tmp";
            var manager = NewManager(dir);

            // LogoutAsync unconditionally calls SaveAuth() and needs no network call, so it
            // exercises the exact same write path a real login/sync would.
            await manager.LogoutAsync();

            Assert.True(File.Exists(authPath));
            Assert.False(File.Exists(tmpPath)); // renamed away, never left behind

            var content = File.ReadAllText(authPath);
            var parsed = JsonSerializer.Deserialize<JsonElement>(content);
            Assert.Equal(JsonValueKind.Object, parsed.ValueKind);

            // A fresh SyncManager loading the same file must not hit the "corrupted JSON ->
            // silently reset to blank" fallback path.
            var reloaded = NewManager(dir);
            Assert.False(reloaded.IsLoggedIn); // correctly reflects the logout, not a parse failure
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
