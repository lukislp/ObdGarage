using ObdGarage.Obd.Transport;

namespace ObdGarage.App.Services;

/// <summary>
/// SCAFFOLD for the BLE transport (plan phase 7) — Android AND iOS.
/// Not yet implemented: all methods throw NotImplementedException,
/// so accidental use is immediately noticed instead of silently hanging.
///
/// RECOMMENDED IMPLEMENTATION (once phase 7 is due):
///
/// 1. NuGet package Plugin.BLE (dotnet add src/ObdGarage.App package Plugin.BLE)
///    — cross-platform API over CoreBluetooth (iOS) and Android BLE.
///
/// 2. Device discovery: IAdapter.ScanForDevicesAsync(), remember the device by name/id
///    (AdapterProfile in ObdGarage.Core already holds the address/id as a field).
///
/// 3. Service/characteristics of common BLE OBD adapters — try both variants
///    when connecting:
///    a) Nordic UART Service (NUS), e.g. vLinker MC+, OBDLink CX:
///       - Service:            6E400001-B5A3-F393-E0A9-E50E24DCCA9E
///       - Write (TX):         6E400002-B5A3-F393-E0A9-E50E24DCCA9E (Write / WriteWithoutResponse)
///       - Receive (RX):       6E400003-B5A3-F393-E0A9-E50E24DCCA9E (Notify)
///    b) "FFF0" pattern of many cheap BLE clones (e.g. Viecar/Vgate BLE 4.0):
///       - Service:            0000FFF0-0000-1000-8000-00805F9B34FB
///       - Receive (Notify):   0000FFF1-0000-1000-8000-00805F9B34FB
///       - Write:               0000FFF2-0000-1000-8000-00805F9B34FB
///
/// 4. Map the data flow onto IObdTransport:
///    - SendAsync  → WriteAsync on the TX/FFF2 characteristic (chunk into ~20 byte
///      writes max, unless a larger MTU was negotiated).
///    - Write notifications from the RX/FFF1 characteristic into an internal queue/pipe;
///      ReadAsync consumes from that (return 0 when disconnected).
///    - Derive IsConnected from IDevice.State; subscribe to the DeviceDisconnected event.
///
/// 5. TODO list:
///    - [ ] Reference Plugin.BLE and build a scan/connection UI
///    - [ ] Runtime permissions: Android BLUETOOTH_SCAN/CONNECT, iOS dialog via
///          NSBluetoothAlwaysUsageDescription (Info.plist is already prepared)
///    - [ ] Implement NUS and FFF0 detection including fallback
///    - [ ] MTU request (Android: RequestMtuAsync(185+)) for less chunking
///    - [ ] Reconnect strategy (BLE clones like to drop out — plan section 8)
///    - [ ] Adapter compatibility notes in the UI (iOS: no BT Classic!)
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
        Task.CompletedTask; // cleanup is always allowed — deliberately no throw

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        throw new NotImplementedException(NotReady);

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw new NotImplementedException(NotReady);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
