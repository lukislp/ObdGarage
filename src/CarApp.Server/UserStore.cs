using System.Security.Cryptography;
using System.Text.Json;

namespace CarApp.Server;

/// <summary>Benutzerkonto — existiert nur serverseitig (die App speichert nur Token + UserId).</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Salt { get; set; }
}

/// <summary>
/// Benutzerkonten in einer JSON-Datei. Passwörter als PBKDF2-Hash
/// (SHA256, 210.000 Iterationen, 16-Byte-Salt) — nie im Klartext.
/// </summary>
public sealed class UserStore(string filePath)
{
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<User>? _cache;

    /// <summary>Legt ein Konto an. Null, wenn die E-Mail bereits registriert ist.</summary>
    public async Task<User?> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var users = await LoadAsync(ct).ConfigureAwait(false);
            if (users.Any(u => u.Email == normalized))
                return null;

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var user = new User
            {
                Email = normalized,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(Hash(password, salt)),
            };
            users.Add(user);
            await SaveAsync(users, ct).ConfigureAwait(false);
            return user;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Prüft E-Mail + Passwort. Null bei unbekanntem Konto oder falschem Passwort.</summary>
    public async Task<User?> VerifyAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var users = await LoadAsync(ct).ConfigureAwait(false);
            var user = users.FirstOrDefault(u => u.Email == normalized);
            if (user is null)
                return null;

            var computed = Hash(password, Convert.FromBase64String(user.Salt));
            return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(user.PasswordHash))
                ? user
                : null;
        }
        finally { _lock.Release(); }
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);

    private async Task<List<User>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;
        if (!File.Exists(filePath))
            return _cache = [];
        await using var stream = File.OpenRead(filePath);
        return _cache = await JsonSerializer.DeserializeAsync<List<User>>(stream, JsonOptions, ct)
            .ConfigureAwait(false) ?? [];
    }

    private async Task SaveAsync(List<User> users, CancellationToken ct)
    {
        var tmp = filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, users, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(tmp, filePath, overwrite: true);
        _cache = users;
    }
}
