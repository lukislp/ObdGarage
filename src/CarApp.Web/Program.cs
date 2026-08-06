using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using CarApp.Web;
using CarApp.Web.Components;
using CarApp.Web.Services;

// German display (numbers/dates); form inputs are deliberately parsed culture-independently (Fmt).
var culture = new CultureInfo("de-DE");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// Needed so framework assets (_framework/blazor.web.js) are served even without "dotnet publish"
// and outside of ASPNETCORE_ENVIRONMENT=Development (running via `dotnet run` without setting
// that variable defaults to Production mode).
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var photosDir = Path.Combine(dataDir, "photos");
Directory.CreateDirectory(photosDir);

// Without this, ASP.NET Core falls back to its default per-machine key location
// (e.g. /home/<user>/.aspnet/DataProtection-Keys in a container), which isn't part of
// the volume mounted at dataDir - every container restart would then generate a fresh
// key, silently invalidating every existing antiforgery token and interactive circuit.
// Persisting into dataDir keeps keys stable across restarts as long as the volume is.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dataprotection-keys")));

var dbPath = Path.Combine(dataDir, "carapp.db");
builder.Services.AddSingleton<IDbContextFactory<CarAppDbContext>>(new SqliteDbContextFactory(dbPath));

builder.Services.AddSingleton(new PhotoStorage(photosDir));
builder.Services.AddSingleton<IClock, SystemClock>();
RegisterRepository<Vehicle>(builder.Services);
RegisterRepository<AdapterProfile>(builder.Services);
RegisterRepository<OdometerReading>(builder.Services);
RegisterRepository<Trip>(builder.Services);
RegisterRepository<MaintenanceTask>(builder.Services);
RegisterRepository<FuelEntry>(builder.Services);
RegisterRepository<Expense>(builder.Services);
builder.Services.AddSingleton<IObdSampleStore, EfObdSampleStore>();

// AppState/SyncManager hold which sync account is "logged in" and gate which vehicles a
// browser sees (Home.razor filters by State.CurrentUserId). This app has no other notion
// of browser-session identity (no cookies/auth middleware) - as singletons, one person
// logging in on one tab would silently switch every OTHER already-open tab's identity too,
// including which account newly-created vehicles get attributed to. Scoped (per Blazor
// Server circuit) isolates each browser tab's login state from every other one; a fresh
// circuit still resumes whichever account was last saved to sync-auth.json (see MainLayout's
// SyncManager pre-warm below), matching the previous single-user convenience.
builder.Services.AddScoped<AppState>();
builder.Services.AddSingleton<OdometerTracker>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddScoped(sp => new SyncManager(
    sp.GetRequiredService<ISyncRepository<Vehicle>>(),
    sp.GetRequiredService<ISyncRepository<AdapterProfile>>(),
    sp.GetRequiredService<ISyncRepository<OdometerReading>>(),
    sp.GetRequiredService<ISyncRepository<Trip>>(),
    sp.GetRequiredService<ISyncRepository<MaintenanceTask>>(),
    sp.GetRequiredService<ISyncRepository<FuelEntry>>(),
    sp.GetRequiredService<ISyncRepository<Expense>>(),
    sp.GetRequiredService<IObdSampleStore>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<AppState>(),
    dataDir));

var app = builder.Build();

// Apply pending migrations (creates the SQLite file + schema on the very first run), then
// import any pre-existing per-entity JSON data from before the EF Core/SQLite switch (see
// JsonToSqliteImporter) - a no-op on every run after the first for a given dataDir.
var dbWasCreatedFresh = !File.Exists(dbPath);
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<CarAppDbContext>>();
await using (var db = await dbFactory.CreateDbContextAsync())
    await db.Database.MigrateAsync();
await JsonToSqliteImporter.ImportIfNeededAsync(dataDir, dbWasCreatedFresh, dbFactory);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapCarAppEndpoints(photosDir);

app.Run();

/// <summary>Registers the same EF Core/SQLite-backed repository as both IRepository AND ISyncRepository.</summary>
static void RegisterRepository<T>(IServiceCollection services) where T : SyncEntity
{
    services.AddSingleton<IRepository<T>, EfSyncRepository<T>>();
    services.AddSingleton<ISyncRepository<T>, EfSyncRepository<T>>();
}
