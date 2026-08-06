using System.Globalization;
using CarApp.Application;
using CarApp.Core;
using CarApp.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;

namespace CarApp.App;

public static class MauiProgram
{
    /// <summary>
    /// Base URL of the home backend (CarApp.Server) for sync. On the home network, enter the
    /// server's IP/hostname — default port per
    /// src/CarApp.Server/Properties/launchSettings.json: 5235 (http profile).
    /// Note: localhost does not work from a phone; the server must listen on
    /// 0.0.0.0 (docs/MAUI-SETUP.md, section "Backend on the home network").
    /// This will later move into a settings page (Preferences/SecureStorage).
    /// </summary>
    private const string DefaultSyncBaseUrl = "http://192.168.0.100:5235/";

    public static MauiApp CreateMauiApp()
    {
        // German display (numbers/dates) — identical to the web app (CarApp.Web/Program.cs).
        var culture = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { /* default system fonts are sufficient for now */ });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // ------------------------------------------------------------------
        // DI wiring analogous to CarApp.Web/Program.cs — only the data
        // directory here is the platform's app sandbox directory.
        // ------------------------------------------------------------------
        var dataDir = FileSystem.AppDataDirectory;
        Directory.CreateDirectory(Path.Combine(dataDir, "photos"));

        builder.Services.AddSingleton<IClock, SystemClock>();
        RegisterRepository<Vehicle>(builder.Services, dataDir);
        RegisterRepository<AdapterProfile>(builder.Services, dataDir);
        RegisterRepository<OdometerReading>(builder.Services, dataDir);
        RegisterRepository<Trip>(builder.Services, dataDir);
        RegisterRepository<MaintenanceTask>(builder.Services, dataDir);
        RegisterRepository<FuelEntry>(builder.Services, dataDir);
        RegisterRepository<Expense>(builder.Services, dataDir);
        builder.Services.AddSingleton<IObdSampleStore>(new JsonlObdSampleStore(dataDir));

        builder.Services.AddSingleton<OdometerTracker>();

        // Sync against the home backend (Phase 6): HttpClient + SyncService.
        // Offline-first — if the server is unreachable, SyncService returns
        // clean result objects instead of exceptions; the app keeps working locally.
        builder.Services.AddSingleton(sp =>
        {
            var http = new HttpClient { BaseAddress = new Uri(DefaultSyncBaseUrl) };
            return new SyncService(
                http,
                sp.GetRequiredService<ISyncRepository<Vehicle>>(),
                sp.GetRequiredService<ISyncRepository<AdapterProfile>>(),
                sp.GetRequiredService<ISyncRepository<OdometerReading>>(),
                sp.GetRequiredService<ISyncRepository<Trip>>(),
                sp.GetRequiredService<ISyncRepository<MaintenanceTask>>(),
                sp.GetRequiredService<ISyncRepository<FuelEntry>>(),
                sp.GetRequiredService<ISyncRepository<Expense>>(),
                sp.GetRequiredService<IObdSampleStore>(),
                sp.GetRequiredService<IClock>(),
                Path.Combine(dataDir, "sync-state.json"));
        });

        // NOTE ON UI: The web app's Razor components (CarApp.Web/Components)
        // will move in a later step into a shared Razor Class Library
        // (CarApp.UI) that is then referenced by both Web AND MAUI — concrete
        // instructions in docs/MAUI-SETUP.md, section "Shared UI (CarApp.UI)".
        // Until then, Components/Main.razor serves as a placeholder start page with
        // a connection test (simulator + WiFi adapter).

        return builder.Build();
    }

    /// <summary>One JSON file per entity — registered as both IRepository AND ISyncRepository (as in the web app).</summary>
    private static void RegisterRepository<T>(IServiceCollection services, string dataDir) where T : SyncEntity
    {
        var repo = new JsonFileRepository<T>(dataDir);
        services.AddSingleton<IRepository<T>>(repo);
        services.AddSingleton<ISyncRepository<T>>(repo);
    }
}
