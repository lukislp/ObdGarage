# MAUI-Setup — CarApp.App auf Android/iOS bauen

`src/CarApp.App` ist die .NET-MAUI-Blazor-Hybrid-Hülle (Plan 2.1). Sie ist
**bewusst nicht in `CarApp.slnx` eingetragen**, damit `dotnet build`/`dotnet test`
der Solution auch auf Maschinen ohne MAUI-Workload (CI, Sandbox) funktionieren.

Voraussetzungen: Windows oder macOS mit .NET 10 SDK. Für iOS zwingend ein Mac
(bzw. Windows + Mac im Netz für "Pair to Mac") mit aktuellem Xcode.

## 1. MAUI-Workload installieren

```bash
dotnet workload install maui
# Prüfen:
dotnet workload list
```

Das Workload liefert auch `$(MauiVersion)` — die beiden PackageReferences in
`CarApp.App.csproj` (`Microsoft.Maui.Controls`, `Microsoft.AspNetCore.Components.WebView.Maui`)
lösen sich damit ohne manuelle Versionsangabe auf.

## 2. Projekt zur Solution hinzufügen

```bash
cd CarApp
dotnet sln CarApp.slnx add src/CarApp.App/CarApp.App.csproj
```

(Wieder entfernen: `dotnet sln CarApp.slnx remove src/CarApp.App/CarApp.App.csproj` —
z.B. bevor auf einer Maschine ohne Workload gebaut wird.)

## 3. Android bauen und deployen

```bash
# Nur bauen:
dotnet build src/CarApp.App -f net10.0-android

# APK aufs per USB verbundene Gerät (USB-Debugging aktivieren!):
dotnet build src/CarApp.App -f net10.0-android -t:Run

# Alternativ manuell per adb:
dotnet publish src/CarApp.App -f net10.0-android -c Release
adb install src/CarApp.App/bin/Release/net10.0-android/publish/com.carapp.mobile-Signed.apk
adb devices          # Gerät sichtbar?
adb logcat -s DOTNET # Logs der App
```

Hinweise Android:
- Emulator reicht für UI-Arbeit; für Bluetooth Classic braucht es ein echtes Gerät.
- Ab Android 12 sind `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` **Laufzeit**-Berechtigungen:
  vor dem ersten Verbinden `Permissions.RequestAsync<Permissions.Bluetooth>()` aufrufen
  (das Manifest ist schon vorbereitet, inkl. `neverForLocation`).
- Der ELM327-Adapter muss vorher in den Android-Einstellungen gekoppelt sein
  (PIN meist `1234` oder `0000`); die App verbindet dann per MAC-Adresse über
  `BluetoothClassicTransport` (SPP-UUID 00001101-0000-1000-8000-00805F9B34FB).

## 4. iOS bauen

```bash
# Auf dem Mac (Gerät per Kabel, in Xcode einmal als vertrauenswürdig einrichten):
dotnet build src/CarApp.App -f net10.0-ios -t:Run

# Von Windows aus: "Pair to Mac" in Visual Studio, oder direkt auf dem Mac bauen.
```

Wichtige iOS-Einschränkungen (Plan 2.1/8):
- **Bluetooth Classic (SPP) geht auf iOS NICHT** — die billigen ELM327-BT-Adapter
  funktionieren dort nie. Nutzbar sind nur **BLE**-Adapter (z.B. vLinker MC+,
  OBDLink CX — Umsetzung: `Services/BleTransport.cs`, Phase 7) und **WLAN**-Adapter
  (`WifiTcpTransport`, funktioniert heute schon).
- Beim ersten TCP-Zugriff auf den WLAN-Adapter/Heimserver zeigt iOS den
  Local-Network-Dialog (`NSLocalNetworkUsageDescription` ist in der Info.plist gesetzt).
- Für Geräte-Builds sind Apple-Entwicklerkonto + Provisioning nötig; Verteilung
  an Tester über TestFlight (Phase 7).

## 5. Web-UI in eine gemeinsame Razor Class Library (CarApp.UI) extrahieren

Die Seiten in `src/CarApp.Web/Components` sind bereits UI-dünn (Services statt
Logik in den Komponenten) — sie lassen sich darum schrittweise in eine RCL
verschieben, die Web **und** MAUI teilen:

1. RCL anlegen und referenzieren:
   ```bash
   dotnet new razorclasslib -n CarApp.UI -o src/CarApp.UI
   dotnet sln CarApp.slnx add src/CarApp.UI/CarApp.UI.csproj
   dotnet add src/CarApp.UI reference src/CarApp.Core src/CarApp.Application src/CarApp.Obd src/CarApp.Shared
   dotnet add src/CarApp.Web reference src/CarApp.UI
   dotnet add src/CarApp.App reference src/CarApp.UI
   ```
2. Komponenten umziehen: Seiten/Teile aus `CarApp.Web/Components` nach
   `src/CarApp.UI/` verschieben, Namespaces auf `CarApp.UI.…` anpassen und in
   beiden Hosts per `@using CarApp.UI` einbinden. **Nicht** mitnehmen:
   Web-spezifisches wie `App.razor`/`Routes.razor` (bleiben im Web) — die MAUI-App
   bekommt eine eigene `Routes.razor` mit `<Router AppAssembly="typeof(CarApp.UI.…).Assembly">`.
3. Dienste-Verdrahtung bleibt pro Host: Beide registrieren dieselben Services
   (siehe `CarApp.Web/Program.cs` vs. `CarApp.App/MauiProgram.cs`) — die
   Komponenten injizieren nur Interfaces/Services und merken nicht, wo sie laufen.
4. Statische Assets der RCL landen unter `_content/CarApp.UI/…` — Pfade in
   `index.html` (MAUI) bzw. `App.razor` (Web) entsprechend ergänzen.
5. Unterschiedliches Verhalten (z.B. ConnectionManager mit Bluetooth nur in der
   App) über je Host registrierte Zusatzservices lösen, nicht über `#if` in der UI.
6. Danach `Components/Main.razor` in CarApp.App durch die echte Startseite aus
   der RCL ersetzen (RootComponent in `MainPage.xaml` umstellen).

## 6. Backend im Heimnetz (Sync)

- Standardport des Servers laut `src/CarApp.Server/Properties/launchSettings.json`:
  **http://localhost:5235**. Vom Handy aus ist `localhost` der falsche Host —
  Server auf allen Interfaces lauschen lassen:
  ```bash
  dotnet run --project src/CarApp.Server --urls http://0.0.0.0:5235
  ```
  (oder `applicationUrl` in den launchSettings auf `http://0.0.0.0:5235` ändern
  und die Firewall des Rechners für den Port freigeben).
- In `src/CarApp.App/MauiProgram.cs` die Konstante `DefaultSyncBaseUrl` auf die
  Heimnetz-IP des Servers setzen (z.B. `http://192.168.0.100:5235/`) — bis eine
  Einstellungsseite das übernimmt.
- Android-Emulator: der Host-Rechner ist dort `10.0.2.2`, nicht `192.168.x.x`.
- Unterwegs (Plan 8): Tailscale/WireGuard statt offenem Port; sobald der Server
  das Heimnetz verlässt, nur noch HTTPS und Token in `SecureStorage`.

## 7. Bekannte Stolpersteine

- `XA5300 / Android SDK nicht gefunden`: einmal `dotnet build -t:InstallAndroidDependencies -f net10.0-android` ausführen oder SDK-Pfad via `AndroidSdkDirectory` setzen.
- Erster Android-Build lädt viel nach (AOT-Profile, SDK-Teile) — dauert.
- `Simulator testen` in der App funktioniert komplett offline — idealer erster
  Smoke-Test auf jedem Gerät, bevor echte Adapter ins Spiel kommen.
