# CarApp — OBD2-Fahrzeug-App (Blazor / .NET MAUI)

Vollständiger Plan: PROJEKTPLAN.md · MAUI-Anleitung: docs/MAUI-SETUP.md

## Schnellstart (auf deinem Rechner, .NET 10 SDK)

    # 1. Alle Tests (100 Stück, inkl. End-to-End mit Fahrzeug-Simulator + Sync-Roundtrip)
    dotnet run --project tools/CarApp.TestRunner

    # 2. Backend starten (Heimserver; Einladungscode Standard: CARAPP-2026)
    ASPNETCORE_URLS=http://0.0.0.0:5299 dotnet run --project src/CarApp.Server --no-launch-profile

    # 3. Web-App starten und im Browser öffnen
    ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project src/CarApp.Web --no-launch-profile
    # → http://127.0.0.1:5199  (Garage → Fahrzeug hinzufügen → Dashboard → "Simulator verbinden")

Die Web-App ist der voll funktionsfähige Testträger der App-UI: Garage-Karten (Foto, km-Stand
mit Quelle, TÜV-/Service-Ampel), Live-Dashboard (Simulator oder WLAN-Adapter), Werte-Verlauf
als Chart, automatisches Fahrtenbuch mit CSV-Export, Wartungsplaner, Tankbuch/Kosten und
Sync mit Login gegen das Backend (Registrierung per Einladungscode, Offline-first, Multi-User
mit striktem Besitzer-Scoping).

## Projekte

- `src/CarApp.Core` — Domänenmodelle + Interfaces (abhängigkeitsfrei)
- `src/CarApp.Obd` — ELM327-Client (Nur-Lese-Whitelist!), PID-Registry, Transporte
  (WLAN fertig, Android-BT in CarApp.App), Fahrzeug-Simulator
- `src/CarApp.Application` — LiveDataService (Polling + lückenlose Werte-Historie),
  TripRecorder, OdometerTracker, MaintenanceCalculator, FuelStatistics, SyncService
- `src/CarApp.Data` — dependency-freie JSON/JSONL-Persistenz hinter Core-Interfaces
  (EF Core/SQLite später 1:1 austauschbar)
- `src/CarApp.Server` — Backend: Konten (PBKDF2), Bearer-Tokens (gehasht), Sync-API (LWW),
  Samples-API; Docker-tauglich, Daten via Config `DataDir`
- `src/CarApp.Shared` — DTOs App ↔ Backend
- `src/CarApp.Web` — komplette UI (Blazor Interactive Server, keine externen Abhängigkeiten)
- `src/CarApp.App` — .NET-MAUI-Hülle (Android/iOS) inkl. Android-Bluetooth-Classic-Transport;
  Build braucht MAUI-Workload → docs/MAUI-SETUP.md (nicht Teil der Solution-Datei)
- `tests/CarApp.Tests` — xunit (OBD-Kern) · `tools/CarApp.TestRunner` — komplette Suite ohne NuGet

## Wichtige Hinweise

- Livewerte werden IMMER historisiert (ObdSample, auch außerhalb von Fahrten);
  `CompactAsync` verdichtet alte Rohwerte zu Minuten-Aggregaten (Min/Avg/Max).
- Sicherheit am Fahrzeug: `Elm327Client` sendet ausschließlich Lese-Befehle
  (kein Mode 04/Fehlercode-Löschen, keine UDS-Writes, kein ATSH) — per Whitelist erzwungen und getestet.
- Web-UI läuft als Blazor Interactive Server (Formulare = direkte C#-Methodenaufrufe,
  Live-Dashboard über `PeriodicTimer`); in der MAUI-App sollen dieselben Komponenten künftig
  über die BlazorWebView interaktiv laufen. RCL-Extraktion: docs/MAUI-SETUP.md.
- Backend global erreichbar machen: Reverse Proxy (HTTPS!) oder WireGuard/Tailscale, siehe PROJEKTPLAN.md 2.2a.
