using CarApp.Core;
using Microsoft.EntityFrameworkCore;

namespace CarApp.Data;

/// <summary>
/// EF Core/SQLite-backed <see cref="ISyncRepository{T}"/> - same contract as the earlier
/// JsonFileRepository (soft delete, IDs generated client-side), replacing it as the
/// production persistence. Uses <see cref="IDbContextFactory{TContext}"/> rather than holding
/// one shared <see cref="DbContext"/>, since this repository is registered as a singleton and
/// DbContext instances are not thread-safe for concurrent use.
/// </summary>
public sealed class EfSyncRepository<T>(IDbContextFactory<CarAppDbContext> factory) : ISyncRepository<T>
    where T : SyncEntity
{
    public async Task<T?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<T>().FindAsync([id], ct).ConfigureAwait(false);
        return entity is { IsDeleted: false } ? entity : null;
    }

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<T>().Where(e => !e.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<T>().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(T entity, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<T>().FindAsync([entity.Id], ct).ConfigureAwait(false);
        if (existing is null)
            db.Add(entity);
        else
            db.Entry(existing).CurrentValues.SetValues(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<T>().FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null)
            return;
        entity.IsDeleted = true;
        entity.Touch();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
