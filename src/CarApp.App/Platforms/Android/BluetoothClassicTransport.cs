using Android.Bluetooth;
using Android.Content;
using CarApp.Obd.Transport;

namespace CarApp.App.Services;

/// <summary>
/// IObdTransport over Bluetooth Classic (SPP/RFCOMM) — the transport for the
/// common (cheap) ELM327 adapters. Android only; the file lives under
/// Platforms/Android and, thanks to SingleProject, is compiled exclusively for
/// net10.0-android. iOS cannot do BT Classic (Plan 2.1) — there,
/// WiFi (WifiTcpTransport) and later BLE (BleTransport) remain.
///
/// Prerequisites for the caller:
///  - The device is already paired at the system level (pairing via Android settings
///    or later via a dedicated device list in the app).
///  - The BLUETOOTH_CONNECT runtime permission has been granted (from Android 12 on), e.g. via
///    Permissions.RequestAsync&lt;Permissions.Bluetooth&gt;() — otherwise Connect throws
///    a SecurityException.
/// </summary>
public sealed class BluetoothClassicTransport(string macAddress) : IObdTransport
{
    /// <summary>Well-known SPP UUID (Serial Port Profile) — spoken by all ELM327 BT adapters.</summary>
    private static readonly Java.Util.UUID SppUuid =
        Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    private BluetoothSocket? _socket;
    private Stream? _input;
    private Stream? _output;
    private Java.IO.InputStream? _javaInput; // for Available() — non-blocking polling

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsConnected => _socket?.IsConnected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        // BluetoothManager instead of the deprecated BluetoothAdapter.DefaultAdapter.
        var manager = (BluetoothManager?)Android.App.Application.Context
            .GetSystemService(Context.BluetoothService);
        var adapter = manager?.Adapter
            ?? throw new InvalidOperationException("Dieses Gerät hat keinen Bluetooth-Adapter.");
        if (!adapter.IsEnabled)
            throw new InvalidOperationException("Bluetooth ist deaktiviert — bitte in den Einstellungen einschalten.");

        BluetoothDevice device;
        try
        {
            device = adapter.GetRemoteDevice(macAddress)
                ?? throw new InvalidOperationException($"Bluetooth-Gerät {macAddress} nicht gefunden.");
        }
        catch (Java.Lang.IllegalArgumentException ex)
        {
            throw new ArgumentException($"Ungültige Bluetooth-MAC-Adresse: '{macAddress}'.", nameof(macAddress), ex);
        }

        // Ongoing discovery massively slows down RFCOMM connections — cancel it if possible.
        // Requires the BLUETOOTH_SCAN permission from Android 12 on; not fatal without it.
        try { adapter.CancelDiscovery(); }
        catch (Java.Lang.SecurityException) { /* discovery just keeps running then */ }

        var socket = device.CreateRfcommSocketToServiceRecord(SppUuid)
            ?? throw new InvalidOperationException("RFCOMM-Socket konnte nicht erstellt werden.");

        // socket.Connect() blocks with no timeout of its own — we enforce one
        // by closing the socket on expiry/cancellation (this aborts Connect).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        using var closeOnCancel = timeout.Token.Register(() =>
        {
            try { socket.Close(); }
            catch { /* socket may already have been closed */ }
        });

        try
        {
            await Task.Run(socket.Connect, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { socket.Close(); } catch { /* doesn't matter */ }
            socket.Dispose();

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Bluetooth-Verbindungsaufbau abgebrochen.", ex, ct);
            if (timeout.IsCancellationRequested)
                throw new TimeoutException(
                    $"Keine Bluetooth-Verbindung zu {macAddress} innerhalb von {ConnectTimeout.TotalSeconds:0}s " +
                    "(Adapter eingesteckt, Zündung an, Gerät gekoppelt?).", ex);
            throw new IOException(
                $"Bluetooth-Verbindung zu {macAddress} fehlgeschlagen — Adapter außer Reichweite oder nicht gekoppelt?", ex);
        }

        _socket = socket;
        _input = socket.InputStream
            ?? throw new InvalidOperationException("Bluetooth-Socket liefert keinen InputStream.");
        _output = socket.OutputStream
            ?? throw new InvalidOperationException("Bluetooth-Socket liefert keinen OutputStream.");
        // Java stream for Available(): allows reading without a blocking thread.
        _javaInput = (_input as Android.Runtime.InputStreamInvoker)?.BaseInputStream;
    }

    public Task DisconnectAsync()
    {
        try { _input?.Dispose(); } catch { /* cleanup must never throw */ }
        try { _output?.Dispose(); } catch { /* ditto */ }
        try { _socket?.Close(); } catch { /* ditto */ }
        _socket?.Dispose();
        _socket = null;
        _input = null;
        _output = null;
        _javaInput = null;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var output = _output ?? throw new InvalidOperationException("Nicht verbunden.");
        await output.WriteAsync(data, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads available bytes. Important: Elm327Client cancels ReadAsync on every
    /// command timeout — so a cancellation here must NOT close the connection.
    /// We therefore poll Available() and only read when data is available; the delay
    /// in between is cleanly cancellable. Return value 0 = connection closed.
    /// </summary>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var input = _input ?? throw new InvalidOperationException("Nicht verbunden.");

        // Fallback without a Java stream (should practically never happen): blocking read.
        if (_javaInput is null)
            return await input.ReadAsync(buffer, ct).ConfigureAwait(false);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int available;
            try
            {
                available = _javaInput.Available();
            }
            catch (Java.IO.IOException)
            {
                return 0; // remote end closed the connection
            }

            if (available > 0)
            {
                var count = Math.Min(available, buffer.Length);
                return await input.ReadAsync(buffer[..count], ct).ConfigureAwait(false);
            }

            if (!IsConnected)
                return 0;

            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
