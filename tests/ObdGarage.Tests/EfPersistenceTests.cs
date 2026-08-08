using ObdGarage.Core;
using ObdGarage.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ObdGarage.Tests;

/// <summary>
/// Exercises the real EF Core/SQLite stack end to end (not mocked) - a genuine .db file on
/// disk, migrated, written to, read back. Also the first real runtime proof that the pinned
/// SQLitePCLRaw.bundle_e_sqlite3 3.0.5 override (ObdGarage.Data.csproj - the NuGet-default 2.1.11
/// has a known advisory) actually works, not just compiles.
/// </summary>
public class EfPersistenceTests
{
    private static IDbContextFactory<ObdGarageDbContext> NewFactory(string dbPath) => new SqliteDbContextFactory(dbPath);

    private static async Task<string> NewMigratedDbAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "obdgarage-ef-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = NewFactory(dbPath);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return dbPath;
    }

    [Fact]
    public async Task EfSyncRepository_RoundTripsInsertGetUpdateSoftDelete()
    {
        var dbPath = await NewMigratedDbAsync();
        try
        {
            var repo = new EfSyncRepository<Vehicle>(NewFactory(dbPath));

            var vehicle = new Vehicle
            {
                Name = "Familienkombi",
                OwnerUserId = Guid.NewGuid(),
                SupportedPids = [0x0C, 0x0D, 0xA6],
            };
            await repo.UpsertAsync(vehicle);

            var loaded = await repo.GetAsync(vehicle.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Familienkombi", loaded!.Name);
            Assert.Equal(new List<byte> { 0x0C, 0x0D, 0xA6 }, loaded.SupportedPids);

            // Update (same Id, different values) must overwrite, not duplicate.
            loaded.Name = "Zweitwagen";
            loaded.Touch();
            await repo.UpsertAsync(loaded);
            Assert.Single(await repo.GetAllAsync());
            Assert.Equal("Zweitwagen", (await repo.GetAsync(vehicle.Id))!.Name);

            // Soft delete: gone from GetAsync/GetAllAsync, still present via GetAllIncludingDeletedAsync.
            await repo.DeleteAsync(vehicle.Id);
            Assert.Null(await repo.GetAsync(vehicle.Id));
            Assert.Empty(await repo.GetAllAsync());
            var tombstone = (await repo.GetAllIncludingDeletedAsync()).Single();
            Assert.True(tombstone.IsDeleted);
        }
        finally
        {
            // SqliteConnection pools its underlying OS file handle by default - without
            // clearing the pool first, File.Delete below intermittently fails with "file in use".
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EfObdSampleStore_AppendQueryCompactRoundTrip()
    {
        var dbPath = await NewMigratedDbAsync();
        try
        {
            var store = new EfObdSampleStore(NewFactory(dbPath));
            var vehicleId = Guid.NewGuid();
            // Fixed instant, not DateTimeOffset.UtcNow: 30s into the minute, so the 5 samples at
            // -4s..0s (below) stay at :26-:30, safely inside one minute-bucket regardless of
            // what wall-clock time the test happens to run at (a real-"now" baseline would
            // occasionally straddle a minute boundary and flakily split into two aggregates).
            var now = new DateTimeOffset(2026, 1, 1, 12, 30, 30, TimeSpan.Zero);

            var samples = Enumerable.Range(0, 5)
                .Select(i => new ObdSample
                {
                    VehicleId = vehicleId,
                    PidKey = "rpm",
                    Timestamp = now.AddSeconds(-i),
                    Value = 1000 + i,
                })
                .ToList();
            await store.AppendBatchAsync(samples);

            var queried = await store.QueryAsync(vehicleId, "rpm", now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(5, queried.Count);

            // Case-insensitive PidKey filter (matches the earlier JsonlObdSampleStore contract).
            var queriedUpper = await store.QueryAsync(vehicleId, "RPM", now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(5, queriedUpper.Count);

            // 1-minute bucket (matches the real per-minute retention aggregate the entity's own
            // doc comment describes) - a wider bucket (e.g. 1 hour) would floor the aggregate's
            // timestamp to an arbitrary point earlier in the current wall-clock hour, which
            // could land outside the +-5 minute query window below depending on what time the
            // test happens to run at.
            var compacted = await store.CompactAsync(vehicleId, now.AddMinutes(5), TimeSpan.FromMinutes(1));
            Assert.Equal(5, compacted);

            var afterCompact = await store.QueryAsync(vehicleId, "rpm", now.AddMinutes(-5), now.AddMinutes(5));
            var aggregate = Assert.Single(afterCompact);
            Assert.True(aggregate.IsAggregated);
            Assert.Equal(1002, aggregate.Value); // average of 1000..1004
        }
        finally
        {
            // SqliteConnection pools its underlying OS file handle by default - without
            // clearing the pool first, File.Delete below intermittently fails with "file in use".
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The upgrade path: an existing self-hosted deployment's pre-EF-Core data (per-entity JSON
    /// files + a samples-{id}.jsonl history file, written by the OLD JsonFileRepository/
    /// JsonlObdSampleStore - still in the codebase specifically to serve this importer, see its
    /// own doc comment) must survive the switch to SQLite instead of silently vanishing.
    /// </summary>
    [Fact]
    public async Task JsonToSqliteImporter_ImportsPreExistingJsonDataOnFirstRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), "obdgarage-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Legacy data written the OLD way, exactly as a real pre-upgrade deployment would have.
            var legacyVehicles = new JsonFileRepository<Vehicle>(dir);
            var vehicle = new Vehicle { Name = "Familienkombi", OwnerUserId = Guid.NewGuid() };
            await legacyVehicles.UpsertAsync(vehicle);

            var legacyTrips = new JsonFileRepository<Trip>(dir);
            var trip = new Trip { VehicleId = vehicle.Id, StartedAt = DateTimeOffset.UtcNow.AddHours(-1), DistanceKm = 12.3 };
            await legacyTrips.UpsertAsync(trip);

            var legacySamples = new JsonlObdSampleStore(dir);
            await legacySamples.AppendBatchAsync(
                [new ObdSample { VehicleId = vehicle.Id, PidKey = "rpm", Timestamp = DateTimeOffset.UtcNow, Value = 1500 }]);

            // First-ever startup against this dataDir under the new EF Core/SQLite storage.
            var dbFactory = new SqliteDbContextFactory(Path.Combine(dir, "obdgarage.db"));
            using (var migrationDb = dbFactory.CreateDbContext())
                await migrationDb.Database.MigrateAsync();
            await JsonToSqliteImporter.ImportIfNeededAsync(dir, dbWasCreatedFresh: true, dbFactory);

            var vehicles = new EfSyncRepository<Vehicle>(dbFactory);
            var importedVehicle = Assert.Single(await vehicles.GetAllAsync());
            Assert.Equal("Familienkombi", importedVehicle.Name);

            var trips = new EfSyncRepository<Trip>(dbFactory);
            var importedTrip = Assert.Single(await trips.GetAllAsync());
            Assert.Equal(vehicle.Id, importedTrip.VehicleId);
            Assert.Equal(12.3, importedTrip.DistanceKm, 1e-9);

            var samples = new EfObdSampleStore(dbFactory);
            var importedSamples = await samples.QueryAsync(
                vehicle.Id, "rpm", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
            var importedSample = Assert.Single(importedSamples);
            Assert.Equal(1500, importedSample.Value);

            // A SECOND startup against the same dataDir must NOT re-import (dbWasCreatedFresh is
            // now false) or duplicate anything - simulates every restart after the first.
            await JsonToSqliteImporter.ImportIfNeededAsync(dir, dbWasCreatedFresh: false, dbFactory);
            Assert.Single(await vehicles.GetAllAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
