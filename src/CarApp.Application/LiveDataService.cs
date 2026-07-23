using CarApp.Core;
using CarApp.Obd;
using CarApp.Obd.Pids;

namespace CarApp.Application;

/// <summary>Ein einzelner, dekodierter Livewert.</summary>
public sealed record ObdReading(string PidKey, double Value, DateTimeOffset Timestamp);

/// <summary>Ordnet Readings einer laufenden Fahrt zu (implementiert vom TripRecorder).</summary>
public interface ITripTracker
{
    ValueTask<Guid?> ProcessAsync(ObdReading reading, CancellationToken ct = default);
}

/// <summary>
/// Polling-Konfiguration: schnelle Werte jeden Zyklus, langsame nur jeden N-ten.
/// ELM327-Klone schaffen oft nur 5–15 Anfragen/Sekunde — deshalb Prioritäten.
/// </summary>
public sealed class PollingProfile
{
    public required IReadOnlyList<PidDefinition> Fast { get; init; }
    public required IReadOnlyList<PidDefinition> Slow { get; init; }
    public int SlowEveryNCycles { get; init; } = 10;
    public TimeSpan CycleDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    public static PollingProfile Default => new()
    {
        Fast = [StandardPids.Rpm, StandardPids.Speed],
        Slow =
        [
            StandardPids.CoolantTemp, StandardPids.ControlModuleVoltage,
            StandardPids.IntakeTemp, StandardPids.EngineLoad,
        ],
    };

    public IEnumerable<PidDefinition> PidsForCycle(long cycle, IReadOnlySet<byte>? supportedPids)
    {
        var pids = (cycle - 1) % SlowEveryNCycles == 0 ? Fast.Concat(Slow) : Fast.AsEnumerable();
        return supportedPids is null ? pids : pids.Where(p => supportedPids.Contains(p.Pid));
    }
}

/// <summary>
/// Pollt Livewerte vom Fahrzeug, meldet sie an die UI (Event) und historisiert
/// JEDEN Wert als <see cref="ObdSample"/> — Kernanforderung: Verlauf ansehen können.
/// UI-frei; eine Blazor-Seite abonniert nur <see cref="ReadingReceived"/>.
/// </summary>
public sealed class LiveDataService(
    Elm327Client client, IObdSampleStore store, IClock clock, ITripTracker? tripTracker = null)
{
    private long _cycle;
    private Guid _vehicleId;
    private IReadOnlySet<byte>? _supportedPids;

    public PollingProfile Profile { get; set; } = PollingProfile.Default;

    public event Action<ObdReading>? ReadingReceived;

    /// <summary>Anzahl fehlgeschlagener PID-Abfragen seit Configure (Diagnose).</summary>
    public int FailedReads { get; private set; }

    public void Configure(Guid vehicleId, IReadOnlySet<byte>? supportedPids)
    {
        _vehicleId = vehicleId;
        _supportedPids = supportedPids;
        _cycle = 0;
        FailedReads = 0;
    }

    /// <summary>Ein Poll-Zyklus — deterministisch testbar, ohne Delay.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (_vehicleId == Guid.Empty)
            throw new InvalidOperationException("Configure(vehicleId, …) wurde nicht aufgerufen.");

        _cycle++;
        var batch = new List<ObdSample>();

        foreach (var pid in Profile.PidsForCycle(_cycle, _supportedPids))
        {
            double value;
            try
            {
                value = await client.ReadPidAsync(pid, ct).ConfigureAwait(false);
            }
            catch (ObdErrorException)
            {
                FailedReads++;
                continue;
            }

            var reading = new ObdReading(pid.Key, value, clock.UtcNow);
            Guid? tripId = null;
            if (tripTracker is not null)
                tripId = await tripTracker.ProcessAsync(reading, ct).ConfigureAwait(false);

            ReadingReceived?.Invoke(reading);
            batch.Add(new ObdSample
            {
                VehicleId = _vehicleId,
                TripId = tripId,
                PidKey = pid.Key,
                Timestamp = reading.Timestamp,
                Value = value,
            });
        }

        if (batch.Count > 0)
            await store.AppendBatchAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>Dauer-Polling bis zum Abbruch (für den echten Betrieb).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(Profile.CycleDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
