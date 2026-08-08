using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObdGarage.Server;

/// <summary>Server-side token entry — only the SHA256 hash is stored.</summary>
public sealed class TokenRecord
{
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Bearer tokens in tokens.json: 32 random bytes as Base64Url sent to the client,
/// only the SHA256 hash stored server-side (a file leak reveals no valid tokens).
/// Expires after 90 days; expired entries are removed on access.
/// </summary>
public sealed class TokenStore(string filePath)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<TokenRecord>? _cache;

    /// <summary>Generates a new token for the user and returns it in plain text.</summary>
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

    /// <summary>Returns the UserId for the token — or null if unknown/expired.</summary>
    public async Task<Guid?> ValidateAsync(string token, DateTimeOffset now, CancellationToken ct = default)
    {
        var hash = HashOf(token);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadAsync(ct).ConfigureAwait(false);
            var hashBytes = Convert.FromBase64String(hash);
            var record = records.FirstOrDefault(r =>
                CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(r.TokenHash), hashBytes));
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
