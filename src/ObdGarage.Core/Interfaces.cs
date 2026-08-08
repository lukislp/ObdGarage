namespace ObdGarage.Core;

/// <summary>Testable time source — services never take DateTimeOffset.UtcNow directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Generic repository for synchronizable entities (soft delete).</summary>
public interface IRepository<T> where T : SyncEntity
{
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>All non-deleted entities.</summary>
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(T entity, CancellationToken ct = default);
    /// <summary>Soft delete: marks as deleted, retained for sync.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Repository with visibility into deleted entries — sync must be able to transmit soft-deletes
/// and check last-write-wins even against deleted records (tombstones).
/// </summary>
public interface ISyncRepository<T> : IRepository<T> where T : SyncEntity
{
    /// <summary>All entities including those marked as deleted.</summary>
    Task<IReadOnlyList<T>> GetAllIncludingDeletedAsync(CancellationToken ct = default);
}

/// <summary>
/// Store for the live-value history (append-only, high volume).
/// Deliberately separated from the generic repository.
/// </summary>
public interface IObdSampleStore
{
    Task AppendBatchAsync(IReadOnlyList<ObdSample> samples, CancellationToken ct = default);

    /// <summary>History query for the chart view.</summary>
    Task<IReadOnlyList<ObdSample>> QueryAsync(
        Guid vehicleId, string? pidKey, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Retention: compacts raw values older than <paramref name="olderThan"/>
    /// into aggregates per <paramref name="bucket"/> (min/avg/max). Returns the number of raw values removed.
    /// </summary>
    Task<int> CompactAsync(Guid vehicleId, DateTimeOffset olderThan, TimeSpan bucket,
        CancellationToken ct = default);
}
