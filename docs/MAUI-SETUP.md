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

## 5. Extract the web UI into a shared Razor Class Library (ObdGarage.UI)

The pages in `src/ObdGarage.Web/Components` are already UI-thin (services instead of
logic in the components) — which is why they can be moved step by step into an RCL
shared by Web **and** MAUI:

1. Create and reference the RCL:
   ```bash
   dotnet new razorclasslib -n ObdGarage.UI -o src/ObdGarage.UI
   dotnet sln ObdGarage.slnx add src/ObdGarage.UI/ObdGarage.UI.csproj
   dotnet add src/ObdGarage.UI reference src/ObdGarage.Core src/ObdGarage.Application src/ObdGarage.Obd src/ObdGarage.Shared
   dotnet add src/ObdGarage.Web reference src/ObdGarage.UI
   dotnet add src/ObdGarage.App reference src/ObdGarage.UI
   ```
2. Move the components: move pages/parts out of `ObdGarage.Web/Components` into
   `src/ObdGarage.UI/`, adjust the namespaces to `ObdGarage.UI.…` and pull them into
   both hosts with `@using ObdGarage.UI`. Do **not** take along
   web-specific things like `App.razor`/`Routes.razor` (those stay in Web) — the MAUI app
   gets its own `Routes.razor` with `<Router AppAssembly="typeof(ObdGarage.UI.…).Assembly">`.
3. Service wiring stays per host: both register the same services
   (see `ObdGarage.Web/Program.cs` vs. `ObdGarage.App/MauiProgram.cs`) — the
   components only inject interfaces/services and never notice where they run.
4. The RCL's static assets end up under `_content/ObdGarage.UI/…` — extend the paths in
   `index.html` (MAUI) and `App.razor` (Web) accordingly.
5. Solve differing behaviour (e.g. ConnectionManager with Bluetooth only in the
   app) with additional services registered per host, not with `#if` in the UI.
6. Afterwards, replace `Components/Main.razor` in ObdGarage.App with the real start page from
   the RCL (switch the RootComponent in `MainPage.xaml`).

## 6. Backend on the home network (sync)

- The server's default port according to `src/ObdGarage.Server/Properties/launchSettings.json`:
  **http://localhost:5235**. From the phone, `localhost` is the wrong host —
  make the server listen on all interfaces:
  ```bash
  dotnet run --project src/ObdGarage.Server --urls http://0.0.0.0:5235
  ```
  (or change `applicationUrl` in the launchSettings to `http://0.0.0.0:5235`
  and open the port in the machine's firewall).
- In `src/ObdGarage.App/MauiProgram.cs`, set the `DefaultSyncBaseUrl` constant to the
  server's home-network IP (e.g. `http://192.168.0.100:5235/`) — until a settings
  page takes that over.
- Android emulator: the host machine is `10.0.2.2` there, not `192.168.x.x`.
- On the road (Plan 8): Tailscale/WireGuard instead of an open port; as soon as the server
  leaves the home network, HTTPS only, and tokens in `SecureStorage`.

## 7. Known pitfalls

- `XA5300 / Android SDK not found`: run `dotnet build -t:InstallAndroidDependencies -f net10.0-android` once, or set the SDK path via `AndroidSdkDirectory`.
- The first Android build downloads a lot (AOT profiles, SDK parts) — it takes a while.
- Testing against the simulator in the app works completely offline — the ideal first
  smoke test on any device, before real adapters come into play.
