using CarApp.Application;
using CarApp.Core;
using CarApp.Obd;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

namespace CarApp.Tests;

/// <summary>Minimal in-memory <see cref="IRepository{T}"/> fake for Application-layer tests.</summary>
internal sealed class InMemoryRepository<T> : IRepository<T> where T : SyncEntity
{
    private readonly Dictionary<Guid, T> _items = [];

    public Task<T?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var v) ? v : null);

    public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<T>>(_items.Values.Where(v => !v.IsDeleted).ToList());

    public Task UpsertAsync(T entity, CancellationToken ct = default)
    {
        _items[entity.Id] = entity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (_items.TryGetValue(id, out var v))
            v.IsDeleted = true;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// Regression tests for bugs found in the Application-layer services, all fixed.
/// </summary>
public class OdometerTrackerBugTests
{
    private static ReplayTransport NewInitializedScript()
    {
        var t = new ReplayTransport();
        t.OnCommand("ATZ", "\r\rELM327 v1.5\r\r>");
        foreach (var c in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0" })
            t.OnCommand(c, "OK\r\r>");
        return t;
    }

    /// <summary>
    /// FIXED (OdometerTracker.cs, TryReadFromObdAsync): unlike RecordManualAsync, the OBD read
    /// path used to apply no plausibility check at all before overwriting
    /// Vehicle.LastKnownOdometerKm, so a single flaky/garbled OBD response (garbage bytes, ECU
    /// reset, misdecoded PID) could silently regress the tracked odometer. It now applies the
    /// same "no backwards, no huge jump" check as manual entry, rejecting the bogus value.
    /// </summary>
    [Fact]
    public async Task TryReadFromObdAsync_RejectsImplausibleBackwardsJump()
    {
        var vehicles = new InMemoryRepository<Vehicle>();
        var readings = new InMemoryRepository<OdometerReading>();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var tracker = new OdometerTracker(vehicles, readings, clock);

        var vehicle = new Vehicle
        {
            Name = "Test",
            LastKnownOdometerKm = 50_000,
            OdometerSource = OdometerSource.ObdStandardPid,
        };
        await vehicles.UpsertAsync(vehicle);

        // Simulate a garbled/flaky OBD response that decodes to 0.0 km (e.g. a momentary
        // comms glitch, an ECU that was just reset, or a misrouted PID on a cheap clone).
        var transport = NewInitializedScript();
        transport.OnCommand("01A6", "41 A6 00 00 00 00\r\r>"); // decodes to 0.0 km

        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var result = await tracker.TryReadFromObdAsync(client, vehicle);

        // Fixed: the bogus 0.0 km reading is rejected (null) and the vehicle's trusted odometer
        // baseline is left untouched instead of jumping backwards from 50,000 to 0.
        Assert.Null(result);
        Assert.Equal(50_000, vehicle.LastKnownOdometerKm);
        Assert.Equal(OdometerSource.ObdStandardPid, vehicle.OdometerSource);
    }
}

public class TripRecorderBugTests
{
    /// <summary>
    /// FIXED (TripRecorder.cs ProcessAsync + ConnectionManager.cs): TripRecorder used to only
    /// end a trip when a NEW low-speed Speed sample arrived after IdleTimeout, or when
    /// NotifyDisconnectedAsync was explicitly called - and LiveDataService/ConnectionManager
    /// never called it on a hard transport failure. A genuine connection loss mid-trip (engine
    /// off cutting power to the dongle, Bluetooth range loss, app killed) left CurrentTrip open
    /// indefinitely, so a later, distinct drive got silently merged into the old trip record.
    /// ProcessAsync now treats a gap of MaxConnectionGap or more since the last sample as an
    /// implicit disconnect and ends the stale trip itself before starting the new one.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_SilentConnectionGap_EndsStaleTripAndStartsNewOne()
    {
        var trips = new InMemoryRepository<Trip>();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var recorder = new TripRecorder(trips, clock) { VehicleId = Guid.NewGuid() };

        var t0 = DateTimeOffset.UtcNow;

        // --- "Trip 1": a short 5-minute drive, then the OBD dongle loses power (engine off).
        // Nothing calls NotifyDisconnectedAsync here, exactly as with a hard transport failure.
        await recorder.ProcessAsync(new ObdReading(StandardPids.Speed.Key, 50, t0));
        var trip1Id = recorder.CurrentTrip!.Id;
        var trip1Start = recorder.CurrentTrip!.StartedAt;
        await recorder.ProcessAsync(new ObdReading(StandardPids.Speed.Key, 50, t0.AddMinutes(5)));
        var trip1LastSampleAt = t0.AddMinutes(5);

        // --- Car sits parked for 6 hours. No samples arrive at all (dongle unpowered). ---
        var t1 = t0.AddHours(6);

        // --- A completely separate, later drive starts. ---
        await recorder.ProcessAsync(new ObdReading(StandardPids.Speed.Key, 60, t1));
        var trip2Id = recorder.CurrentTrip!.Id;
        await recorder.ProcessAsync(new ObdReading(StandardPids.Speed.Key, 0, t1.AddSeconds(1)));
        // Let the idle timeout elapse so the second trip ends.
        await recorder.ProcessAsync(
            new ObdReading(StandardPids.Speed.Key, 0, t1.AddSeconds(1) + recorder.IdleTimeout));

        // Fixed: two separate trip records, not one merged trip spanning 6+ hours.
        Assert.NotEqual(trip1Id, trip2Id);

        var savedTrip1 = await trips.GetAsync(trip1Id);
        Assert.NotNull(savedTrip1);
        Assert.Equal(trip1Start, savedTrip1!.StartedAt);
        Assert.Equal(trip1LastSampleAt, savedTrip1.EndedAt);

        var savedTrip2 = await trips.GetAsync(trip2Id);
        Assert.NotNull(savedTrip2);
        Assert.Equal(t1, savedTrip2!.StartedAt);
    }
}

public class FuelStatisticsBugTests
{
    /// <summary>
    /// FIXED (FuelStatistics.cs, CostPerKm): the distance window is derived only from fuel
    /// entries that HAVE an odometer reading (bounded to [firstKm, lastKm]), but the cost
    /// numerator used to come from TotalCost(fuel, expenses) - summing ALL fuel entries and
    /// ALL expenses passed in, completely unfiltered by date or odometer range. Any one-off
    /// expense unrelated to this stretch of driving would massively inflate cost/km. CostPerKm
    /// now bounds both fuel and expenses to the same date window the distance was measured over.
    /// </summary>
    [Fact]
    public void CostPerKm_ExcludesExpensesOutsideTheDistanceWindow()
    {
        var vehicleId = Guid.NewGuid();
        var fuel = new List<FuelEntry>
        {
            new()
            {
                VehicleId = vehicleId, Date = new DateOnly(2026, 1, 1),
                Liters = 40, TotalPrice = 60m, OdometerKm = 10_000, FullTank = true,
            },
            new()
            {
                VehicleId = vehicleId, Date = new DateOnly(2026, 1, 10),
                Liters = 40, TotalPrice = 60m, OdometerKm = 10_500, FullTank = true,
            },
        };
        // Distance window implied by the fuel entries that have an odometer: 10,000 -> 10,500 km (500 km).

        var expenses = new List<Expense>
        {
            // A major repair from over a year before this fuel window — unrelated to these 500 km.
            new() { VehicleId = vehicleId, Date = new DateOnly(2024, 3, 1), Category = "Repair", Amount = 2000m },
        };

        var costPerKm = FuelStatistics.CostPerKm(fuel, expenses);

        Assert.NotNull(costPerKm);
        // Fixed: cost is now bounded to the same window as distance, so the unrelated repair
        // from over a year earlier is excluded: (60 + 60) / 500 = 0.24 EUR/km, not 4.24 EUR/km.
        Assert.Equal(0.24m, Math.Round(costPerKm!.Value, 2));
    }
}
