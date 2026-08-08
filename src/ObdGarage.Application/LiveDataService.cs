using ObdGarage.Core;
using ObdGarage.Obd;
using ObdGarage.Obd.Pids;

namespace ObdGarage.Application;

/// <summary>A single, decoded live value.</summary>
public sealed record ObdReading(string PidKey, double Value, DateTimeOffset Timestamp);

/// <summary>Assigns readings to an ongoing trip (implemented by TripRecorder).</summary>
public interface ITripTracker
{
    ValueTask<Guid?> ProcessAsync(ObdReading reading, CancellationToken ct = default);
}

/// <summary>
/// Polling configuration: fast values every cycle, slow ones only every Nth cycle.
/// ELM327 clones often manage only 5-15 requests/second — hence the priorities.
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
/// Polls live values from the vehicle, reports them to the UI (event), and records
/// EVERY value as an <see cref="ObdSample"/> — core requirement: history must be viewable.
/// UI-free; a Blazor page just subscribes to <see cref="ReadingReceived"/>.
/// </summary>
public sealed class LiveDataService(
    Elm327Client client, IObdSampleStore store, IClock clock, ITripTracker? tripTracker = null)
{
    private long _cycle;
    private Guid _vehicleId;
    private IReadOnlySet<byte>? _supportedPids;

    public PollingProfile Profile { get; set; } = PollingProfile.Default;

    public event Action<ObdReading>? ReadingReceived;

    /// <summary>Number of failed PID queries since Configure (diagnostics).</summary>
    public int FailedReads { get; private set; }

    public void Configure(Guid vehicleId, IReadOnlySet<byte>? supportedPids)
    {
        _vehicleId = vehicleId;
        _supportedPids = supportedPids;
        _cycle = 0;
        FailedReads = 0;
    }

    /// <summary>A single poll cycle — deterministically testable, no delay.</summary>
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

    /// <summary>Continuous polling until cancellation (for real operation).</summary>
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
