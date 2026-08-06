using System.Globalization;
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
// and outside of ASPNETCORE_ENVIRONMENT=Development (the default start
// per CLAUDE.md runs without an environment variable, i.e. in Production mode).
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var photosDir = Path.Combine(dataDir, "photos");
Directory.CreateDirectory(photosDir);

builder.Services.AddSingleton(new PhotoStorage(photosDir));
builder.Services.AddSingleton<IClock, SystemClock>();
RegisterRepository<Vehicle>(builder.Services, dataDir);
RegisterRepository<AdapterProfile>(builder.Services, dataDir);
RegisterRepository<OdometerReading>(builder.Services, dataDir);
RegisterRepository<Trip>(builder.Services, dataDir);
RegisterRepository<MaintenanceTask>(builder.Services, dataDir);
RegisterRepository<FuelEntry>(builder.Services, dataDir);
RegisterRepository<Expense>(builder.Services, dataDir);
builder.Services.AddSingleton<IObdSampleStore>(new JsonlObdSampleStore(dataDir));

builder.Services.AddSingleton<AppState>();
builder.Services.AddSingleton<OdometerTracker>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton(sp => new SyncManager(
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapCarAppEndpoints(photosDir);

// Instantiate SyncManager early so a saved token (data/sync-auth.json)
// is loaded immediately and AppState.CurrentUserId is already correct on the first request.
_ = app.Services.GetRequiredService<SyncManager>();

app.Run();

/// <summary>One JSON file per entity — registered as both IRepository AND ISyncRepository.</summary>
static void RegisterRepository<T>(IServiceCollection services, string dataDir) where T : SyncEntity
{
    var repo = new JsonFileRepository<T>(dataDir);
    services.AddSingleton<IRepository<T>>(repo);
    services.AddSingleton<ISyncRepository<T>>(repo);
}
