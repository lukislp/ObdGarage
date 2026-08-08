using ObdGarage.Core;
using Microsoft.EntityFrameworkCore;

namespace ObdGarage.Data;

/// <summary>
/// EF Core/SQLite-backed <see cref="IObdSampleStore"/> - same contract as the earlier
/// JsonlObdSampleStore (per-vehicle append-only history, retention compaction into
/// per-bucket min/avg/max aggregates), replacing it as the production persistence.
/// </summary>
public sealed class EfObdSampleStore(IDbContextFactory<ObdGarageDbContext> factory) : IObdSampleStore
{
    public async Task AppendBatchAsync(IReadOnlyList<ObdSample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0)
            return;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ObdSamples.AddRange(samples);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ObdSample>> QueryAsync(
        Guid vehicleId, string? pidKey, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ObdSamples
            .Where(s => s.VehicleId == vehicleId && s.Timestamp >= from && s.Timestamp <= to)
            .Where(s => pidKey == null || s.PidKey == pidKey) // PidKey column uses a NOCASE collation
            .OrderBy(s => s.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CompactAsync(
        Guid vehicleId, DateTimeOffset olderThan, TimeSpan bucket, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var toCompact = await db.ObdSamples
            .Where(s => s.VehicleId == vehicleId && !s.IsAggregated && s.Timestamp < olderThan)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (toCompact.Count == 0)
            return 0;

        var aggregates = toCompact
            .GroupBy(s => (s.PidKey, Bucket: FloorTo(s.Timestamp, bucket)))
            .Select(g => new ObdSample
            {
                VehicleId = vehicleId,
                PidKey = g.Key.PidKey,
                Timestamp = g.Key.Bucket,
                Value = g.Average(s => s.Value),
                MinValue = g.Min(s => s.Value),
                MaxValue = g.Max(s => s.Value),
                IsAggregated = true,
            });

        db.ObdSamples.RemoveRange(toCompact);
        db.ObdSamples.AddRange(aggregates);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return toCompact.Count;
    }

    private static DateTimeOffset FloorTo(DateTimeOffset t, TimeSpan bucket) =>
        new(t.UtcTicks - t.UtcTicks % bucket.Ticks, TimeSpan.Zero);
}
