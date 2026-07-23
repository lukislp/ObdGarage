using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

namespace CarApp.Obd;

/// <summary>Wird geworfen, wenn ein Befehl nicht auf der Nur-Lese-Whitelist steht.</summary>
public sealed class ObdCommandBlockedException(string command)
    : InvalidOperationException($"Befehl '{command}' ist nicht erlaubt (Nur-Lese-Whitelist).")
{
    public string Command { get; } = command;
}

/// <summary>Wird geworfen, wenn der Adapter einen Fehler meldet oder keine Daten liefert.</summary>
public sealed class ObdErrorException(string command, string response)
    : Exception($"OBD-Fehler bei '{command}': {response}")
{
    public string Command { get; } = command;
    public string Response { get; } = response;
}

/// <summary>
/// ELM327-Protokoll-Client. Spricht über ein <see cref="IObdTransport"/> mit dem Adapter.
/// Sicherheitsprinzip: Es gehen ausschließlich Befehle von der Nur-Lese-Whitelist raus —
/// kein Fehlercode-Löschen (Mode 04), keine Schreibbefehle, keine UDS-Writes.
/// </summary>
public sealed class Elm327Client(IObdTransport transport) : IAsyncDisposable
{
    private static readonly Regex HexLine = new(@"^[0-9A-F]{2}(\s?[0-9A-F]{2})*$", RegexOptions.Compiled);

    /// <summary>
    /// Erlaubte AT-Befehle (Adapter-Konfiguration, fahrzeugseitig wirkungslos).
    /// Bewusst strikt: z.B. ist ATSH (Set Header) NICHT erlaubt.
    /// </summary>
    private static readonly Regex AllowedAtCommand = new(
        @"^AT(Z|I|RV|PC|DPN?|E[01]|L[01]|S[01]|H[01]|SP[0-9A-C]|AT[0-2]|ST[0-9A-F]{1,2})$",
        RegexOptions.Compiled);

    /// <summary>Erlaubte OBD-Modi: 01 Livedaten, 02 Freeze Frame, 03/07 DTC lesen, 09 Fahrzeuginfo.</summary>
    private static readonly Regex AllowedObdRequest = new(@"^(01|02|03|07|09)([0-9A-F]{2}){0,2}$", RegexOptions.Compiled);

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly StringBuilder _rx = new();

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(6);

    public bool IsInitialized { get; private set; }

    public static bool IsCommandAllowed(string command)
    {
        var c = Normalize(command);
        if (c.StartsWith("AT", StringComparison.Ordinal))
            return AllowedAtCommand.IsMatch(c);
        return AllowedObdRequest.IsMatch(c);
    }

    /// <summary>Standard-Initialisierung: Reset, Echo aus, Formatierung, Protokoll automatisch.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!transport.IsConnected)
            await transport.ConnectAsync(ct).ConfigureAwait(false);

        await SendRawAsync("ATZ", ct).ConfigureAwait(false);
        await SendRawAsync("ATE0", ct).ConfigureAwait(false);
        await SendRawAsync("ATL0", ct).ConfigureAwait(false);
        await SendRawAsync("ATS0", ct).ConfigureAwait(false);
        await SendRawAsync("ATH0", ct).ConfigureAwait(false);
        await SendRawAsync("ATSP0", ct).ConfigureAwait(false);
        IsInitialized = true;
    }

    /// <summary>
    /// Sendet einen Befehl (nach Whitelist-Prüfung) und liefert die Rohantwort
    /// bis zum Prompt '&gt;' — bereinigt um Echo und Leerzeilen.
    /// </summary>
    public async Task<string> SendRawAsync(string command, CancellationToken ct = default)
    {
        var cmd = Normalize(command);
        if (!IsCommandAllowed(cmd))
            throw new ObdCommandBlockedException(cmd);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await transport.SendAsync(Encoding.ASCII.GetBytes(cmd + "\r"), ct).ConfigureAwait(false);
            var raw = await ReadUntilPromptAsync(ct).ConfigureAwait(false);
            return CleanResponse(raw, cmd);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Fragt einen PID ab und liefert den dekodierten physikalischen Wert.</summary>
    public async Task<double> ReadPidAsync(PidDefinition pid, CancellationToken ct = default)
    {
        var payload = await QueryAsync(pid.RequestCommand, ct).ConfigureAwait(false);
        if (payload.Length < pid.PayloadLength)
            throw new ObdErrorException(pid.RequestCommand, $"Payload zu kurz ({payload.Length} Bytes)");
        return pid.Decode(payload);
    }

    /// <summary>Ermittelt alle vom Fahrzeug unterstützten Mode-01-PIDs (0100, 0120, …).</summary>
    public async Task<IReadOnlySet<byte>> GetSupportedPidsAsync(CancellationToken ct = default)
    {
        var supported = new HashSet<byte>();
        for (byte range = 0x00; ; range += 0x20)
        {
            byte[] mask;
            try
            {
                mask = await QueryAsync($"01{range:X2}", ct).ConfigureAwait(false);
            }
            catch (ObdErrorException)
            {
                break; // Bereich nicht unterstützt → fertig
            }

            foreach (var pid in StandardPids.DecodeSupportedPidMask(range, mask))
                supported.Add(pid);

            // Letztes Bit der Maske zeigt an, ob der nächste Bereich existiert.
            if (mask.Length < 4 || (mask[3] & 0x01) == 0 || range >= 0xC0)
                break;
        }
        return supported;
    }

    /// <summary>Liest die Fahrgestellnummer (Mode 09, PID 02) — Basis der Fahrzeug-Autoerkennung.</summary>
    public async Task<string?> ReadVinAsync(CancellationToken ct = default)
    {
        var response = await SendRawAsync("0902", ct).ConfigureAwait(false);
        var bytes = ExtractHexBytes(response);

        // Nach der Kennung 49 02 <Sequenz> stehen die ASCII-Bytes der VIN.
        var ascii = new StringBuilder();
        for (int i = 0; i < bytes.Count; i++)
        {
            if (i + 1 < bytes.Count && bytes[i] == 0x49 && bytes[i + 1] == 0x02)
            {
                i += 2; // Kennung und Sequenzzähler überspringen
                continue;
            }
            if (bytes[i] is >= 0x20 and < 0x7F)
                ascii.Append((char)bytes[i]);
        }

        var candidate = new string(ascii.ToString().Where(char.IsLetterOrDigit).ToArray());
        return candidate.Length >= 17 ? candidate[^17..] : null;
    }

    /// <summary>Batteriespannung direkt vom Adapter (ATRV), funktioniert auch bei Zündung aus.</summary>
    public async Task<double?> ReadAdapterVoltageAsync(CancellationToken ct = default)
    {
        var response = await SendRawAsync("ATRV", ct).ConfigureAwait(false);
        var m = Regex.Match(response, @"(\d+(?:\.\d+)?)\s*V", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Sendet eine OBD-Anfrage (z.B. "010C") und liefert die Payload-Bytes
    /// nach Modus-Echo (0x40 + Mode) und PID.
    /// </summary>
    public async Task<byte[]> QueryAsync(string request, CancellationToken ct = default)
    {
        var cmd = Normalize(request);
        var response = await SendRawAsync(cmd, ct).ConfigureAwait(false);

        if (response.Contains("NO DATA", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            response.Contains('?'))
            throw new ObdErrorException(cmd, response);

        var expectedMode = (byte)(Convert.ToByte(cmd[..2], 16) + 0x40);
        var expectedPid = cmd.Length >= 4 ? Convert.ToByte(cmd.Substring(2, 2), 16) : (byte?)null;

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = line.Replace(" ", "");
            if (!HexLine.IsMatch(line.Trim()) && !HexLine.IsMatch(normalized))
                continue;

            var bytes = ExtractHexBytes(line);
            if (bytes.Count < 2 || bytes[0] != expectedMode)
                continue;
            if (expectedPid is { } pid && bytes[1] != pid)
                continue;

            return bytes.Skip(expectedPid is null ? 1 : 2).ToArray();
        }

        throw new ObdErrorException(cmd, response);
    }

    private async Task<string> ReadUntilPromptAsync(CancellationToken ct)
    {
        _rx.Clear();
        var buffer = new byte[512];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);

        while (true)
        {
            int n;
            try
            {
                n = await transport.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Keine Antwort vom Adapter innerhalb von {CommandTimeout.TotalSeconds:0.#}s.");
            }

            if (n == 0)
                throw new ObdErrorException("<read>", "Verbindung geschlossen");

            _rx.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (_rx.ToString().Contains('>'))
                return _rx.ToString();
        }
    }

    private static string CleanResponse(string raw, string command)
    {
        var lines = raw.Replace(">", "")
            .Split('\r', '\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l => !l.Equals(command, StringComparison.OrdinalIgnoreCase)) // Echo
            .Where(l => !l.Equals("SEARCHING...", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', lines);
    }

    private static List<byte> ExtractHexBytes(string text)
    {
        var bytes = new List<byte>();
        foreach (var line in text.Split('\n'))
        {
            // Zeilenpräfixe von Multi-Frame-Antworten entfernen ("0:", "1:", …)
            var content = Regex.Replace(line.Trim(), @"^[0-9A-F]{1,3}:", "");
            // ISO-TP-Längenzeilen wie "014" (3 Hex-Zeichen allein) überspringen
            var compact = content.Replace(" ", "");
            if (compact.Length % 2 != 0)
                continue;
            if (!Regex.IsMatch(compact, "^[0-9A-Fa-f]*$"))
                continue;
            for (int i = 0; i + 1 < compact.Length; i += 2)
                bytes.Add(Convert.ToByte(compact.Substring(i, 2), 16));
        }
        return bytes;
    }

    private static string Normalize(string command) =>
        command.Replace(" ", "").Trim().ToUpperInvariant();

    public async ValueTask DisposeAsync()
    {
        _ioLock.Dispose();
        await transport.DisposeAsync().ConfigureAwait(false);
    }
}
