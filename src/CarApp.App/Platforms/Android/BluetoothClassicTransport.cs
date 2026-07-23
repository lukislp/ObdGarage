using Android.Bluetooth;
using Android.Content;
using CarApp.Obd.Transport;

namespace CarApp.App.Services;

/// <summary>
/// IObdTransport über Bluetooth Classic (SPP/RFCOMM) — der Transport für die
/// verbreiteten (Billig-)ELM327-Adapter. Nur Android; die Datei liegt unter
/// Platforms/Android und wird dank SingleProject ausschließlich für
/// net10.0-android kompiliert. iOS kann kein BT Classic (Plan 2.1) — dort
/// bleiben WLAN (WifiTcpTransport) und später BLE (BleTransport).
///
/// Voraussetzungen beim Aufrufer:
///  - Gerät ist im System bereits gekoppelt (Pairing über die Android-Einstellungen
///    oder später über eine eigene Geräteliste in der App).
///  - Laufzeit-Berechtigung BLUETOOTH_CONNECT wurde erteilt (ab Android 12), z.B. via
///    Permissions.RequestAsync&lt;Permissions.Bluetooth&gt;() — sonst wirft Connect
///    eine SecurityException.
/// </summary>
public sealed class BluetoothClassicTransport(string macAddress) : IObdTransport
{
    /// <summary>Wohlbekannte SPP-UUID (Serial Port Profile) — die sprechen alle ELM327-BT-Adapter.</summary>
    private static readonly Java.Util.UUID SppUuid =
        Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    private BluetoothSocket? _socket;
    private Stream? _input;
    private Stream? _output;
    private Java.IO.InputStream? _javaInput; // für Available() — nicht-blockierendes Polling

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsConnected => _socket?.IsConnected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        // BluetoothManager statt des veralteten BluetoothAdapter.DefaultAdapter.
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

        // Laufende Discovery bremst RFCOMM-Verbindungen massiv — nach Möglichkeit abbrechen.
        // Braucht ab Android 12 die BLUETOOTH_SCAN-Berechtigung; ohne sie nicht fatal.
        try { adapter.CancelDiscovery(); }
        catch (Java.Lang.SecurityException) { /* Discovery läuft dann eben weiter */ }

        var socket = device.CreateRfcommSocketToServiceRecord(SppUuid)
            ?? throw new InvalidOperationException("RFCOMM-Socket konnte nicht erstellt werden.");

        // socket.Connect() blockiert ohne eigenes Timeout — wir erzwingen eines,
        // indem wir den Socket bei Ablauf/Abbruch schließen (bricht Connect ab).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        using var closeOnCancel = timeout.Token.Register(() =>
        {
            try { socket.Close(); }
            catch { /* Socket war ggf. schon zu */ }
        });

        try
        {
            await Task.Run(socket.Connect, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { socket.Close(); } catch { /* egal */ }
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
        // Java-Stream für Available(): erlaubt Lesen ohne blockierenden Thread.
        _javaInput = (_input as Android.Runtime.InputStreamInvoker)?.BaseInputStream;
    }

    public Task DisconnectAsync()
    {
        try { _input?.Dispose(); } catch { /* Aufräumen darf nie werfen */ }
        try { _output?.Dispose(); } catch { /* dito */ }
        try { _socket?.Close(); } catch { /* dito */ }
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
    /// Liest verfügbare Bytes. Wichtig: Der Elm327Client cancelt ReadAsync bei jedem
    /// Befehls-Timeout — deshalb darf ein Abbruch hier NICHT die Verbindung schließen.
    /// Wir pollen daher Available() und lesen nur, wenn Daten anliegen; das Delay
    /// dazwischen ist sauber abbrechbar. Rückgabe 0 = Verbindung geschlossen.
    /// </summary>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var input = _input ?? throw new InvalidOperationException("Nicht verbunden.");

        // Fallback ohne Java-Stream (sollte praktisch nie eintreten): blockierendes Lesen.
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
                return 0; // Gegenstelle hat die Verbindung geschlossen
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
