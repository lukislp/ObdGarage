using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CarApp.Core;
using CarApp.Data;
using CarApp.Shared;

namespace CarApp.Server;

/// <summary>
/// Builds the complete server app (auth, sync, samples) on top of the JSON persistence
/// from CarApp.Data. Kept as its own method so tests can start the server in-process —
/// Program.cs just calls BuildApp(args).Run().
/// </summary>
public static class ServerApp
{
    public static WebApplication BuildApp(string[] args, string? dataDir = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        dataDir ??= builder.Configuration["DataDir"] ?? "./data";
        Directory.CreateDirectory(dataDir);
        var inviteCode = builder.Configuration["InviteCode"] ?? "CARAPP-2026";

        var users = new UserStore(Path.Combine(dataDir, "users.json"));
        var tokens = new TokenStore(Path.Combine(dataDir, "tokens.json"));
        var clock = new SystemClock();

        var vehicles = new JsonFileRepository<Vehicle>(dataDir);
        var adapterProfiles = new JsonFileRepository<AdapterProfile>(dataDir);
        var odometerReadings = new JsonFileRepository<OdometerReading>(dataDir);
        var trips = new JsonFileRepository<Trip>(dataDir);
        var maintenanceTasks = new JsonFileRepository<MaintenanceTask>(dataDir);
        var fuelEntries = new JsonFileRepository<FuelEntry>(dataDir);
        var expenses = new JsonFileRepository<Expense>(dataDir);
        var samples = new JsonlObdSampleStore(dataDir);

        var app = builder.Build();

        app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }));

        // --- Auth (public) ---------------------------------------------------------

        app.MapPost("/api/v1/auth/register", async (RegisterRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new ErrorResponse("E-Mail und Passwort sind erforderlich."));
            if (!FixedTimeStringEquals(req.InviteCode, inviteCode))
                return Results.Json(new ErrorResponse("Ungültiger Einladungscode."), statusCode: StatusCodes.Status403Forbidden);

            var user = await users.RegisterAsync(req.Email, req.Password, ct);
            return user is null
                ? Results.BadRequest(new ErrorResponse("Diese E-Mail ist bereits registriert."))
                : Results.Ok(new { user.Id });
        });

        app.MapPost("/api/v1/auth/login", async (LoginRequest req, CancellationToken ct) =>
        {
            var user = await users.VerifyAsync(req.Email, req.Password, ct);
            if (user is null)
                return Results.Json(new ErrorResponse("E-Mail oder Passwort ist falsch."), statusCode: StatusCodes.Status401Unauthorized);

            var token = await tokens.IssueAsync(user.Id, clock.UtcNow, ct);
            return Results.Ok(new LoginResponse(token, user.Id));
        });

        // --- Protected area: bearer token check as an endpoint filter --------------

        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            Guid? userId = null;
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                userId = await tokens.ValidateAsync(header["Bearer ".Length..].Trim(), clock.UtcNow, http.RequestAborted);
            if (userId is null)
                return Results.Json(new ErrorResponse("Nicht angemeldet."), statusCode: StatusCodes.Status401Unauthorized);

            http.Items["UserId"] = userId.Value;
            return await next(ctx);
        });

        // --- Sync API: push (LWW) + pull (?since=) per entity, strictly user-scoped ---

        var sync = api.MapGroup("/sync");
        MapVehicleSync(sync, vehicles, clock);
        MapChildSync(sync, "adapterprofiles", adapterProfiles, vehicles, clock, e => e.VehicleId);
        MapChildSync(sync, "odometerreadings", odometerReadings, vehicles, clock, e => e.VehicleId);
        MapChildSync(sync, "trips", trips, vehicles, clock, e => e.VehicleId);
        MapChildSync(sync, "maintenancetasks", maintenanceTasks, vehicles, clock, e => e.VehicleId);
        MapChildSync(sync, "fuelentries", fuelEntries, vehicles, clock, e => e.VehicleId);
        MapChildSync(sync, "expenses", expenses, vehicles, clock, e => e.VehicleId);

        // --- Live value history: batch append + history query --------------------------

        api.MapPost("/samples", async (HttpContext http, List<ObdSample> batch, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var owned = await OwnedVehicleIdsAsync(vehicles, userId, ct);
            var accepted = batch.Where(s => owned.Contains(s.VehicleId)).ToList();
            var rejectedIds = batch.Where(s => !owned.Contains(s.VehicleId)).Select(s => s.Id).ToList();
            foreach (var s in accepted)
                s.SyncState = SyncState.Synced;
            await samples.AppendBatchAsync(accepted, ct);
            return Results.Ok(new SyncPushResponse(accepted.Count, rejectedIds.Count, rejectedIds));
        });

        api.MapGet("/samples", async (HttpContext http, Guid vehicleId, string? pidKey, string? from, string? to, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var owned = await OwnedVehicleIdsAsync(vehicles, userId, ct);
            if (!owned.Contains(vehicleId))
                return Results.Json(new ErrorResponse("Kein Zugriff auf dieses Fahrzeug."), statusCode: StatusCodes.Status403Forbidden);

            var f = ParseTime(from) ?? DateTimeOffset.MinValue;
            var t = ParseTime(to) ?? DateTimeOffset.MaxValue;
            var key = string.IsNullOrEmpty(pidKey) ? null : pidKey;
            return Results.Ok(await samples.QueryAsync(vehicleId, key, f, t, ct));
        });

        return app;
    }

    /// <summary>Vehicles: ownership directly via OwnerUserId from the token.</summary>
    private static void MapVehicleSync(RouteGroupBuilder sync, ISyncRepository<Vehicle> vehicles, IClock clock)
    {
        sync.MapPost("/vehicles", async (HttpContext http, List<Vehicle> batch, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var existingAll = (await vehicles.GetAllIncludingDeletedAsync(ct)).ToDictionary(v => v.Id);
            int accepted = 0;
            var rejectedIds = new List<Guid>();
            foreach (var incoming in batch)
            {
                var existing = existingAll.GetValueOrDefault(incoming.Id);
                // Foreign data is ignored: wrong owner OR attempt to hijack someone else's vehicle.
                if (incoming.OwnerUserId != userId || (existing is not null && existing.OwnerUserId != userId))
                {
                    rejectedIds.Add(incoming.Id);
                    continue;
                }
                accepted++;
                if (existing is not null && existing.ModifiedAt >= incoming.ModifiedAt)
                    continue; // Last-Write-Wins: older version does not overwrite.
                incoming.SyncState = SyncState.Synced;
                await vehicles.UpsertAsync(incoming, ct);
                existingAll[incoming.Id] = incoming;
            }
            return Results.Ok(new SyncPushResponse(accepted, rejectedIds.Count, rejectedIds));
        });

        sync.MapGet("/vehicles", async (HttpContext http, string? since, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var sinceTs = ParseTime(since) ?? DateTimeOffset.MinValue;
            var items = (await vehicles.GetAllIncludingDeletedAsync(ct))
                .Where(v => v.OwnerUserId == userId && v.ModifiedAt > sinceTs)
                .ToList();
            return Results.Ok(new SyncEnvelope<Vehicle> { Items = items, ServerTime = clock.UtcNow });
        });
    }

    /// <summary>Child entities: ownership via VehicleId (vehicle must belong to the token's user).</summary>
    private static void MapChildSync<T>(RouteGroupBuilder sync, string name,
        ISyncRepository<T> repo, ISyncRepository<Vehicle> vehicles, IClock clock,
        Func<T, Guid?> vehicleIdOf) where T : SyncEntity
    {
        sync.MapPost("/" + name, async (HttpContext http, List<T> batch, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var owned = await OwnedVehicleIdsAsync(vehicles, userId, ct);
            var existingAll = (await repo.GetAllIncludingDeletedAsync(ct)).ToDictionary(e => e.Id);
            int accepted = 0;
            var rejectedIds = new List<Guid>();
            foreach (var incoming in batch)
            {
                var existing = existingAll.GetValueOrDefault(incoming.Id);
                var incomingOk = vehicleIdOf(incoming) is Guid v && owned.Contains(v);
                var existingOk = existing is null || (vehicleIdOf(existing) is Guid ev && owned.Contains(ev));
                if (!incomingOk || !existingOk)
                {
                    rejectedIds.Add(incoming.Id);
                    continue;
                }
                accepted++;
                if (existing is not null && existing.ModifiedAt >= incoming.ModifiedAt)
                    continue; // Last-Write-Wins
                incoming.SyncState = SyncState.Synced;
                await repo.UpsertAsync(incoming, ct);
                existingAll[incoming.Id] = incoming;
            }
            return Results.Ok(new SyncPushResponse(accepted, rejectedIds.Count, rejectedIds));
        });

        sync.MapGet("/" + name, async (HttpContext http, string? since, CancellationToken ct) =>
        {
            var userId = UserIdOf(http);
            var owned = await OwnedVehicleIdsAsync(vehicles, userId, ct);
            var sinceTs = ParseTime(since) ?? DateTimeOffset.MinValue;
            var items = (await repo.GetAllIncludingDeletedAsync(ct))
                .Where(e => vehicleIdOf(e) is Guid v && owned.Contains(v) && e.ModifiedAt > sinceTs)
                .ToList();
            return Results.Ok(new SyncEnvelope<T> { Items = items, ServerTime = clock.UtcNow });
        });
    }

    private static Guid UserIdOf(HttpContext http) => (Guid)http.Items["UserId"]!;

    /// <summary>All vehicle IDs of the user — including deleted ones, so child tombstones remain syncable.</summary>
    private static async Task<HashSet<Guid>> OwnedVehicleIdsAsync(
        ISyncRepository<Vehicle> vehicles, Guid userId, CancellationToken ct) =>
        (await vehicles.GetAllIncludingDeletedAsync(ct))
            .Where(v => v.OwnerUserId == userId)
            .Select(v => v.Id)
            .ToHashSet();

    private static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t
            : null;

    /// <summary>Constant-time string comparison for secrets (invite code) - a plain
    /// StringComparison.Ordinal comparison short-circuits on the first mismatched
    /// character, leaking timing information about how much of the guess was correct.</summary>
    private static bool FixedTimeStringEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
