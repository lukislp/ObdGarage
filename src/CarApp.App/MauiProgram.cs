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
    /// Basis-URL des Heim-Backends (CarApp.Server) für den Sync. Im Heimnetz die
    /// IP/den Hostnamen des Servers eintragen — Standardport laut
    /// src/CarApp.Server/Properties/launchSettings.json: 5235 (http-Profil).
    /// Achtung: localhost funktioniert vom Handy aus nicht; der Server muss auf
    /// 0.0.0.0 lauschen (docs/MAUI-SETUP.md, Abschnitt "Backend im Heimnetz").
    /// Später wandert das in eine Einstellungsseite (Preferences/SecureStorage).
    /// </summary>
    private const string DefaultSyncBaseUrl = "http://192.168.0.100:5235/";

    public static MauiApp CreateMauiApp()
    {
        // Deutsche Anzeige (Zahlen/Daten) — identisch zur Web-App (CarApp.Web/Program.cs).
        var culture = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { /* Standard-Systemfonts genügen fürs Erste */ });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // ------------------------------------------------------------------
        // DI-Verdrahtung analog zu CarApp.Web/Program.cs — nur das Daten-
        // verzeichnis ist hier das App-Sandbox-Verzeichnis der Plattform.
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

        // Sync gegen das Heim-Backend (Phase 6): HttpClient + SyncService.
        // Offline-first — ist der Server nicht erreichbar, liefert SyncService
        // saubere Ergebnisobjekte statt Exceptions, die App arbeitet lokal weiter.
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

        // HINWEIS ZUR UI: Die Razor-Komponenten der Web-App (CarApp.Web/Components)
        // wandern in einem späteren Schritt in eine gemeinsame Razor Class Library
        // (CarApp.UI), die dann von Web UND MAUI referenziert wird — konkrete
        // Anleitung in docs/MAUI-SETUP.md, Abschnitt "Gemeinsame UI (CarApp.UI)".
        // Bis dahin dient Components/Main.razor als Platzhalter-Startseite mit
        // Verbindungstest (Simulator + WLAN-Adapter).

        return builder.Build();
    }

    /// <summary>Eine JSON-Datei pro Entität — als IRepository UND ISyncRepository registriert (wie in der Web-App).</summary>
    private static void RegisterRepository<T>(IServiceCollection services, string dataDir) where T : SyncEntity
    {
        var repo = new JsonFileRepository<T>(dataDir);
        services.AddSingleton<IRepository<T>>(repo);
        services.AddSingleton<ISyncRepository<T>>(repo);
    }
}
