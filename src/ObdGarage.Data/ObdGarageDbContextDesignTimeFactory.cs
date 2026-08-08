using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ObdGarage.Data;

/// <summary>
/// Only used by the "dotnet ef migrations add/update" tooling at design time - the connection
/// string here is never used at runtime (Program.cs/ServerApp.cs configure the real one, pointed
/// at dataDir/obdgarage.db).
/// </summary>
public sealed class ObdGarageDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ObdGarageDbContext>
{
    public ObdGarageDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ObdGarageDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new ObdGarageDbContext(options);
    }
}
