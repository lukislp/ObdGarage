using System.Net.Sockets;

namespace CarApp.Obd.Transport;

/// <summary>
/// Transport für WLAN-OBD-Adapter (der Adapter spannt ein eigenes WLAN auf,
/// typischer Endpunkt 192.168.0.10:35000). Läuft auf allen Plattformen.
/// </summary>
public sealed class WifiTcpTransport(string host, int port = 35000) : IObdTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        await _client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    public Task DisconnectAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Nicht verbunden.");
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Nicht verbunden.");
        return await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
