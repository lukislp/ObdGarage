using Microsoft.EntityFrameworkCore;

namespace CarApp.Data;

/// <summary>
/// Minimal <see cref="IDbContextFactory{CarAppDbContext}"/> for a single SQLite file. Used by
/// every production host (Web/Server/MAUI) in place of ASP.NET Core's DI-only
/// AddDbContextFactory helper, and directly by tests that construct repositories without any DI
/// container at all - one implementation, no duplicated wiring between production and tests.
/// </summary>
public sealed class SqliteDbContextFactory(string dbPath) : IDbContextFactory<CarAppDbContext>
{
    public CarAppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CarAppDbContext>().UseSqlite($"Data Source={dbPath}").Options);
}
