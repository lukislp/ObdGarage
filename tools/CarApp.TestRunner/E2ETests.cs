using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using CarApp.Obd;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

namespace CarApp.TestRunner;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan d) => UtcNow += d;
}

public static class E2ETests
{
    public static async Task RunAsync(Action<string, bool> check, Action<string, double, double, double> checkEqual)
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clock = new FakeClock();

        var car = new SimulatedCar();
        var transport = new SimulatedCarTransport(car);
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        Console.WriteLine("Simulator + Client:");
        var vin = await client.ReadVinAsync();
        check("VIN vom Simulator gelesen", vin == car.Vin);
        var supported = await client.GetSupportedPidsAsync();
        check("Supported-Scan findet RPM/Speed/Spannung/Odometer",
            supported.Contains(0x0C) && supported.Contains(0x0D) &&
            supported.Contains(0x42) && supported.Contains(0xA6));
        checkEqual("Odometer über Simulator", 123456.7, await client.ReadPidAsync(StandardPids.Odometer), 1e-3);
        checkEqual("ATRV über Simulator", 13.8, await client.ReadAdapterVoltageAsync() ?? double.NaN, 1e-3);

        var store = new JsonlObdSampleStore(dir);
        var trips = new JsonFileRepository<Trip>(dir);
        var vehicles = new JsonFileRepository<Vehicle>(dir);
        var odoReadings = new JsonFileRepository<OdometerReading>(dir);

        var vehicle = new Vehicle { Name = "Testwagen", Vin = vin, OwnerUserId = Guid.NewGuid() };
        await vehicles.UpsertAsync(vehicle);

        var recorder = new TripRecorder(trips, clock)
        {
            VehicleId = vehicle.Id,
            IdleTimeout = TimeSpan.FromSeconds(60),
        };
        var live = new LiveDataService(client, store, clock, recorder);
        live.Configure(vehicle.Id, supported);

        Console.WriteLine("Polling + Livewerte-Historie:");
        var t0 = clock.UtcNow.AddMinutes(-5);
        car.SpeedKmh = 0;
        car.Rpm = 800;
        await live.PollOnceAsync();                       // Zyklus 1: fast + slow
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.PollOnceAsync();                       // Zyklus 2: nur fast
        var early = await store.QueryAsync(vehicle.Id, null, t0, clock.UtcNow);
        check($"Jeder gepollte Wert historisiert (8 erwartet, war {early.Count})", early.Count == 8);
        check("Im Stand keine Fahrt-Zuordnung", early.All(s => s.TripId is null));
        check("Keine fehlgeschlagenen Reads", live.FailedReads == 0);

        Console.WriteLine("Automatisches Fahrtenbuch:");
        car.SpeedKmh = 54;
        car.Rpm = 2200;
        for (int i = 0; i < 61; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await live.PollOnceAsync();
        }
        check("Fahrt läuft nach Losfahren", recorder.CurrentTrip is not null);
        checkEqual("Distanz nach 60 s bei 54 km/h ≈ 0,9 km", 0.9, recorder.CurrentDistanceKm, 0.02);

        car.SpeedKmh = 0;
        car.Rpm = 800;
        for (int i = 0; i < 70; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await live.PollOnceAsync();
        }
        check("Fahrt nach Stillstands-Timeout beendet", recorder.CurrentTrip is null);
        var allTrips = await trips.GetAllAsync();
        check("Genau eine Fahrt persistiert, mit Ende", allTrips.Count == 1 && allTrips[0].EndedAt is not null);
        var trip = allTrips[0];
        check($"Fahrtdistanz plausibel ({trip.DistanceKm} km)", trip.DistanceKm is > 0.85 and < 1.0);
        var speedSamples = await store.QueryAsync(vehicle.Id, StandardPids.Speed.Key, t0, clock.UtcNow);
        check("Speed-Samples während der Fahrt tragen die TripId", speedSamples.Any(s => s.TripId == trip.Id));
        check("Samples im Stand tragen keine TripId", speedSamples.First().TripId is null);

        Console.WriteLine("Kilometerstand-Strategie:");
        var odo = new OdometerTracker(vehicles, odoReadings, clock);
        var kmObd = await odo.TryReadFromObdAsync(client, vehicle);
        checkEqual("PID A6 gelesen", 123456.7, kmObd ?? double.NaN, 1e-3);
        check("Quelle hochgestuft auf OBD", vehicle.OdometerSource == OdometerSource.ObdStandardPid);
        var rejected = false;
        try { await odo.RecordManualAsync(vehicle, 123_000); }
        catch (ArgumentException) { rejected = true; }
        check("Rückwärtslaufender km-Stand abgelehnt", rejected);
        rejected = false;
        try { await odo.RecordManualAsync(vehicle, 999_999); }
        catch (ArgumentException) { rejected = true; }
        check("Unplausibler Riesensprung abgelehnt", rejected);

        var oldCar = new SimulatedCar { SupportsOdometer = false, Vin = "WSIMOLD0000000002" };
        await using var oldClient = new Elm327Client(new SimulatedCarTransport(oldCar));
        await oldClient.InitializeAsync();
        var oldVehicle = new Vehicle { Name = "Alter Wagen", OwnerUserId = vehicle.OwnerUserId };
        await vehicles.UpsertAsync(oldVehicle);
        check("Ohne A6-Unterstützung → null (kein Fehler)",
            await odo.TryReadFromObdAsync(oldClient, oldVehicle) is null);
        await odo.RecordManualAsync(oldVehicle, 200_000);
        await odo.ApplyTripDistanceAsync(oldVehicle, 12.5);
        checkEqual("Fortschreibung: 200000 + 12,5", 200_012.5, oldVehicle.LastKnownOdometerKm ?? double.NaN, 1e-6);
        check("Quelle = geschätzt", oldVehicle.OdometerSource == OdometerSource.Estimated);
        await odo.ApplyTripDistanceAsync(vehicle, 12.5);
        checkEqual("OBD-Quelle wird NICHT fortgeschrieben", 123456.7, vehicle.LastKnownOdometerKm ?? double.NaN, 1e-3);

        Console.WriteLine("Fehlercodes (DTC):");
        check("Ohne gespeicherte Codes → leere Liste (kein Fehler)",
            (await client.ReadDtcsAsync()).Count == 0);
        car.Dtcs = ["P0301", "P0420"];
        car.PendingDtcs = ["P0133"];
        var stored = await client.ReadDtcsAsync();
        check($"Gespeicherte Codes gelesen (war {string.Join(",", stored)})",
            stored.SequenceEqual(["P0301", "P0420"]));
        var pending = await client.ReadDtcsAsync(pending: true);
        check($"Anstehende Codes über eigenes Kommando (war {string.Join(",", pending)})",
            pending.SequenceEqual(["P0133"]));
        check("Beschreibung für bekannten Code vorhanden",
            DtcDescriptions.Describe("P0420") is not null);
        car.Dtcs = [];
        car.PendingDtcs = [];

        Console.WriteLine("Wartungsplaner:");
        var today = new DateOnly(2026, 7, 22);
        var oil = new MaintenanceTask
        {
            VehicleId = vehicle.Id, Type = MaintenanceType.OilChange, Title = "Ölwechsel",
            IntervalKm = 15_000, LastDoneAtKm = 110_000,
        };
        var s1 = MaintenanceCalculator.GetStatus(oil, 124_500, today);
        check("Ölwechsel: 500 km übrig → gelb", s1.RemainingKm == 500 && s1.Badge == DueBadge.Yellow && !s1.IsOverdue);
        var s2 = MaintenanceCalculator.GetStatus(oil, 125_100, today);
        check("Ölwechsel überfällig → rot", s2.IsOverdue && s2.Badge == DueBadge.Red);
        var tuv = new MaintenanceTask
        {
            VehicleId = vehicle.Id, Type = MaintenanceType.Inspection, Title = "TÜV",
            FixedDueDate = today.AddDays(30),
        };
        var s3 = MaintenanceCalculator.GetStatus(tuv, null, today);
        check("TÜV in 30 Tagen → gelb", s3.RemainingDays == 30 && s3.Badge == DueBadge.Yellow);
        var s4 = MaintenanceCalculator.GetStatus(
            new MaintenanceTask { VehicleId = vehicle.Id, Title = "Inspektion", FixedDueDate = today.AddDays(200) },
            null, today);
        check("Inspektion in 200 Tagen → grün", s4.Badge == DueBadge.Green);
        var urgent = MaintenanceCalculator.MostUrgent([oil, tuv], 124_500, today);
        check("Dringendste Aufgabe = Ölwechsel", urgent!.Task.Title == "Ölwechsel");

        Console.WriteLine("Verbrauch + Kosten:");
        var vid = vehicle.Id;
        List<FuelEntry> fuel =
        [
            new() { VehicleId = vid, Date = new(2026, 6, 1), Liters = 40, TotalPrice = 68m, OdometerKm = 1000, FullTank = true },
            new() { VehicleId = vid, Date = new(2026, 6, 15), Liters = 35, TotalPrice = 61m, OdometerKm = 1500, FullTank = true },
            new() { VehicleId = vid, Date = new(2026, 7, 1), Liters = 36, TotalPrice = 60m, OdometerKm = 2000, FullTank = true },
        ];
        checkEqual("Verbrauch 7,1 l/100km", 7.1, FuelStatistics.ConsumptionPer100Km(fuel) ?? double.NaN, 1e-6);
        check("Kosten pro km = 0,189 €", FuelStatistics.CostPerKm(fuel, []) == 0.189m);
        check("Verbrauch braucht 2 Volltank-Einträge",
            FuelStatistics.ConsumptionPer100Km([fuel[0]]) is null);

        Console.WriteLine("Retention (Rohwerte → Minuten-Aggregate):");
        var rStore = new JsonlObdSampleStore(Path.Combine(dir, "retention"));
        var v2 = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var raw = new List<ObdSample>();
        for (int i = 0; i < 600; i++)  // 10 Minuten à 1 Hz
            raw.Add(new ObdSample { VehicleId = v2, PidKey = "rpm", Timestamp = start.AddSeconds(i), Value = 1000 + i % 60 });
        await rStore.AppendBatchAsync(raw);
        var removed = await rStore.CompactAsync(v2, start.AddMinutes(5), TimeSpan.FromMinutes(1));
        check($"300 Rohwerte verdichtet (war {removed})", removed == 300);
        var after = await rStore.QueryAsync(v2, "rpm", start, start.AddMinutes(11));
        var aggs = after.Where(s => s.IsAggregated).ToList();
        check($"5 Minuten-Aggregate (war {aggs.Count})", aggs.Count == 5);
        check("Aggregate tragen Min=1000/Max=1059", aggs.All(a => a.MinValue == 1000 && a.MaxValue == 1059));
        checkEqual("Aggregat-Durchschnitt 1029,5", 1029.5, aggs[0].Value, 1e-6);
        check($"Restbestand 305 (300 roh + 5 agg, war {after.Count})", after.Count == 305);
        var secondRun = await rStore.CompactAsync(v2, start.AddMinutes(5), TimeSpan.FromMinutes(1));
        check("Kompaktierung ist idempotent", secondRun == 0);

        Console.WriteLine("Persistenz über Neustart:");
        var vehiclesReloaded = new JsonFileRepository<Vehicle>(dir);
        var reloaded = await vehiclesReloaded.GetAllAsync();
        check("Fahrzeuge nach Neustart geladen", reloaded.Count == 2 && reloaded.Any(v => v.Name == "Testwagen"));
        var reloadedVehicle = reloaded.First(v => v.Name == "Testwagen");
        checkEqual("km-Stand überlebte Neustart", 123456.7, reloadedVehicle.LastKnownOdometerKm ?? double.NaN, 1e-3);
        await vehiclesReloaded.DeleteAsync(oldVehicle.Id);
        var vehiclesAgain = new JsonFileRepository<Vehicle>(dir);
        check("Soft Delete überlebt Neustart",
            (await vehiclesAgain.GetAllAsync()).Count == 1 &&
            await vehiclesAgain.GetAsync(oldVehicle.Id) is null);

        Directory.Delete(dir, recursive: true);
    }
}
