using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ObdGarage.Obd.Pids;
using ObdGarage.Obd.Transport;

namespace ObdGarage.Obd;

/// <summary>Thrown when a command is not on the read-only whitelist.</summary>
public sealed class ObdCommandBlockedException(string command)
    : InvalidOperationException($"Befehl '{command}' ist nicht erlaubt (Nur-Lese-Whitelist).")
{
    public string Command { get; } = command;
}

/// <summary>Thrown when the adapter reports an error or returns no data.</summary>
public sealed class ObdErrorException(string command, string response)
    : Exception($"OBD-Fehler bei '{command}': {response}")
{
    public string Command { get; } = command;
    public string Response { get; } = response;
}

/// <summary>
/// ELM327 protocol client. Communicates with the adapter over an <see cref="IObdTransport"/>.
/// Safety principle: only commands from the read-only whitelist are ever sent —
/// no DTC clearing (mode 04), no write commands, no UDS writes.
/// </summary>
public sealed class Elm327Client(IObdTransport transport) : IAsyncDisposable
{
    /// <summary>
    /// Allowed AT commands (adapter configuration, has no effect on the vehicle side).
    /// Deliberately strict: e.g. ATSH (Set Header) is NOT allowed.
    /// </summary>
    private static readonly Regex AllowedAtCommand = new(
        @"^AT(Z|I|RV|PC|DPN?|E[01]|L[01]|S[01]|H[01]|SP[0-9A-C]|AT[0-2]|ST[0-9A-F]{1,2})$",
        RegexOptions.Compiled);

    /// <summary>Allowed OBD modes: 01 live data, 02 freeze frame, 03/07 read DTCs, 09 vehicle info.</summary>
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

    /// <summary>Standard initialization: reset, echo off, formatting, protocol automatic.</summary>
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
    /// Sends a command (after whitelist check) and returns the raw response
    /// up to the '&gt;' prompt — cleaned of echo and blank lines.
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

    /// <summary>Queries a PID and returns the decoded physical value.</summary>
    public async Task<double> ReadPidAsync(PidDefinition pid, CancellationToken ct = default)
    {
        var payload = await QueryAsync(pid.RequestCommand, ct).ConfigureAwait(false);
        if (payload.Length < pid.PayloadLength)
            throw new ObdErrorException(pid.RequestCommand, $"Payload zu kurz ({payload.Length} Bytes)");
        return pid.Decode(payload);
    }

    /// <summary>Determines all mode-01 PIDs supported by the vehicle (0100, 0120, …).</summary>
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
                break; // Range not supported → done
            }

            foreach (var pid in StandardPids.DecodeSupportedPidMask(range, mask))
                supported.Add(pid);

            // Last bit of the mask indicates whether the next range exists. SAE J1979 defines
            // ranges only up to 0xE0 (PIDs 0xE1-0x100) - that hard cutoff stops the scan there
            // regardless of the bit (avoiding an infinite loop / byte overflow past 0xE0), but
            // must NOT fire any earlier than that, or the mask's own continuation bit for a
            // legitimate higher range (e.g. 0xC0 signalling 0xE0 exists) gets silently ignored.
            if (mask.Length < 4 || (mask[3] & 0x01) == 0 || range >= 0xE0)
                break;
        }
        return supported;
    }

    /// <summary>
    /// Reads diagnostic trouble codes: stored (mode 03, the default) or pending (mode 07,
    /// detected but not yet confirmed/MIL-triggering). Both are read-only requests, distinct
    /// from the permanently-blocked mode 04 (clear codes) - see the class doc comment.
    /// An empty list means no codes are currently stored/pending, not a read failure.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadDtcsAsync(bool pending = false, CancellationToken ct = default)
    {
        byte[] payload;
        try
        {
            payload = await QueryAsync(pending ? "07" : "03", ct).ConfigureAwait(false);
        }
        catch (ObdErrorException)
        {
            // Some adapters/vehicles answer "NO DATA" when there are zero stored/pending codes
            // rather than an explicit empty/zero-filled response - a healthy state, not an error.
            return [];
        }

        return Dtc.DecodeAll(payload);
    }

    /// <summary>Reads the vehicle identification number (mode 09, PID 02) — basis for automatic vehicle detection.</summary>
    public async Task<string?> ReadVinAsync(CancellationToken ct = default)
    {
        var response = await SendRawAsync("0902", ct).ConfigureAwait(false);
        var bytes = ExtractHexBytes(response);

        // After the identifier 49 02 <sequence> come the ASCII bytes of the VIN.
        var ascii = new StringBuilder();
        for (int i = 0; i < bytes.Count; i++)
        {
            if (i + 1 < bytes.Count && bytes[i] == 0x49 && bytes[i + 1] == 0x02)
            {
                i += 2; // Skip identifier and sequence counter
                continue;
            }
            if (bytes[i] is >= 0x20 and < 0x7F)
                ascii.Append((char)bytes[i]);
        }

        var candidate = new string(ascii.ToString().Where(char.IsLetterOrDigit).ToArray());
        return candidate.Length >= 17 ? candidate[^17..] : null;
    }

    /// <summary>Battery voltage directly from the adapter (ATRV), works even with ignition off.</summary>
    public async Task<double?> ReadAdapterVoltageAsync(CancellationToken ct = default)
    {
        var response = await SendRawAsync("ATRV", ct).ConfigureAwait(false);
        var m = Regex.Match(response, @"(\d+(?:\.\d+)?)\s*V", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Sends an OBD request (e.g. "010C") and returns the payload bytes
    /// after the mode echo (0x40 + mode) and PID.
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
            // ExtractHexBytes already validates/decodes hex content per line and yields an
            // empty/short result for anything that isn't valid data, which the checks below
            // skip anyway - no separate pre-filter needed (and one used to reject lines
            // ExtractHexBytes could otherwise parse fine, see the CAN-header fix there).
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
            // Remove line prefixes from multi-frame responses ("0:", "1:", …)
            var content = Regex.Replace(line.Trim(), @"^[0-9A-F]{1,3}:", "");
            // Remove a leading CAN arbitration ID (11-bit "7E8" or 29-bit "18DAF110"), present
            // on every response line once "headers on" (ATH1) is in effect. ATH1 is on the
            // read-only whitelist (documented as harmless - it configures the adapter, not the
            // vehicle), so a caller can legally leave it in effect; without stripping it here,
            // every subsequent response line fails the hex-pair checks below and is discarded.
            content = Regex.Replace(content, @"^[0-9A-F]{3}(?:[0-9A-F]{5})?\s+", "");
            // Skip ISO-TP length lines like "014" (3 hex characters alone)
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
