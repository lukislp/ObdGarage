# MAUI setup — building ObdGarage.App for Android/iOS

`src/ObdGarage.App` is the .NET MAUI Blazor Hybrid shell (Plan 2.1). It is
**deliberately not listed in `ObdGarage.slnx`**, so that `dotnet build`/`dotnet test`
on the solution keep working on machines without the MAUI workload (CI, sandbox).

Requirements: Windows or macOS with the .NET 10 SDK. iOS strictly requires a Mac
(or Windows plus a Mac on the network for "Pair to Mac") with a current Xcode.

## 1. Install the MAUI workload

```bash
dotnet workload install maui
# Verify:
dotnet workload list
```

The workload also supplies `$(MauiVersion)` — that is how the two PackageReferences in
`ObdGarage.App.csproj` (`Microsoft.Maui.Controls`, `Microsoft.AspNetCore.Components.WebView.Maui`)
resolve without an explicit version.

## 2. Add the project to the solution

```bash
cd ObdGarage
dotnet sln ObdGarage.slnx add src/ObdGarage.App/ObdGarage.App.csproj
```

(To remove it again: `dotnet sln ObdGarage.slnx remove src/ObdGarage.App/ObdGarage.App.csproj` —
e.g. before building on a machine without the workload.)

## 3. Build and deploy for Android

```bash
# Build only:
dotnet build src/ObdGarage.App -f net10.0-android

# APK onto the device connected over USB (enable USB debugging!):
dotnet build src/ObdGarage.App -f net10.0-android -t:Run

# Alternatively by hand via adb:
dotnet publish src/ObdGarage.App -f net10.0-android -c Release
adb install src/ObdGarage.App/bin/Release/net10.0-android/publish/com.obdgarage.mobile-Signed.apk
adb devices          # device visible?
adb logcat -s DOTNET # the app's logs
```

Android notes:
- An emulator is enough for UI work; Bluetooth Classic needs a real device.
- From Android 12 on, `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` are **runtime** permissions:
  call `Permissions.RequestAsync<Permissions.Bluetooth>()` before the first connection
  (the manifest is already prepared, including `neverForLocation`).
- The ELM327 adapter has to be paired in the Android settings beforehand
  (PIN is usually `1234` or `0000`); the app then connects by MAC address through
  `BluetoothClassicTransport` (SPP UUID 00001101-0000-1000-8000-00805F9B34FB).

## 4. Build for iOS

```bash
# On the Mac (device connected by cable, trusted once in Xcode):
dotnet build src/ObdGarage.App -f net10.0-ios -t:Run

# From Windows: "Pair to Mac" in Visual Studio, or build directly on the Mac.
```

Important iOS limitations (Plan 2.1/8):
- **Bluetooth Classic (SPP) does NOT work on iOS** — the cheap ELM327 BT adapters
  will never work there. Only **BLE** adapters (e.g. vLinker MC+,
  OBDLink CX — implementation: `Services/BleTransport.cs`, Phase 7) and **Wi-Fi** adapters
  (`WifiTcpTransport`, already working today) are usable.
- On the first TCP access to the Wi-Fi adapter/home server, iOS shows the
  local-network dialog (`NSLocalNetworkUsageDescription` is set in the Info.plist).
- Device builds need an Apple developer account plus provisioning; distribution
  to testers goes through TestFlight (Phase 7).

## 5. How Web and MAUI share the UI (ObdGarage.UI)

The Blazor UI lives in `src/ObdGarage.UI`, a Razor Class Library referenced by **both** hosts,
so a page exists exactly once and the two never drift apart. `ObdGarage.Web/Components` only
keeps the web-specific shell (`App.razor`, `Routes.razor`, the error page); `ObdGarage.App`
keeps its MAUI shell (`MainPage.xaml`, `Components/Routes.razor`). A component added to the
RCL shows up in the web app and in the mobile app at the same time, without touching a host.

What lives in the RCL:

- `Pages/` — `Home`, `VehicleDetail`, `VehicleForm`, `Settings`, `NotFound`
- `VehicleTabs/` — Dashboard, Diagnose, Fahrten, Kosten, Verlauf, Wartung
- `Layout/MainLayout.razor` — the shared layout
- `SvgChart.cs`, `Fmt.cs` — chart rendering and culture-independent formatting
- `wwwroot/app.css` — all shared styling

It references `ObdGarage.Core`, `.Application`, `.Obd` and `.Shared` only — no `ObdGarage.Data`,
no host-specific packages — so the components inject interfaces and never learn where they run.

**Routing is per host**, because the routable `@page` components sit in a different assembly
than either host:

- Web (`ObdGarage.Web/Components/Routes.razor`): `AppAssembly="typeof(Program).Assembly"` plus
  `AdditionalAssemblies="new[] { typeof(ObdGarage.UI.Pages.Home).Assembly }"`. The same assembly
  additionally has to be passed to `MapRazorComponents<App>().AddAdditionalAssemblies(…)` in
  `Program.cs` — without that, ASP.NET Core's own routing middleware never learns the RCL's
  pages exist and every route 404s before Blazor gets to render.
- MAUI (`ObdGarage.App/Components/Routes.razor`): the RCL assembly *is* the `AppAssembly`;
  `MainPage.xaml` mounts that `Routes` component as the `BlazorWebView`'s root component.

**Static assets** of the RCL are served from `_content/ObdGarage.UI/…`: both
`ObdGarage.Web/Components/App.razor` and `ObdGarage.App/wwwroot/index.html` link
`_content/ObdGarage.UI/app.css`. `ObdGarage.Web` has no `wwwroot` of its own; the MAUI app adds
only its host-specific `wwwroot/css/app.css` on top of the shared stylesheet.

**Service wiring stays per host** — that is what lets the same components run unchanged on both:

- `ObdGarage.Web/Program.cs`: data directory under the content root, `AppState` and `ISyncManager`
  registered **scoped** (per Blazor Server circuit, so one browser tab's sync login cannot switch
  another tab's identity), `SyncManager` persisting to `sync-auth.json`.
- `ObdGarage.App/MauiProgram.cs`: data directory `FileSystem.AppDataDirectory`, `AppState` and
  `ISyncManager` as **singletons** (one long-lived window, no tabs), and `SecureStorageSyncManager`
  instead — sync credentials live in the platform's `SecureStorage`.
- Shared between both: the EF Core/SQLite repositories, `IClock`, `PhotoStorage`,
  `OdometerTracker`, `ConnectionManager`, migrations plus the JSON→SQLite import on startup.

Platform-only behaviour (e.g. Bluetooth Classic, which exists on Android only) is handled the same
way: an extra service registered by the host that has it, never a `#if` inside the UI project —
`src/ObdGarage.UI` contains none.

## 6. Backend on the home network (sync)

- The server's default port according to `src/ObdGarage.Server/Properties/launchSettings.json`:
  **http://localhost:5235**. From the phone, `localhost` is the wrong host —
  make the server listen on all interfaces:
  ```bash
  dotnet run --project src/ObdGarage.Server --urls http://0.0.0.0:5235
  ```
  (or change `applicationUrl` in the launchSettings to `http://0.0.0.0:5235`
  and open the port in the machine's firewall).
- The address is no longer baked into the app: enter the server's home-network URL
  (e.g. `http://192.168.0.100:5235`) once in the app's **Settings** page, next to the sync
  login — it is persisted from then on (on the phone through `SecureStorageSyncManager`).
  The placeholder shown there is `SyncService.DefaultServerUrl` (`http://localhost:5299`),
  which only works when server and client run on the same machine.
- Android emulator: the host machine is `10.0.2.2` there, not `192.168.x.x`.
- On the road (Plan 8): Tailscale/WireGuard instead of an open port; as soon as the server
  leaves the home network, HTTPS only, and tokens in `SecureStorage`.

## 7. Known pitfalls

- `XA5300 / Android SDK not found`: run `dotnet build -t:InstallAndroidDependencies -f net10.0-android` once, or set the SDK path via `AndroidSdkDirectory`.
- The first Android build downloads a lot (AOT profiles, SDK parts) — it takes a while.
- Testing against the simulator in the app works completely offline — the ideal first
  smoke test on any device, before real adapters come into play.
