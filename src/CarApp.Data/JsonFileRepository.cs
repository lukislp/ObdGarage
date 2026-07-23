using System.Text.Json;
using CarApp.Core;

namespace CarApp.Data;

/// <summary>
/// Dependency-freie Persistenz: eine JSON-Datei pro Entitätstyp, atomare Schreibvorgänge
/// (tmp + Move), threadsicher. Erfüllt IRepository&lt;T&gt; aus Core — der spätere Umstieg
/// auf EF Core/SQLite ist ein reiner Austausch der Registrierung, kein UI-/Service-Umbau.
/// </summary>
public sealed class JsonFileRepository<T> : ISyncRepository<T> where T : SyncEntity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<Guid, T>? _cache;

    public JsonFileRepository(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, typeof(T).Name + ".json");
    }

    public async Task<T?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadAsync(ct).ConfigureAwait(false);
            return data.TryGetValue(id, out var e) && !e.IsDeleted ? e : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadAsync(ct).ConfigureAwait(false);
            return data.Values.Where(e => !e.IsDeleted).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<T>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadAsync(ct).ConfigureAwait(false);
            return data.Values.ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertAsync(T entity, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadAsync(ct).ConfigureAwait(false);
            data[entity.Id] = entity;
            await SaveAsync(data, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadAsync(ct).ConfigureAwait(false);
            if (data.TryGetValue(id, out var e))
            {
                e.IsDeleted = true;
                e.Touch();
                await SaveAsync(data, ct).ConfigureAwait(false);
            }
        }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<Guid, T>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;
        if (!File.Exists(_filePath))
            return _cache = [];

        await using var stream = File.OpenRead(_filePath);
        var list = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, ct)
            .ConfigureAwait(false) ?? [];
        return _cache = list.ToDictionary(e => e.Id);
    }

    private async Task SaveAsync(Dictionary<Guid, T> data, CancellationToken ct)
    {
        var tmp = _filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, data.Values.ToList(), JsonOptions, ct)
                .ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }
}
