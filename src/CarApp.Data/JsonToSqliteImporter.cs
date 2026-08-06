using CarApp.Core;
using Microsoft.EntityFrameworkCore;

namespace CarApp.Data;

/// <summary>
/// One-time upgrade path from the pre-EF-Core storage (one JSON file per entity type, plus
/// per-vehicle samples-{id}.jsonl history files) to the SQLite database. Only does anything
/// when the caller reports the SQLite file was just created fresh by this very startup - an
/// existing/already-migrated database is never touched, so this is safe to call unconditionally
/// on every startup. <see cref="JsonFileRepository{T}"/>/<see cref="JsonlObdSampleStore"/> are
/// no longer the active production repositories (see EfSyncRepository/EfObdSampleStore), but
/// stay in the codebase specifically to serve as this importer's read side - the safest way to
/// migrate is reusing the exact same reader the old data was written by, not re-implementing it.
/// </summary>
public static class JsonToSqliteImporter
{
    public static async Task ImportIfNeededAsync(
        string dataDir, bool dbWasCreatedFresh, IDbContextFactory<CarAppDbContext> factory,
        CancellationToken ct = default)
    {
        if (!dbWasCreatedFresh)
            return;

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var vehicleIds = await ImportEntitiesAsync<Vehicle>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<AdapterProfile>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<OdometerReading>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<Trip>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<MaintenanceTask>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<FuelEntry>(dataDir, db, ct).ConfigureAwait(false);
        await ImportEntitiesAsync<Expense>(dataDir, db, ct).ConfigureAwait(false);

        // Sample history is per-vehicle (samples-{id}.jsonl) - only reachable once the vehicle
        // IDs the JSON import above just found are known.
        if (vehicleIds.Count > 0)
        {
            var legacySamples = new JsonlObdSampleStore(dataDir);
            foreach (var vehicleId in vehicleIds)
            {
                var samples = await legacySamples.QueryAsync(
                        vehicleId, null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct)
                    .ConfigureAwait(false);
                if (samples.Count > 0)
                    db.ObdSamples.AddRange(samples);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Guid>> ImportEntitiesAsync<T>(
        string dataDir, CarAppDbContext db, CancellationToken ct)
        where T : SyncEntity
    {
        if (!File.Exists(Path.Combine(dataDir, typeof(T).Name + ".json")))
            return [];

        var legacy = new JsonFileRepository<T>(dataDir);
        var entities = await legacy.GetAllIncludingDeletedAsync(ct).ConfigureAwait(false);
        if (entities.Count > 0)
            db.Set<T>().AddRange(entities);
        return entities.Select(e => e.Id).ToList();
    }
}
