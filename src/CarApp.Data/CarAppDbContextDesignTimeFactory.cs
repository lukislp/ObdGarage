using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarApp.Data;

/// <summary>
/// Only used by the "dotnet ef migrations add/update" tooling at design time - the connection
/// string here is never used at runtime (Program.cs/ServerApp.cs configure the real one, pointed
/// at dataDir/carapp.db).
/// </summary>
public sealed class CarAppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CarAppDbContext>
{
    public CarAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CarAppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new CarAppDbContext(options);
    }
}
