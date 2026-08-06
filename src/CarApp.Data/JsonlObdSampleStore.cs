using System.Text.Json;
using CarApp.Core;

namespace CarApp.Data;

/// <summary>
/// Live-value history as a JSON-Lines file per vehicle: appends are cheap
/// (high write volume during polling), compaction rewrites the file atomically.
/// </summary>
public sealed class JsonlObdSampleStore(string directory) : IObdSampleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string FileFor(Guid vehicleId) =>
        Path.Combine(directory, $"samples-{vehicleId:N}.jsonl");

    public async Task AppendBatchAsync(IReadOnlyList<ObdSample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0)
            return;
        Directory.CreateDirectory(directory);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var group in samples.GroupBy(s => s.VehicleId))
            {
                var lines = group.Select(s => JsonSerializer.Serialize(s, JsonOptions));
                await File.AppendAllLinesAsync(FileFor(group.Key), lines, ct).ConfigureAwait(false);
            }
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<ObdSample>> QueryAsync(
        Guid vehicleId, string? pidKey, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAllAsync(vehicleId, ct).ConfigureAwait(false))
                .Where(s => s.Timestamp >= from && s.Timestamp <= to)
                .Where(s => pidKey is null || s.PidKey.Equals(pidKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Timestamp)
                .ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> CompactAsync(
        Guid vehicleId, DateTimeOffset olderThan, TimeSpan bucket, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(vehicleId, ct).ConfigureAwait(false);
            var toCompact = all.Where(s => !s.IsAggregated && s.Timestamp < olderThan).ToList();
            if (toCompact.Count == 0)
                return 0;

            var keep = all.Except(toCompact).ToList();
            var aggregates = toCompact
                .GroupBy(s => (s.PidKey, Bucket: FloorTo(s.Timestamp, bucket)))
                .Select(g => new ObdSample
                {
                    VehicleId = vehicleId,
                    PidKey = g.Key.PidKey,
                    Timestamp = g.Key.Bucket,
                    Value = g.Average(s => s.Value),
                    MinValue = g.Min(s => s.Value),
                    MaxValue = g.Max(s => s.Value),
                    IsAggregated = true,
                });

            var result = keep.Concat(aggregates).OrderBy(s => s.Timestamp).ToList();
            var tmp = FileFor(vehicleId) + ".tmp";
            await File.WriteAllLinesAsync(tmp,
                result.Select(s => JsonSerializer.Serialize(s, JsonOptions)), ct).ConfigureAwait(false);
            File.Move(tmp, FileFor(vehicleId), overwrite: true);
            return toCompact.Count;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<ObdSample>> ReadAllAsync(Guid vehicleId, CancellationToken ct)
    {
        var file = FileFor(vehicleId);
        if (!File.Exists(file))
            return [];

        var samples = new List<ObdSample>();
        foreach (var line in await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var s = JsonSerializer.Deserialize<ObdSample>(line, JsonOptions);
            if (s is not null)
                samples.Add(s);
        }
        return samples;
    }

    private static DateTimeOffset FloorTo(DateTimeOffset t, TimeSpan bucket) =>
        new(t.UtcTicks - t.UtcTicks % bucket.Ticks, TimeSpan.Zero);
}
