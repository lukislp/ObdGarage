namespace ObdGarage.Obd.Pids;

/// <summary>
/// Describes an OBD-II PID (mode 01): how it is requested and how the response bytes
/// are converted into a physical value.
/// </summary>
public sealed record PidDefinition(
    byte Pid,
    string Key,
    string DisplayName,
    string Unit,
    int PayloadLength,
    Func<byte[], double> Decode)
{
    /// <summary>The command sent to the adapter, e.g. "010C".</summary>
    public string RequestCommand => $"01{Pid:X2}";

    public override string ToString() => $"{Key} (01 {Pid:X2})";
}

/// <summary>
/// Registry of the standard PIDs supported by the app (SAE J1979, mode 01).
/// See the SAE standard for formulas; A = byte0, B = byte1, ...
/// </summary>
public static class StandardPids
{
    public static readonly PidDefinition EngineLoad = new(
        0x04, "engine_load", "Motorlast", "%", 1, p => p[0] * 100.0 / 255.0);

    public static readonly PidDefinition CoolantTemp = new(
        0x05, "coolant_temp", "Kühlmitteltemperatur", "°C", 1, p => p[0] - 40.0);

    public static readonly PidDefinition IntakePressure = new(
        0x0B, "intake_pressure", "Saugrohrdruck", "kPa", 1, p => p[0]);

    public static readonly PidDefinition Rpm = new(
        0x0C, "rpm", "Drehzahl", "1/min", 2, p => (p[0] * 256.0 + p[1]) / 4.0);

    public static readonly PidDefinition Speed = new(
        0x0D, "speed", "Geschwindigkeit", "km/h", 1, p => p[0]);

    public static readonly PidDefinition IntakeTemp = new(
        0x0F, "intake_temp", "Ansauglufttemperatur", "°C", 1, p => p[0] - 40.0);

    public static readonly PidDefinition Maf = new(
        0x10, "maf", "Luftmassenstrom", "g/s", 2, p => (p[0] * 256.0 + p[1]) / 100.0);

    public static readonly PidDefinition ThrottlePosition = new(
        0x11, "throttle", "Drosselklappe", "%", 1, p => p[0] * 100.0 / 255.0);

    public static readonly PidDefinition FuelLevel = new(
        0x2F, "fuel_level", "Tankfüllstand", "%", 1, p => p[0] * 100.0 / 255.0);

    public static readonly PidDefinition ControlModuleVoltage = new(
        0x42, "module_voltage", "Bordspannung", "V", 2, p => (p[0] * 256.0 + p[1]) / 1000.0);

    public static readonly PidDefinition AmbientTemp = new(
        0x46, "ambient_temp", "Außentemperatur", "°C", 1, p => p[0] - 40.0);

    public static readonly PidDefinition OilTemp = new(
        0x5C, "oil_temp", "Öltemperatur", "°C", 1, p => p[0] - 40.0);

    /// <summary>Odometer reading (only common from ~2019 onward). Value in km, 0.1 km resolution.</summary>
    public static readonly PidDefinition Odometer = new(
        0xA6, "odometer", "Kilometerstand", "km", 4,
        p => ((uint)p[0] << 24 | (uint)p[1] << 16 | (uint)p[2] << 8 | p[3]) / 10.0);

    public static readonly IReadOnlyList<PidDefinition> All = new[]
    {
        EngineLoad, CoolantTemp, IntakePressure, Rpm, Speed, IntakeTemp, Maf,
        ThrottlePosition, FuelLevel, ControlModuleVoltage, AmbientTemp, OilTemp, Odometer,
    };

    public static PidDefinition? ByPid(byte pid) => All.FirstOrDefault(d => d.Pid == pid);

    public static PidDefinition? ByKey(string key) =>
        All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Evaluates the bitmasks of the supported-PID queries (0100, 0120, 0140, …).
    /// <paramref name="rangeStart"/> is the PID of the query (0x00, 0x20, 0x40, …),
    /// <paramref name="mask"/> is the 4 response bytes. Bit 31 (MSB of A) = PID rangeStart+1.
    /// </summary>
    public static IEnumerable<byte> DecodeSupportedPidMask(byte rangeStart, byte[] mask)
    {
        if (mask.Length < 4) yield break;
        uint bits = (uint)mask[0] << 24 | (uint)mask[1] << 16 | (uint)mask[2] << 8 | mask[3];
        for (int i = 0; i < 32; i++)
        {
            if ((bits & (1u << (31 - i))) != 0)
                yield return (byte)(rangeStart + i + 1);
        }
    }
}
