using ObdGarage.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ObdGarage.Data;

/// <summary>
/// EF Core model for every <see cref="SyncEntity"/> plus the live-value sample history.
/// One SQLite file per deployment (dataDir/obdgarage.db) - replaces the earlier one-JSON-
/// file-per-entity-type storage. <see cref="EfSyncRepository{T}"/>/<see cref="EfObdSampleStore"/>
/// are the only callers; everything above <see cref="ObdGarage.Core.IRepository{T}"/> is unaffected.
/// </summary>
public sealed class ObdGarageDbContext(DbContextOptions<ObdGarageDbContext> options) : DbContext(options)
{
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<AdapterProfile> AdapterProfiles => Set<AdapterProfile>();
    public DbSet<OdometerReading> OdometerReadings => Set<OdometerReading>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<MaintenanceTask> MaintenanceTasks => Set<MaintenanceTask>();
    public DbSet<FuelEntry> FuelEntries => Set<FuelEntry>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ObdSample> ObdSamples => Set<ObdSample>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // The SQLite provider's default DateTimeOffset mapping (ISO-8601 TEXT) fails to
        // translate range comparisons (>=, <=) at all - EfObdSampleStore.QueryAsync's
        // Timestamp >= from && Timestamp <= to throws InvalidOperationException. Storing as
        // UTC ticks (a plain long) sidesteps the whole class of DateTimeOffset-translation
        // issues: every comparison/ordering becomes a trivial integer comparison. Applies to
        // every DateTimeOffset (and DateTimeOffset?) property in the model, not just
        // ObdSample.Timestamp - the same translation gap could bite any future date-range query.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every SyncEntity subtype: primary key + an index on the columns the repositories'
        // own query patterns actually filter/sort on (IsDeleted for GetAllAsync, VehicleId for
        // every child-entity lookup, Timestamp range scans for the sample history).
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var idProperty = entity.FindProperty(nameof(SyncEntity.Id));
            if (idProperty is not null)
                idProperty.ValueGenerated = ValueGenerated.Never;
        }

        modelBuilder.Entity<Vehicle>(b =>
        {
            b.HasIndex(v => v.IsDeleted);
            // List<byte> has no built-in SQLite mapping - stored as a compact byte[] BLOB
            // instead (element order doesn't matter for this set, but preserving it is free).
            b.Property(v => v.SupportedPids)
                .HasConversion(
                    list => list.ToArray(),
                    bytes => bytes.ToList());
        });

        modelBuilder.Entity<AdapterProfile>(b => b.HasIndex(a => a.VehicleId));
        modelBuilder.Entity<OdometerReading>(b => b.HasIndex(o => o.VehicleId));
        modelBuilder.Entity<Trip>(b => b.HasIndex(t => t.VehicleId));
        modelBuilder.Entity<MaintenanceTask>(b => b.HasIndex(m => m.VehicleId));
        modelBuilder.Entity<FuelEntry>(b => b.HasIndex(f => f.VehicleId));
        modelBuilder.Entity<Expense>(b => b.HasIndex(e => e.VehicleId));

        modelBuilder.Entity<ObdSample>(b =>
        {
            // The store's own query shape: one vehicle, optionally one PID, a timestamp range.
            b.HasIndex(s => new { s.VehicleId, s.PidKey, s.Timestamp });
            // Matches JsonlObdSampleStore's case-insensitive PidKey comparison
            // (StringComparison.OrdinalIgnoreCase) - PID keys are always lowercase in
            // practice, but the query contract itself doesn't require the caller to match case.
            b.Property(s => s.PidKey).UseCollation("NOCASE");
        });
    }

    private sealed class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(
        dto => dto.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
