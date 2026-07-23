using CarApp.Core;
using CarApp.Obd;
using CarApp.Obd.Pids;

namespace CarApp.Application;

/// <summary>
/// Kilometerstand-Strategie (Plan 4.2): 1) Standard-PID A6, 2) manuell mit
/// Plausibilitätsprüfung, 3) Fortschreibung über aufgezeichnete Fahrtdistanzen.
/// </summary>
public sealed class OdometerTracker(
    IRepository<Vehicle> vehicles, IRepository<OdometerReading> readings, IClock clock)
{
    /// <summary>Maximal plausibler Sprung bei manueller Eingabe (Schutz vor Tippfehlern).</summary>
    public double MaxManualJumpKm { get; set; } = 15_000;

    /// <summary>Versucht, den km-Stand per OBD (PID A6) zu lesen. Null, wenn nicht unterstützt.</summary>
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

        // OBD schlägt alles: Quelle hochstufen und speichern.
        await RecordAsync(vehicle, km, OdometerSource.ObdStandardPid, ct).ConfigureAwait(false);
        return km;
    }

    /// <summary>Manuelle Eingabe mit Plausibilitätsprüfung (kein Rückwärtslaufen, kein Riesensprung).</summary>
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

        // Manuelle Eingabe stuft eine OBD-Quelle nicht herab
        var source = vehicle.OdometerSource == OdometerSource.ObdStandardPid
            ? OdometerSource.ObdStandardPid
            : OdometerSource.Manual;
        await RecordAsync(vehicle, km, source, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fortschreibung nach einer Fahrt (nur wenn das Auto kein OBD-Odometer liefert):
    /// letzter Stand + gefahrene Distanz, Quelle "geschätzt".
    /// </summary>
    public async Task ApplyTripDistanceAsync(Vehicle vehicle, double distanceKm, CancellationToken ct = default)
    {
        if (vehicle.OdometerSource == OdometerSource.ObdStandardPid || distanceKm <= 0)
            return;
        if (vehicle.LastKnownOdometerKm is not { } last)
            return; // ohne Basiswert keine Fortschreibung

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
