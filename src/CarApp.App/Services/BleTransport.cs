using CarApp.Obd.Transport;

namespace CarApp.App.Services;

/// <summary>
/// GERÜST für den BLE-Transport (Plan Phase 7) — Android UND iOS.
/// Noch nicht implementiert: alle Methoden werfen NotImplementedException,
/// damit versehentliche Nutzung sofort auffällt statt still zu hängen.
///
/// EMPFOHLENE UMSETZUNG (wenn Phase 7 ansteht):
///
/// 1. NuGet-Paket Plugin.BLE (dotnet add src/CarApp.App package Plugin.BLE)
///    — plattformneutrale API über CoreBluetooth (iOS) und Android-BLE.
///
/// 2. Geräte-Discovery: IAdapter.ScanForDevicesAsync(), Gerät per Name/Id merken
///    (AdapterProfile in CarApp.Core hält die Adresse/Id bereits als Feld).
///
/// 3. Service/Charakteristiken der üblichen BLE-OBD-Adapter — beim Verbinden
///    beide Varianten durchprobieren:
///    a) Nordic UART Service (NUS), z.B. vLinker MC+, OBDLink CX:
///       - Service:            6E400001-B5A3-F393-E0A9-E50E24DCCA9E
///       - Schreiben (TX):     6E400002-B5A3-F393-E0A9-E50E24DCCA9E (Write / WriteWithoutResponse)
///       - Empfangen (RX):     6E400003-B5A3-F393-E0A9-E50E24DCCA9E (Notify)
///    b) "FFF0"-Muster vieler günstiger BLE-Klone (z.B. Viecar/Vgate BLE 4.0):
///       - Service:            0000FFF0-0000-1000-8000-00805F9B34FB
///       - Empfangen (Notify): 0000FFF1-0000-1000-8000-00805F9B34FB
///       - Schreiben:          0000FFF2-0000-1000-8000-00805F9B34FB
///
/// 4. Datenfluss auf IObdTransport abbilden:
///    - SendAsync  → WriteAsync auf der TX-/FFF2-Charakteristik (auf max. ~20 Byte
///      pro Write stückeln, falls keine größere MTU ausgehandelt wurde).
///    - Notifications der RX-/FFF1-Charakteristik in eine interne Queue/Pipe
///      schreiben; ReadAsync bedient sich daraus (0 zurückgeben, wenn getrennt).
///    - IsConnected aus IDevice.State ableiten; DeviceDisconnected-Event abonnieren.
///
/// 5. TODO-Liste:
///    - [ ] Plugin.BLE referenzieren und Scan-/Verbindungs-UI bauen
///    - [ ] Runtime-Permissions: Android BLUETOOTH_SCAN/CONNECT, iOS-Dialog via
///          NSBluetoothAlwaysUsageDescription (Info.plist ist schon vorbereitet)
///    - [ ] NUS- und FFF0-Erkennung inkl. Fallback implementieren
///    - [ ] MTU-Request (Android: RequestMtuAsync(185+)) für weniger Stückelung
///    - [ ] Reconnect-Strategie (BLE-Klone brechen gern ab — Plan Abschnitt 8)
///    - [ ] Adapter-Kompatibilitätshinweise in der UI (iOS: kein BT Classic!)
/// </summary>
public sealed class BleTransport : IObdTransport
{
    private const string NotReady =
        "BLE-Transport ist noch nicht implementiert (Plan Phase 7). " +
        "Bitte WLAN-Adapter (WifiTcpTransport) oder unter Android Bluetooth Classic " +
        "(BluetoothClassicTransport) verwenden. Umsetzungsleitfaden: Kommentar in Services/BleTransport.cs.";

    public bool IsConnected => false;

    public Task ConnectAsync(CancellationToken ct = default) =>
        throw new NotImplementedException(NotReady);

    public Task DisconnectAsync() =>
        Task.CompletedTask; // Aufräumen ist immer erlaubt — bewusst kein Throw

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        throw new NotImplementedException(NotReady);

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw new NotImplementedException(NotReady);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
