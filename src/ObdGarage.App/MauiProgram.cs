using System.Globalization;
using ObdGarage.App.Services;
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
        var photosDir = Path.Combine(dataDir, "photos");
        Directory.CreateDirectory(photosDir);

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

        builder.Services.AddSingleton(new PhotoStorage(photosDir));
        builder.Services.AddSingleton<OdometerTracker>();
        builder.Services.AddSingleton<ConnectionManager>();

        // AppState is a singleton here (unlike Web's per-circuit Scoped) - MAUI has a single
        // long-lived window/session, not multiple browser tabs that could stomp on each other's
        // login state (see the Web host's Program.cs for the multi-tab reasoning this doesn't
        // apply to).
        builder.Services.AddSingleton<AppState>();

        // ISyncManager: SecureStorage-backed on this host (see SecureStorageSyncManager) instead
        // of Web's sync-auth.json file - same shared UI (Settings.razor, ObdGarage.UI) drives
        // both without knowing which. Server URL is entered once on the Settings page and
        // persisted from then on - no more hardcoded LAN IP baked into the app.
        builder.Services.AddSingleton<ISyncManager>(sp => new SecureStorageSyncManager(
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
            Path.Combine(dataDir, "sync-state.json")));

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
