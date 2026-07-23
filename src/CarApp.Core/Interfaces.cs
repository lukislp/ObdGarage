namespace CarApp.Core;

/// <summary>Testbare Zeitquelle — Services nehmen nie direkt DateTimeOffset.UtcNow.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Generisches Repository für synchronisierbare Entitäten (Soft Delete).</summary>
public interface IRepository<T> where T : SyncEntity
{
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Alle nicht gelöschten Entitäten.</summary>
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(T entity, CancellationToken ct = default);
    /// <summary>Soft Delete: markiert als gelöscht, bleibt für den Sync erhalten.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Repository mit Sicht auf gelöschte Einträge — der Sync muss Soft-Deletes übertragen
/// und Last-Write-Wins auch gegen gelöschte Datensätze (Tombstones) prüfen können.
/// </summary>
public interface ISyncRepository<T> : IRepository<T> where T : SyncEntity
{
    /// <summary>Alle Entitäten inklusive der als gelöscht markierten.</summary>
    Task<IReadOnlyList<T>> GetAllIncludingDeletedAsync(CancellationToken ct = default);
}

/// <summary>
/// Speicher für die Livewerte-Historie (append-only, hohes Volumen).
/// Bewusst vom generischen Repository getrennt.
/// </summary>
public interface IObdSampleStore
{
    Task AppendBatchAsync(IReadOnlyList<ObdSample> samples, CancellationToken ct = default);

    /// <summary>Verlaufsabfrage für die Chart-Ansicht.</summary>
    Task<IReadOnlyList<ObdSample>> QueryAsync(
        Guid vehicleId, string? pidKey, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Retention: verdichtet Rohwerte, die älter als <paramref name="olderThan"/> sind,
    /// zu Aggregaten pro <paramref name="bucket"/> (Min/Avg/Max). Liefert die Zahl entfernter Rohwerte.
    /// </summary>
    Task<int> CompactAsync(Guid vehicleId, DateTimeOffset olderThan, TimeSpan bucket,
        CancellationToken ct = default);
}
