using Microsoft.EntityFrameworkCore;

namespace ObdGarage.Data;

/// <summary>
/// Minimal <see cref="IDbContextFactory{ObdGarageDbContext}"/> for a single SQLite file. Used by
/// every production host (Web/Server/MAUI) in place of ASP.NET Core's DI-only
/// AddDbContextFactory helper, and directly by tests that construct repositories without any DI
/// container at all - one implementation, no duplicated wiring between production and tests.
/// </summary>
public sealed class SqliteDbContextFactory(string dbPath) : IDbContextFactory<ObdGarageDbContext>
{
    public ObdGarageDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ObdGarageDbContext>().UseSqlite($"Data Source={dbPath}").Options);
}
