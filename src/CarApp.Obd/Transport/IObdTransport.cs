namespace CarApp.Obd.Transport;

/// <summary>
/// Abstraktion über die physische Verbindung zum ELM327-Adapter
/// (Bluetooth Classic, BLE oder WLAN/TCP). Die ELM327-Logik kennt nur dieses Interface.
/// </summary>
public interface IObdTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>Sendet Rohbytes (Befehl inkl. abschließendem CR) an den Adapter.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Liest verfügbare Bytes. Rückgabe 0 bedeutet: Verbindung geschlossen.</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}
