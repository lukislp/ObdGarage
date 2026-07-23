using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CarApp.Server;

/// <summary>Serverseitiger Token-Eintrag — es wird nur der SHA256-Hash gespeichert.</summary>
public sealed class TokenRecord
{
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Bearer-Tokens in tokens.json: 32 Zufallsbytes als Base64Url an den Client,
/// serverseitig nur der SHA256-Hash (ein Datei-Leck verrät keine gültigen Tokens).
/// Ablauf nach 90 Tagen; abgelaufene Einträge werden beim Zugriff entfernt.
/// </summary>
public sealed class TokenStore(string filePath)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<TokenRecord>? _cache;

    /// <summary>Erzeugt ein neues Token für den Nutzer und liefert es im Klartext zurück.</summary>
    public async Task<string> IssueAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadAsync(ct).ConfigureAwait(false);
            records.RemoveAll(r => r.ExpiresAt <= now);
            records.Add(new TokenRecord
            {
                TokenHash = HashOf(token),
                UserId = userId,
                ExpiresAt = now + Lifetime,
            });
            await SaveAsync(records, ct).ConfigureAwait(false);
            return token;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Liefert die UserId zum Token — oder null bei unbekannt/abgelaufen.</summary>
    public async Task<Guid?> ValidateAsync(string token, DateTimeOffset now, CancellationToken ct = default)
    {
        var hash = HashOf(token);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadAsync(ct).ConfigureAwait(false);
            var record = records.FirstOrDefault(r => r.TokenHash == hash);
            if (record is null)
                return null;
            if (record.ExpiresAt <= now)
            {
                records.Remove(record);
                await SaveAsync(records, ct).ConfigureAwait(false);
                return null;
            }
            return record.UserId;
        }
        finally { _lock.Release(); }
    }

    private static string HashOf(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<List<TokenRecord>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;
        if (!File.Exists(filePath))
            return _cache = [];
        await using var stream = File.OpenRead(filePath);
        return _cache = await JsonSerializer.DeserializeAsync<List<TokenRecord>>(stream, JsonOptions, ct)
            .ConfigureAwait(false) ?? [];
    }

    private async Task SaveAsync(List<TokenRecord> records, CancellationToken ct)
    {
        var tmp = filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(tmp, filePath, overwrite: true);
        _cache = records;
    }
}
