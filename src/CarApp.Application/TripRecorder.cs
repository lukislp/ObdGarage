using CarApp.Core;
using CarApp.Obd.Pids;

namespace CarApp.Application;

/// <summary>
/// Automatisches Fahrtenbuch: erkennt Fahrtbeginn (Geschwindigkeit über Schwelle)
/// und Fahrtende (Stillstand über Timeout oder Verbindungsverlust),
/// integriert die Distanz aus den Geschwindigkeits-Samples (Trapezregel).
/// </summary>
public sealed class TripRecorder(IRepository<Trip> trips, IClock clock) : ITripTracker
{
    private DateTimeOffset _lastMovementAt;
    private double? _lastSpeed;
    private DateTimeOffset? _lastSpeedAt;
    private double _distanceKm;

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public double StartSpeedThresholdKmh { get; set; } = 3.0;
    /// <summary>Lücken über dieser Dauer fließen nicht in die Distanz ein (Verbindungsabbruch).</summary>
    public TimeSpan MaxIntegrationGap { get; set; } = TimeSpan.FromSeconds(30);

    public Guid VehicleId { get; set; }
    public TripCategory DefaultCategory { get; set; } = TripCategory.Private;

    public Trip? CurrentTrip { get; private set; }
    public double CurrentDistanceKm => Math.Round(_distanceKm, 3);

    public async ValueTask<Guid?> ProcessAsync(ObdReading reading, CancellationToken ct = default)
    {
        if (reading.PidKey != StandardPids.Speed.Key)
            return CurrentTrip?.Id;

        var speed = reading.Value;
        var now = reading.Timestamp;

        if (CurrentTrip is null)
        {
            if (speed >= StartSpeedThresholdKmh)
                await StartTripAsync(now, ct).ConfigureAwait(false);
            else
                return null;
        }

        // Distanz integrieren (Trapezregel), Lücken überspringen
        if (_lastSpeed is { } lastSpeed && _lastSpeedAt is { } lastAt)
        {
            var dt = now - lastAt;
            if (dt > TimeSpan.Zero && dt <= MaxIntegrationGap)
                _distanceKm += (lastSpeed + speed) / 2.0 * dt.TotalHours;
        }
        _lastSpeed = speed;
        _lastSpeedAt = now;

        if (speed >= StartSpeedThresholdKmh)
        {
            _lastMovementAt = now;
        }
        else if (now - _lastMovementAt >= IdleTimeout)
        {
            await EndTripAsync(now, ct).ConfigureAwait(false);
            return null;
        }

        return CurrentTrip?.Id;
    }

    /// <summary>Bei Verbindungsverlust/Zündung aus aufrufen — beendet eine laufende Fahrt.</summary>
    public Task NotifyDisconnectedAsync(CancellationToken ct = default) =>
        CurrentTrip is null ? Task.CompletedTask : EndTripAsync(clock.UtcNow, ct);

    private async Task StartTripAsync(DateTimeOffset at, CancellationToken ct)
    {
        CurrentTrip = new Trip
        {
            VehicleId = VehicleId,
            StartedAt = at,
            Category = DefaultCategory,
        };
        _distanceKm = 0;
        _lastSpeed = null;
        _lastSpeedAt = null;
        _lastMovementAt = at;
        await trips.UpsertAsync(CurrentTrip, ct).ConfigureAwait(false);
    }

    private async Task EndTripAsync(DateTimeOffset at, CancellationToken ct)
    {
        var trip = CurrentTrip!;
        trip.EndedAt = at;
        trip.DistanceKm = Math.Round(_distanceKm, 2);
        trip.Touch();
        await trips.UpsertAsync(trip, ct).ConfigureAwait(false);
        CurrentTrip = null;
        _lastSpeed = null;
        _lastSpeedAt = null;
    }
}
