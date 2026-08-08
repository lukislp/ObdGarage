using System.Globalization;
using ObdGarage.Application;
using ObdGarage.Core;
using ObdGarage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;

namespace ObdGarage.App;

public static class MauiProgram
{
    /// <summary>
    /// Base URL of the home backend (ObdGarage.Server) for sync. On the home network, enter the
    /// server's IP/hostname — default port per
    /// src/ObdGarage.Server/Properties/launchSettings.json: 5235 (http profile).
    /// Note: localhost does not work from a phone; the server must listen on
    /// 0.0.0.0 (docs/MAUI-SETUP.md, section "Backend on the home network").
    /// This will later move into a settings page (Preferences/SecureStorage).
    /// </summary>
    private const string DefaultSyncBaseUrl = "http://192.168.0.100:5235/";

    public static MauiApp CreateMauiApp()
    {
        // German display (numbers/dates) — identical to the web app (ObdGarage.Web/Program.cs).
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
        // DI wiring analogous to ObdGarage.Web/Program.cs — only the data
        // directory here is the platform's app sandbox directory.
        // ------------------------------------------------------------------
        var dataDir = FileSystem.AppDataDirectory;
        Directory.CreateDirectory(Path.Combine(dataDir, "photos"));

        var dbPath = Path.Combine(dataDir, "obdgarage.db");
        builder.Services.AddSingleton<IDbContextFactory<ObdGarageDbContext>>(new SqliteDbContextFactory(dbPath));

        builder.Services.AddSingleton<IClock, SystemClock>();
        RegisterRepository<Vehicle>(builder.Services);
        RegisterRepository<AdapterProfile>(builder.Services);
        RegisterRepository<OdometerReading>(builder.Services);
        RegisterRepository<Trip>(builder.Services);
        RegisterRepository<MaintenanceTask>(builder.Services);
        RegisterRepository<FuelEntry>(builder.Services);
        RegisterRepository<Expense>(builder.Services);
        builder.Services.AddSingleton<IObdSampleStore, EfObdSampleStore>();

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

        // NOTE ON UI: The web app's Razor components (ObdGarage.Web/Components)
        // will move in a later step into a shared Razor Class Library
        // (ObdGarage.UI) that is then referenced by both Web AND MAUI — concrete
        // instructions in docs/MAUI-SETUP.md, section "Shared UI (ObdGarage.UI)".
        // Until then, Components/Main.razor serves as a placeholder start page with
        // a connection test (simulator + WiFi adapter).

        var app = builder.Build();

        // Apply pending migrations (creates the SQLite file + schema on the very first run),
        // then import any pre-existing per-entity JSON data from before the EF Core/SQLite
        // switch (see JsonToSqliteImporter) - a no-op on every run after the first. Blocks
        // briefly at startup - CreateMauiApp itself isn't async, and nothing needs the data
        // layer before this returns anyway.
        var dbWasCreatedFresh = !File.Exists(dbPath);
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<ObdGarageDbContext>>();
        using (var migrationDb = dbFactory.CreateDbContext())
            migrationDb.Database.Migrate();
        JsonToSqliteImporter.ImportIfNeededAsync(dataDir, dbWasCreatedFresh, dbFactory)
            .GetAwaiter().GetResult();

        return app;
    }

    /// <summary>Registers the same EF Core/SQLite-backed repository as both IRepository AND ISyncRepository.</summary>
    private static void RegisterRepository<T>(IServiceCollection services) where T : SyncEntity
    {
        services.AddSingleton<IRepository<T>, EfSyncRepository<T>>();
        services.AddSingleton<ISyncRepository<T>, EfSyncRepository<T>>();
    }
}
