using System.Globalization;
using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using CarApp.Web;
using CarApp.Web.Components;
using CarApp.Web.Services;

// Deutsche Anzeige (Zahlen/Daten); Formulareingaben werden bewusst kulturunabhängig geparst (Fmt).
var culture = new CultureInfo("de-DE");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// Nötig, damit Framework-Assets (_framework/blazor.web.js) auch ohne "dotnet publish"
// und außerhalb von ASPNETCORE_ENVIRONMENT=Development ausgeliefert werden (Default-Start
// per CLAUDE.md läuft ohne Environment-Variable, also im Production-Modus).
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

// SyncManager früh instanziieren, damit ein gespeichertes Token (data/sync-auth.json)
// sofort geladen wird und AppState.CurrentUserId schon beim ersten Request stimmt.
_ = app.Services.GetRequiredService<SyncManager>();

app.Run();

/// <summary>Eine JSON-Datei pro Entität — als IRepository UND ISyncRepository registriert.</summary>
static void RegisterRepository<T>(IServiceCollection services, string dataDir) where T : SyncEntity
{
    var repo = new JsonFileRepository<T>(dataDir);
    services.AddSingleton<IRepository<T>>(repo);
    services.AddSingleton<ISyncRepository<T>>(repo);
}
