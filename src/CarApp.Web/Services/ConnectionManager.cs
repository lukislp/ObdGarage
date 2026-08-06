using System.Collections.Concurrent;
using CarApp.Application;
using CarApp.Core;
using CarApp.Obd;
using CarApp.Obd.Transport;

namespace CarApp.Web.Services;

/// <summary>Snapshot of a vehicle's live data (for /api/live/{id} and the UI).</summary>
public sealed record LiveSnapshot(
    bool Connected,
    string? TransportLabel,
    string? Vin,
    IReadOnlyList<ObdReading> Readings,
    bool TripActive,
    double TripDistanceKm,
    DateTimeOffset? TripStartedAt);

/// <summary>
/// Holds the active OBD connection per vehicle: Elm327Client + LiveDataService
/// (background polling) + TripRecorder (automatic trip logbook) + CTS.
/// Singleton — the interactive Blazor components access it through here.
/// </summary>
public sealed class ConnectionManager(
    IRepository<Vehicle> vehicles,
    IRepository<Trip> trips,
    IObdSampleStore sampleStore,
    OdometerTracker odometerTracker,
    IClock clock)
{
    private readonly ConcurrentDictionary<Guid, VehicleConnection> _connections = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public bool IsConnected(Guid vehicleId) => _connections.ContainsKey(vehicleId);

    public string? GetTransportLabel(Guid vehicleId) =>
        _connections.TryGetValue(vehicleId, out var c) ? c.TransportLabel : null;

    public string? GetVin(Guid vehicleId) =>
        _connections.TryGetValue(vehicleId, out var c) ? c.Vin : null;

    public Task ConnectSimulatorAsync(Guid vehicleId, CancellationToken ct = default)
    {
        var car = new SimulatedCar();
        return ConnectAsync(vehicleId, new SimulatedCarTransport(car), "Simulator", car, ct);
    }

    public Task ConnectWifiAsync(Guid vehicleId, string host, int port, CancellationToken ct = default) =>
        ConnectAsync(vehicleId, new WifiTcpTransport(host, port), $"WLAN {host}:{port}", null, ct);

    private async Task ConnectAsync(
        Guid vehicleId, IObdTransport transport, string label, SimulatedCar? sim, CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connections.ContainsKey(vehicleId))
                return; // already connected

            var vehicle = await vehicles.GetAsync(vehicleId, ct)
                ?? throw new InvalidOperationException("Fahrzeug nicht gefunden.");

            var client = new Elm327Client(transport);
            try
            {
                await client.InitializeAsync(ct);

                string? vin = null;
                try { vin = await client.ReadVinAsync(ct); }
                catch (ObdErrorException) { /* vehicle does not provide a VIN */ }

                var supported = await client.GetSupportedPidsAsync(ct);

                vehicle.SupportedPids = supported.OrderBy(p => p).ToList();
                if (!string.IsNullOrWhiteSpace(vin))
                    vehicle.Vin = vin;
                vehicle.Touch();
                await vehicles.UpsertAsync(vehicle, ct);

                var recorder = new TripRecorder(trips, clock) { VehicleId = vehicleId };
                var tracker = new TripEndTracker(recorder, trip => OnTripEndedAsync(vehicleId, trip));
                var live = new LiveDataService(client, sampleStore, clock, tracker);
                live.Configure(vehicleId, supported);

                var conn = new VehicleConnection
                {
                    Client = client,
                    Live = live,
                    Recorder = recorder,
                    Cts = new CancellationTokenSource(),
                    TransportLabel = label,
                    Vin = vin,
                };
                live.ReadingReceived += r => conn.Latest[r.PidKey] = r;

                conn.PollTask = Task.Run(() => live.RunAsync(conn.Cts.Token), CancellationToken.None);
                if (sim is not null)
                    conn.ProfileTask = Task.Run(() => DriveProfileAsync(sim, conn.Cts.Token), CancellationToken.None);

                _connections[vehicleId] = conn;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(Guid vehicleId)
    {
        if (!_connections.TryRemove(vehicleId, out var conn))
            return;

        conn.Cts.Cancel();
        try { if (conn.PollTask is not null) await conn.PollTask; } catch { /* cancellation */ }
        try { if (conn.ProfileTask is not null) await conn.ProfileTask; } catch { /* cancellation */ }

        // Cleanly end an ongoing trip and update the odometer reading.
        var running = conn.Recorder.CurrentTrip;
        if (running is not null)
        {
            await conn.Recorder.NotifyDisconnectedAsync();
            await OnTripEndedAsync(vehicleId, running);
        }

        conn.Cts.Dispose();
        await conn.Client.DisposeAsync();
    }

    public LiveSnapshot GetSnapshot(Guid vehicleId)
    {
        if (!_connections.TryGetValue(vehicleId, out var conn))
            return new LiveSnapshot(false, null, null, [], false, 0, null);

        var readings = conn.Latest.Values.OrderBy(r => r.PidKey, StringComparer.Ordinal).ToList();
        var trip = conn.Recorder.CurrentTrip;
        return new LiveSnapshot(
            true, conn.TransportLabel, conn.Vin, readings,
            trip is not null, conn.Recorder.CurrentDistanceKm, trip?.StartedAt);
    }

    /// <summary>Read the odometer reading via OBD (PID A6) — null if not connected or not supported.</summary>
    public async Task<double?> ReadOdometerFromObdAsync(Guid vehicleId, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(vehicleId, out var conn))
            return null;
        var vehicle = await vehicles.GetAsync(vehicleId, ct);
        if (vehicle is null)
            return null;
        return await odometerTracker.TryReadFromObdAsync(conn.Client, vehicle, ct);
    }

    /// <summary>Trip end: update the odometer reading using the distance driven (source "estimated").</summary>
    private async Task OnTripEndedAsync(Guid vehicleId, Trip trip)
    {
        try
        {
            var vehicle = await vehicles.GetAsync(vehicleId);
            if (vehicle is not null)
                await odometerTracker.ApplyTripDistanceAsync(vehicle, trip.DistanceKm);
        }
        catch
        {
            // Updating the odometer must never stop polling.
        }
    }

    /// <summary>
    /// Small drive profile for the simulator: accelerate, hold speed (with noise),
    /// brake, brief idle phase — so something visibly happens on the live dashboard.
    /// </summary>
    private static async Task DriveProfileAsync(SimulatedCar car, CancellationToken ct)
    {
        var rnd = new Random();
        double t = 0;
        const double cycle = 150; // seconds per drive cycle

        while (!ct.IsCancellationRequested)
        {
            var phase = t % cycle;
            double target;
            if (phase < 25) target = phase / 25.0 * 70;                          // accelerate
            else if (phase < 110) target = 70 + 25 * Math.Sin((phase - 25) / 12.0); // driving
            else if (phase < 125) target = Math.Max(0, 70 * (1 - (phase - 110) / 12.0)); // braking
            else target = 0;                                                     // idle

            var speed = target <= 0 ? 0 : Math.Clamp(target + rnd.NextDouble() * 4 - 2, 0, 220);
            car.SpeedKmh = speed;
            car.Rpm = speed <= 0.5
                ? 780 + rnd.NextDouble() * 60
                : 900 + speed * 34 + rnd.NextDouble() * 120;
            car.EngineLoadPct = Math.Clamp(12 + speed / 2.2 + rnd.NextDouble() * 5, 5, 95);
            car.CoolantTempC = Math.Min(96, car.CoolantTempC + 0.03);
            car.VoltageV = 13.6 + rnd.NextDouble() * 0.5;
            car.OdometerKm += speed / 3600.0;

            t += 1;
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Passes readings through to the TripRecorder and detects the transition
    /// "trip active" → "trip ended" (idle timeout), to update the odometer reading.
    /// </summary>
    private sealed class TripEndTracker(TripRecorder inner, Func<Trip, Task> onTripEnded) : ITripTracker
    {
        public async ValueTask<Guid?> ProcessAsync(ObdReading reading, CancellationToken ct = default)
        {
            var before = inner.CurrentTrip;
            var result = await inner.ProcessAsync(reading, ct);
            if (before is not null && inner.CurrentTrip is null)
                await onTripEnded(before); // EndTripAsync has already set Distance/EndedAt
            return result;
        }
    }

    private sealed class VehicleConnection
    {
        public required Elm327Client Client { get; init; }
        public required LiveDataService Live { get; init; }
        public required TripRecorder Recorder { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required string TransportLabel { get; init; }
        public string? Vin { get; init; }
        public Task? PollTask { get; set; }
        public Task? ProfileTask { get; set; }
        public ConcurrentDictionary<string, ObdReading> Latest { get; } = new();
    }
}
