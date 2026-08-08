namespace ObdGarage.Obd.Transport;

/// <summary>
/// Abstraction over the physical connection to the ELM327 adapter
/// (Bluetooth Classic, BLE, or WiFi/TCP). The ELM327 logic knows only this interface.
/// </summary>
public interface IObdTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>Sends raw bytes (command including trailing CR) to the adapter.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Reads available bytes. A return value of 0 means: connection closed.</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}
