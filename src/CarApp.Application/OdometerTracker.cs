using CarApp.Core;
using CarApp.Obd;
using CarApp.Obd.Pids;

namespace CarApp.Application;

/// <summary>
/// Odometer strategy (Plan 4.2): 1) standard PID A6, 2) manual with
/// plausibility check, 3) carry-forward via recorded trip distances.
/// </summary>
public sealed class OdometerTracker(
    IRepository<Vehicle> vehicles, IRepository<OdometerReading> readings, IClock clock)
{
    /// <summary>Maximum plausible jump for manual entry (protection against typos).</summary>
    public double MaxManualJumpKm { get; set; } = 15_000;

    /// <summary>Attempts to read the odometer via OBD (PID A6). Null if not supported.</summary>
    public async Task<double?> TryReadFromObdAsync(Elm327Client client, Vehicle vehicle, CancellationToken ct = default)
    {
        double km;
        try
        {
            km = await client.ReadPidAsync(StandardPids.Odometer, ct).ConfigureAwait(false);
        }
        catch (ObdErrorException)
        {
            return null;
        }

        // OBD beats everything: upgrade the source and save.
        await RecordAsync(vehicle, km, OdometerSource.ObdStandardPid, ct).ConfigureAwait(false);
        return km;
    }

    /// <summary>Manual entry with plausibility check (no running backwards, no huge jump).</summary>
    public async Task RecordManualAsync(Vehicle vehicle, double km, CancellationToken ct = default)
    {
        if (km < 0)
            throw new ArgumentOutOfRangeException(nameof(km), "Kilometerstand kann nicht negativ sein.");
        if (vehicle.LastKnownOdometerKm is { } last)
        {
            if (km < last)
                throw new ArgumentException(
                    $"Kilometerstand ({km:0.#} km) liegt unter dem letzten bekannten Wert ({last:0.#} km).");
            if (km - last > MaxManualJumpKm)
                throw new ArgumentException(
                    $"Sprung von {km - last:0.#} km wirkt unplausibel (max. {MaxManualJumpKm:0.#} km). Bitte prüfen.");
        }

        // Manual entry does not downgrade an OBD source
        var source = vehicle.OdometerSource == OdometerSource.ObdStandardPid
            ? OdometerSource.ObdStandardPid
            : OdometerSource.Manual;
        await RecordAsync(vehicle, km, source, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Carry-forward after a trip (only if the car doesn't provide an OBD odometer):
    /// last reading + distance driven, source "estimated".
    /// </summary>
    public async Task ApplyTripDistanceAsync(Vehicle vehicle, double distanceKm, CancellationToken ct = default)
    {
        if (vehicle.OdometerSource == OdometerSource.ObdStandardPid || distanceKm <= 0)
            return;
        if (vehicle.LastKnownOdometerKm is not { } last)
            return; // no baseline value, no carry-forward

        await RecordAsync(vehicle, last + distanceKm, OdometerSource.Estimated, ct).ConfigureAwait(false);
    }

    private async Task RecordAsync(Vehicle vehicle, double km, OdometerSource source, CancellationToken ct)
    {
        await readings.UpsertAsync(new OdometerReading
        {
            VehicleId = vehicle.Id,
            ValueKm = km,
            Source = source,
            Timestamp = clock.UtcNow,
        }, ct).ConfigureAwait(false);

        vehicle.LastKnownOdometerKm = km;
        vehicle.OdometerSource = source;
        vehicle.Touch();
        await vehicles.UpsertAsync(vehicle, ct).ConfigureAwait(false);
    }
}
