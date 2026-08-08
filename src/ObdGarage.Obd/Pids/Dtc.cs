namespace ObdGarage.Obd.Pids;

/// <summary>
/// Diagnostic Trouble Code encode/decode per SAE J2012 / ISO 15031-6: each code is 2 raw bytes
/// from a mode 03 (stored) or mode 07 (pending) response.
///
/// Byte A: bits 7-6 select the category letter (00=P Powertrain, 01=C Chassis, 10=B Body,
/// 11=U Network), bits 5-4 are the first digit (0-3), bits 3-0 are the second digit (hex).
/// Byte B: bits 7-4 are the third digit, bits 3-0 are the fourth digit (both hex).
/// Example: 01 33 → P0133 (upstream O2 sensor slow response).
/// </summary>
public static class Dtc
{
    /// <summary>Decodes one 2-byte DTC into its 5-character code (e.g. "P0301").</summary>
    public static string Decode(byte a, byte b)
    {
        var letter = (a >> 6) switch
        {
            0 => 'P',
            1 => 'C',
            2 => 'B',
            _ => 'U',
        };
        var digit1 = (a >> 4) & 0x03;
        var digit2 = a & 0x0F;
        var digit3 = (b >> 4) & 0x0F;
        var digit4 = b & 0x0F;
        return $"{letter}{digit1}{digit2:X}{digit3:X}{digit4:X}";
    }

    /// <summary>
    /// Splits a raw mode 03/07 payload (mode echo already stripped) into individual DTC code
    /// strings, skipping "00 00" filler pairs (unused slots / no-code padding) and any leftover
    /// odd trailing byte.
    /// </summary>
    public static IReadOnlyList<string> DecodeAll(IReadOnlyList<byte> payload)
    {
        var codes = new List<string>();
        for (var i = 0; i + 1 < payload.Count; i += 2)
        {
            if (payload[i] == 0 && payload[i + 1] == 0)
                continue;
            codes.Add(Decode(payload[i], payload[i + 1]));
        }
        return codes;
    }

    /// <summary>Encodes a code string (e.g. "P0301") back to its 2 raw bytes - mainly for tests/the simulator.</summary>
    public static (byte A, byte B) Encode(string code)
    {
        if (code.Length != 5)
            throw new ArgumentException($"DTC muss 5 Zeichen lang sein: '{code}'.", nameof(code));

        var category = char.ToUpperInvariant(code[0]) switch
        {
            'P' => 0,
            'C' => 1,
            'B' => 2,
            'U' => 3,
            _ => throw new ArgumentException($"Unbekannte DTC-Kategorie: '{code[0]}'.", nameof(code)),
        };
        var digit1 = code[1] - '0';
        if (digit1 is < 0 or > 3)
            throw new ArgumentException($"Erste DTC-Ziffer muss 0-3 sein: '{code}'.", nameof(code));
        var digit2 = Convert.ToByte(code[2].ToString(), 16);
        var digit3 = Convert.ToByte(code[3].ToString(), 16);
        var digit4 = Convert.ToByte(code[4].ToString(), 16);

        var a = (byte)((category << 6) | (digit1 << 4) | digit2);
        var b = (byte)((digit3 << 4) | digit4);
        return (a, b);
    }
}
