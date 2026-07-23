# Projektplan: OBD2-Fahrzeug-App mit Blazor / .NET MAUI

**Stand:** Juli 2026
**Zielplattformen:** Android + iOS
**Adapter:** Bluetooth (Classic + BLE) und WLAN (ELM327-kompatibel)
**MVP-Umfang:** Fahrzeug-Karten-Übersicht, Live-Dashboard, Kilometerstand + Wartung, Fahrtenbuch + Kosten
**Nutzer:** Mehrbenutzer mit Login (E-Mail + Passwort); jedes Fahrzeug gehört genau einem Nutzer, kein Teilen
**Speicherung:** Offline-first lokal (SQLite) + selbst gehostetes Backend im Heimnetz, vorbereitet für globalen Zugriff

---

## 1. Zielbild

Eine Mehrbenutzer-App: Jede Person meldet sich mit eigenem Konto an und verwaltet ihre eigenen Fahrzeuge — ein Fahrzeug gehört genau einem Nutzer, es gibt kein Teilen und keine Zweitzuordnung. Die Startseite zeigt die Fahrzeuge als **Karten** mit Bild und den wichtigsten Infos auf einen Blick (km-Stand, nächster TÜV, nächster Service, Verbindungsstatus).

Pro Fahrzeug verbindet sich die App mit einem OBD2-Adapter, erkennt das Auto idealerweise automatisch per VIN, liest Kilometerstand und Livedaten aus und baut daraus Fahrtenbuch, Wartungsplaner und Kostenübersicht. Alle Daten liegen lokal auf dem Gerät und syncen im Heim-WLAN mit dem eigenen Backend — später auch von unterwegs.

---

## 2. Architektur-Überblick

### 2.1 App (.NET MAUI Blazor Hybrid)

```
┌─────────────────────────────────────────────┐
│  UI: Blazor-Komponenten (BlazorWebView)     │
│  Dashboard · Fahrzeuge · Fahrten · Wartung  │
├─────────────────────────────────────────────┤
│  App-Services (C#, DI)                      │
│  VehicleService · TripRecorder ·            │
│  MaintenanceService · SyncService           │
├──────────────────────┬──────────────────────┤
│  OBD-Schicht         │  Datenschicht        │
│  Elm327Client        │  SQLite (EF Core     │
│  ObdPidParser        │  oder sqlite-net)    │
│  IObdTransport ◄──┐  │  Repositories        │
├───────────────────┼──┴──────────────────────┤
│  Transporte (austauschbar):                 │
│  · BluetoothClassicTransport (nur Android)  │
│  · BleTransport (Android + iOS)             │
│  · WifiTcpTransport (Android + iOS)         │
└─────────────────────────────────────────────┘
```

**Kernprinzip 1: UI ist austauschbar.** Die Blazor-Komponenten sind eine dünne Präsentationsschicht ohne eigene Logik. Sie sprechen ausschließlich gegen Interfaces bzw. UI-unabhängige Services (`IVehicleService`, `ITripRecorder`, `IObdConnectionManager`, …), die in separaten Klassenbibliotheken ohne jede MAUI-/Blazor-Referenz liegen. Konkret heißt das:

- **Keine Datenzugriffe in `.razor`-Dateien** — kein DbContext, kein SQL, kein HttpClient direkt in Komponenten. Komponenten rufen Services, Services nutzen Repositories.
- **Zustand lebt in Services, nicht in Komponenten:** z.B. hält ein `LiveDataService` die aktuellen OBD-Werte und feuert Events/`IObservable` — die Blazor-Seite abonniert nur und rendert. Eine spätere native XAML-Seite oder ein Web-Frontend könnte denselben Service abonnieren.
- **DTO-/Modelltrennung:** Die UI bekommt View-Modelle bzw. Domänenmodelle aus `CarApp.Core`, nie EF-Entities mit Persistenz-Details.
- Damit ist das Frontend tauschbar: MAUI-Blazor heute, z.B. natives MAUI-XAML, ein reines Blazor-WASM-Web-Frontend gegen das Backend oder etwas ganz anderes morgen — ohne die Daten-, OBD- oder Sync-Schicht anzufassen.

**Kernprinzip 2: Transport-Abstraktion.** Die ELM327-Logik (AT-Befehle, PID-Anfragen, Parsing) spricht nur gegen ein Interface `IObdTransport` (Connect, Send, ReadLine, Disconnect). Darunter liegen drei Implementierungen:

| Transport | Android | iOS | Bemerkung |
|---|---|---|---|
| Bluetooth Classic (SPP) | ✅ nativ (Android.Bluetooth) | ❌ nicht möglich | Die billigen ELM327-Klone |
| BLE | ✅ | ✅ | z.B. via Plugin.BLE; Adapter wie vLinker MC+, OBDLink CX |
| WLAN (TCP, meist 192.168.0.10:35000) | ✅ | ✅ | `TcpClient`; iOS braucht Local-Network-Permission |

**Wichtige Konsequenz für iOS:** Nutzer mit Billig-Bluetooth-Adaptern können unter iOS nicht verbinden. Die App sollte beim Adapter-Setup den Typ erkennen/abfragen und auf iOS klar kommunizieren, welche Adapter funktionieren.

### 2.2 Backend (Heimserver)

- **ASP.NET Core (Minimal API)** im Docker-Container, lauffähig auf NAS, Raspberry Pi oder Heimserver.
- Datenbank: **PostgreSQL** (oder SQLite für den Anfang — bei Einzelnutzer völlig ausreichend, Postgres erleichtert später Mehrgeräte/Mehrnutzer).
- REST-API, versioniert (`/api/v1/...`).
- App erlaubt Konfiguration von **zwei URLs**: lokale URL (Heimnetz) und optionale externe URL, mit automatischem Fallback.

### 2.2a Benutzerkonten & Login

- **ASP.NET Core Identity** im Backend: Konten mit E-Mail + Passwort, Passwort-Hashing, Lockout etc. sind fertig dabei.
- **JWT (Access-Token, kurzlebig) + Refresh-Token (langlebig)**: Die App speichert die Tokens im sicheren Speicher (`SecureStorage`) und erneuert sie automatisch — der Nutzer meldet sich einmal an und bleibt angemeldet.
- **Offline-Verhalten (wichtig fürs Auto):** Der Login wird nur beim ersten Einrichten und beim Token-Refresh gebraucht. Ist das Backend nicht erreichbar (unterwegs), arbeitet die App normal mit den lokalen Daten des zuletzt angemeldeten Nutzers weiter. Kein Internet ≠ ausgesperrt.
- **Registrierung:** offen oder per Admin/Einladungscode — bei einem Heimserver empfiehlt sich Einladungscode, damit sich nicht Fremde registrieren, sobald das Backend global erreichbar ist.
- **Datentrennung strikt serverseitig:** Jede API-Abfrage filtert auf die `UserId` aus dem Token. Ein Nutzer kann fremde Fahrzeuge weder sehen noch erraten (GUIDs + Autorisierungsprüfung pro Datensatz).
- Später global erreichbar via Reverse Proxy (Caddy/Traefik + Let's Encrypt) oder VPN (WireGuard/Tailscale) — an der App ändert sich dann nur die Basis-URL. HTTPS ist ab dann Pflicht (Tokens!).

### 2.3 Sync-Konzept (offline-first)

Das Handy ist im Auto meist getrennt vom Heimnetz — daher:

1. **Lokale SQLite ist immer die Quelle der Wahrheit für die App.** Alles funktioniert ohne Backend-Verbindung (nach dem ersten Login).
2. **Alle lokalen Daten sind dem angemeldeten Nutzer zugeordnet.** Der Sync überträgt nur die Daten dieses Nutzers; der Server prüft die Zuordnung zusätzlich anhand des Tokens.
3. Jede Entität bekommt: `Guid Id`, `DateTimeOffset ModifiedAt`, `bool IsDeleted` (Soft Delete), `SyncState` (Synced/Pending).
4. Ein `SyncService` läuft, wenn das Backend erreichbar ist (App-Start, App im Vordergrund, manuell): Push aller `Pending`-Änderungen, dann Pull aller Änderungen seit letztem Sync (`?since=timestamp`).
5. Konfliktstrategie v1: **Last-Write-Wins** anhand `ModifiedAt` — da jedes Fahrzeug genau einem Nutzer gehört, sind Konflikte nur zwischen dessen eigenen Geräten möglich; das ist einfach und praktisch konfliktfrei.
6. Messdaten (TripSamples) sind append-only → kein Konfliktpotenzial, nur Upload.

---

## 3. Datenmodell (Entwurf)

| Entität | Wichtige Felder | Zweck |
|---|---|---|
| **User** | Id, E-Mail, PasswordHash (via ASP.NET Identity), Anzeigename | Benutzerkonto (nur im Backend; App speichert Tokens + UserId) |
| **Vehicle** | Id, **OwnerUserId**, Name, VIN, Kennzeichen, Marke/Modell, Baujahr, Foto, TüvBis (Datum), OdometerSource (OBD/Manuell/Geschätzt) | Fahrzeugprofil — gehört genau einem Nutzer |
| **AdapterProfile** | Id, VehicleId?, Typ (BT/BLE/WLAN), MAC/UUID/IP:Port, Name | Gespeicherte Adapter, Zuordnung zu Fahrzeug |
| **OdometerReading** | Id, VehicleId, Wert (km), Quelle (OBD-PID/manuell/berechnet), Zeitstempel | km-Historie |
| **Trip** | Id, VehicleId, Start/Ende (Zeit), StartKm, EndKm, Distanz, Dauer, Kategorie (privat/geschäftlich), Notiz | Fahrtenbuch |
| **ObdSample** | Id, VehicleId, TripId (optional), PID/Wertetyp, Zeitstempel, Wert | **Jeder gepollte Livewert wird historisiert** — auch außerhalb von Fahrten (z.B. Spannung im Stand). Long-Format: eine Zeile pro Wert und Zeitpunkt |
| **MaintenanceTask** | Id, VehicleId, Typ (Öl/TÜV/Reifen/frei), Intervall (km und/oder Monate), zuletzt erledigt (km + Datum) | Wartungsplaner |
| **FuelEntry** | Id, VehicleId, Datum, Liter, Preis, km-Stand, Volltank-Flag | Tankbuch → Verbrauchsberechnung |
| **Expense** | Id, VehicleId, Datum, Kategorie (Werkstatt/Versicherung/…), Betrag, Notiz | Sonstige Kosten |

Alle Entitäten mit den Sync-Feldern aus 2.3. Zeitstempel durchgängig als `DateTimeOffset` (UTC), IDs als GUIDs (client-seitig erzeugbar, sync-freundlich). Alle fahrzeugbezogenen Entitäten hängen über `VehicleId` am Fahrzeug und erben damit dessen Besitzer — die Autorisierung prüft immer die Kette bis zur `OwnerUserId`.

**TÜV-Modellierung:** TÜV/HU ist einfach eine `MaintenanceTask` vom Typ "TÜV" mit reinem Zeitintervall (24 Monate) bzw. festem Fälligkeitsdatum (`TüvBis` am Fahrzeug als Komfortfeld für die Karte). So erscheint er automatisch in Erinnerungen und auf der Fahrzeugkarte.

**Fahrzeugfotos:** lokal als Datei, im Sync als separater Upload-Endpoint (`/vehicles/{id}/photo`), nicht als Blob in der Datenbank-Sync-Payload — hält den Sync schlank.

---

## 4. OBD-Schicht im Detail

### 4.1 ELM327-Kommunikation

- Init-Sequenz: `ATZ` (Reset) → `ATE0` (Echo aus) → `ATL0`/`ATS0` (Formatierung) → `ATSP0` (Protokoll automatisch) → erste PID-Anfrage `0100` (weckt das Steuergerät, ermittelt unterstützte PIDs).
- **Supported-PIDs-Discovery** (`0100`, `0120`, `0140`, …): Die App fragt pro Fahrzeug einmal ab, welche PIDs das Auto kann, und speichert das im Fahrzeugprofil. Das Dashboard bietet dann nur an, was wirklich verfügbar ist.
- **VIN auslesen** (Mode 09, PID 02): Grundlage für die automatische Fahrzeugerkennung — Adapter verbinden, VIN lesen, passendes Fahrzeugprofil aktivieren (oder Anlage eines neuen vorschlagen).
- Polling-Loop mit Prioritäten: schnelle Werte (Drehzahl, Geschwindigkeit) häufig, langsame (Temperaturen, Spannung) seltener. ELM327-Klone schaffen oft nur ~5–15 Anfragen/Sekunde — das Polling muss darauf ausgelegt sein (Timeouts, Retry, Reconnect).

### 4.2 Kilometerstand — die realistische Strategie

Das ist der heikelste Punkt des Projekts, deshalb dreistufig:

1. **Standard-PID `01 A6`** (Odometer): funktioniert bei vielen Autos ab ca. 2019. Zuerst probieren.
2. **Herstellerspezifisch (UDS Mode 22)**: pro Marke unterschiedliche DIDs, teils andere CAN-Header nötig. Als spätere Erweiterung mit einer kleinen, wachsenden Lookup-Tabelle je Marke — nicht MVP-kritisch.
3. **Fallback: manuell + fortgeschrieben.** Nutzer gibt den km-Stand einmal manuell ein; die App schreibt ihn anhand der aufgezeichneten Fahrtdistanzen (aus OBD-Geschwindigkeit integriert) fort und bittet gelegentlich um Abgleich. Plausibilitätsprüfung bei manuellen Eingaben (kein Rückwärtslaufen).

Die `OdometerSource` pro Fahrzeug macht transparent, wie verlässlich der Wert ist.

### 4.3 Fahrtenbuch-Trigger

- Fahrt beginnt: erfolgreiche OBD-Verbindung + Drehzahl > 0 bzw. Geschwindigkeit > 0.
- Fahrt endet: Zündung aus (Verbindungsverlust / RPM = 0 über x Sekunden).
- **Plattform-Realität:** iOS beendet Apps im Hintergrund aggressiv. Für v1 gilt: Aufzeichnung läuft zuverlässig, solange die App offen ist (typisch: Handy in der Halterung). Automatischer Hintergrund-Start ist auf iOS nur eingeschränkt machbar (BLE-Background-Modes) — als spätere Ausbaustufe einplanen, nicht versprechen.

---

## 5. Features im MVP

### 5.1 Fahrzeug-Übersicht als Karten (Startseite)

Die Startseite nach dem Login ist die **Garage**: eine Kartenliste der eigenen Fahrzeuge. Jede Karte zeigt:

- Fahrzeugfoto (oder Platzhalter mit Markenfarbe/Initialen), Name + Kennzeichen
- aktueller **km-Stand** (mit Quelle-Indikator: OBD/manuell/geschätzt)
- **nächster TÜV** mit Restzeit-Badge (grün > 3 Monate, gelb < 3 Monate, rot überfällig)
- **nächster Service** (die am dringendsten fällige Wartungsaufgabe, z.B. "Ölwechsel in 1.200 km")
- Verbindungsstatus (Adapter in Reichweite / verbunden / getrennt)

Tippen öffnet die Fahrzeug-Detailseite (Tabs: Dashboard, Fahrten, Wartung, Kosten). Ein Floating-Button "Fahrzeug hinzufügen" startet den Anlege-Assistenten (manuell oder per Adapter verbinden → VIN auslesen → Daten vorausfüllen).

Dazu die Verwaltung: Fahrzeuge anlegen/bearbeiten/löschen, Foto aufnehmen oder wählen, VIN-Auto-Erkennung beim Verbinden, Adapter-Verwaltung mit Verbindungstest.

### 5.1a Login & Konto

Login-Screen (E-Mail + Passwort), Registrierung (per Einladungscode), "angemeldet bleiben" via Refresh-Token, Abmelden, Passwort ändern. Nach dem ersten Login voll offline nutzbar; ein Nutzerwechsel auf demselben Gerät erfordert Backend-Erreichbarkeit.

### 5.2 Live-Dashboard + Werte-Historie
Frei belegbare Kacheln/Anzeigen (Drehzahl, Geschwindigkeit, Kühlmittel, Ansauglufttemperatur, Batteriespannung, Motorlast, ggf. Ladedruck), nur verfügbare PIDs wählbar, Warnschwellen (z.B. Kühlmittel > 105 °C → Hinweis).

**Alle Livewerte werden immer historisiert** (nicht nur auf Wunsch): Jeder gepollte Wert landet als `ObdSample` in der lokalen DB, mit Fahrt-Zuordnung wenn gerade eine Fahrt läuft. Dazu eine **Verlaufsansicht** pro Wert: Zeitraum wählen (Fahrt / Tag / Woche / frei), Kurve mit Min/Avg/Max, mehrere Werte übereinanderlegbar (z.B. Drehzahl vs. Kühlmitteltemperatur).

Damit die DB auf dem Handy nicht explodiert, gehört eine **Retention-Strategie** dazu:

- Rohdaten (volle Polling-Rate, ~1 Hz) werden z.B. 90 Tage behalten,
- danach automatische Verdichtung auf Minuten-Aggregate (Min/Avg/Max pro Wert),
- Aggregate bleiben dauerhaft; das Backend kann optional die Rohdaten unbegrenzt behalten (Server hat Platz, Sync ist append-only).
- Zeitraum/Verhalten in den Einstellungen konfigurierbar.

### 5.3 Kilometerstand + Wartung
km-Anzeige mit Quelle und Historie (Kurve), Wartungsaufgaben mit km-/Zeitintervall, Restanzeige ("noch 1.200 km oder 2 Monate bis Ölwechsel"), lokale Benachrichtigungen, Erledigen mit km-Stempel.

### 5.4 Fahrtenbuch + Kosten
Automatische Fahrterfassung (Start/Ende, Distanz, Dauer, Durchschnitts-/Maxwerte), Kategorisierung privat/geschäftlich, Tankbuch mit Verbrauchsberechnung (Volltankmethode), Kosten pro km und Monatsübersicht, CSV-Export.

**Bewusst nicht im MVP:** Fehlercode-Diagnose (DTCs), Performance-Messungen, EV-Daten, Mehrbenutzer — die Architektur lässt all das später zu (Diagnose ist mit fertiger OBD-Schicht ein kleiner Schritt).

---

## 6. Technologie-Entscheidungen

| Bereich | Wahl | Begründung |
|---|---|---|
| App-Framework | .NET MAUI Blazor Hybrid (aktuelles .NET) | Vorgabe; eine UI-Codebasis, durchgängig C# |
| BLE | Plugin.BLE (oder Shiny.BluetoothLE) | Etabliert, Android + iOS |
| BT Classic | Android-native API hinter `IObdTransport` | Gibt es nur auf Android, daher Plattformcode ok |
| Lokale DB | SQLite + EF Core (oder sqlite-net-pcl) | Offline-first, LINQ, Migrationen |
| Charts | LiveCharts2 / Blazor-ApexCharts | km-Historie, Fahrt-Diagramme |
| Backend | ASP.NET Core Minimal API + EF Core, Docker | Gleiche Sprache, geteilte DTOs via Shared-Projekt |
| Auth | ASP.NET Core Identity + JWT/Refresh-Token | Mehrbenutzer mit E-Mail+Passwort, komplett selbst gehostet |
| CI | GitHub Actions (Android-Build; iOS-Build braucht Mac) | Früh automatisieren |

**Projektstruktur (Solution):**

```
CarApp.sln
├── CarApp.App          (MAUI Blazor Hybrid — NUR UI: .razor-Komponenten, DI-Bootstrap,
│                        plattformspezifische Transport-Implementierungen)
├── CarApp.Core         (Domänenmodelle + Interfaces: IVehicleService, ITripRecorder,
│                        IVehicleRepository, … — referenziert NICHTS von MAUI/Blazor/EF)
├── CarApp.Application  (Implementierung der Services/Geschäftslogik: TripRecorder,
│                        MaintenanceService, SyncService — nur Abhängigkeit auf Core)
├── CarApp.Data         (Persistenz: EF Core/SQLite, Repositories, Migrationen —
│                        implementiert die Repository-Interfaces aus Core)
├── CarApp.Obd          (ELM327, IObdTransport, PID-Parser — reine Klassenbibliothek)
├── CarApp.Shared       (DTOs App ↔ Backend)
├── CarApp.Server       (ASP.NET Core Backend, nutzt Shared + eigene Persistenz)
└── CarApp.Tests        (Unit-Tests: PID-Parsing, Services, Sync-Logik — läuft ohne UI)
```

**Abhängigkeitsrichtung (strikt eingehalten):** `App → Application/Data/Obd → Core`. Die UI kennt nur Interfaces aus Core; Application und Data kennen keinerlei UI. Ein Frontend-Tausch bedeutet: neues UI-Projekt schreiben, DI-Registrierung übernehmen, fertig — Core, Application, Data, Obd und Server bleiben unangetastet. Netter Nebeneffekt: Application und Data sind komplett unit-testbar, und die OBD-Bibliothek lässt sich mit aufgezeichneten Antwort-Strings ganz ohne Auto testen.

---

## 7. Phasenplan

| Phase | Inhalt | Meilenstein |
|---|---|---|
| **0 – Setup** | Solution-Struktur mit Schichtentrennung (Core/Application/Data/Obd/App), DI-Grundgerüst, MAUI Blazor lauffähig auf Android + iOS, CI, SQLite eingebunden | "Hello World" auf beiden Plattformen, Architektur-Skelett steht |
| **1 – OBD-Kern** | `IObdTransport` + WLAN-Transport + Android BT Classic, ELM327-Init, PID-Parser mit Tests, **Debug-Terminal-Seite** (rohe Befehle senden) | Live-Drehzahl vom echten Auto auf dem Handy |
| **2 – Fahrzeuge + Daten** | Fahrzeug-CRUD, **Karten-Übersicht (Garage)** mit Foto/TÜV/Service-Badges, Adapter-Profile, VIN-Erkennung, Supported-PID-Scan, lokale DB komplett | Garage-Ansicht steht; Adapter verbinden → richtiges Auto wird erkannt |
| **3 – Live-Dashboard** | Konfigurierbare Anzeigen, Polling-Loop mit Prioritäten, Warnschwellen | Nutzbares Dashboard während der Fahrt |
| **4 – km + Wartung** | PID A6 + manueller Fallback + Fortschreibung, km-Historie, Wartungsaufgaben + Notifications | Wartungsplaner funktioniert |
| **5 – Fahrtenbuch + Kosten** | Trip-Erkennung, Aufzeichnung, Tankbuch, Auswertungen, CSV-Export | MVP-Funktionsumfang komplett (lokal) |
| **6 – Backend, Konten + Sync** | ASP.NET-API + Identity (E-Mail/Passwort, JWT + Refresh), Login-/Registrierungs-Screens, Docker-Deployment zuhause, SyncService mit Nutzer-Scoping, lokale/externe URL | Login funktioniert; Daten syncen im Heim-WLAN, strikt pro Nutzer getrennt |
| **7 – iOS-Feinschliff + BLE** | BLE-Transport, iOS-Permissions (Bluetooth, Local Network), Adapter-Kompatibilitätshinweise, TestFlight | App auf iPhone mit BLE-/WLAN-Adapter nutzbar |

Reihenfolge-Logik: Phase 1 startet mit WLAN + Android-BT, weil das die schnellsten Erfolgserlebnisse liefert; BLE (fummeliger) kommt in Phase 7, die Architektur ist aber ab Tag 1 dafür vorbereitet. Backend bewusst nach dem lokalen MVP — offline-first heißt, die App ist vorher schon voll nutzbar.

**Hinweis zu den Konten:** Bis Phase 6 läuft die App mit einem impliziten lokalen Nutzer (alle Daten lokal, kein Login-Screen). Das Datenmodell trägt die `OwnerUserId` aber von Anfang an — beim ersten echten Login in Phase 6 werden die vorhandenen lokalen Daten diesem Konto zugeordnet. So blockiert das Backend die frühen Phasen nicht, und es gibt keine Migrations-Schmerzen.

---

## 8. Risiken & Stolpersteine

- **Kilometerstand nicht auslesbar** bei älteren Autos → Fallback-Strategie (4.2) ist fester Bestandteil, kein Randfall.
- **ELM327-Klone** sind unzuverlässig (abgespeckte Firmware, langsam, Verbindungsabbrüche) → robustes Timeout-/Reconnect-Handling, Debug-Terminal zum Diagnostizieren; für die Entwicklung einen hochwertigen Adapter (z.B. OBDLink) und einen Billig-Klon zum Testen anschaffen.
- **iOS-Einschränkungen**: kein BT Classic, Local-Network-Permission für WLAN-Adapter, Hintergrundausführung limitiert → Erwartungsmanagement in der App, Fahrtenbuch v1 = App im Vordergrund.
- **Android-Berechtigungen**: `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` (ab API 31), Notification-Permission — früh sauber implementieren.
- **Testen ohne Auto**: OBD-Simulator einplanen — Software-Fake-Transport (spielt aufgezeichnete Antworten ab, ideal für UI-Entwicklung) und/oder ein ECU-Simulator-Dongle (~50–100 €).
- **Backend-Erreichbarkeit unterwegs** (später): Empfehlung Tailscale/WireGuard statt offenem Port — sicherer und ohne eigene Domain machbar.
- **Tokens & Sicherheit**: JWTs nur über HTTPS, sobald das Backend das Heimnetz verlässt; Tokens in `SecureStorage`, nie in der SQLite; Registrierung per Einladungscode absichern, bevor der Server global erreichbar wird.

---

## 9. Nächste Schritte

1. Entwicklungs-Hardware klären: Welche Autos/Baujahre? (bestimmt, ob PID A6 realistisch ist) Welcher Adapter ist vorhanden?
2. Phase 0 starten: Solution aufsetzen, MAUI Blazor Hybrid auf dem Android-Gerät zum Laufen bringen.
3. Phase 1: Debug-Terminal bauen und erste echte ELM327-Session gegen das Auto fahren — ab da wird's greifbar.
