using System.Globalization;
using System.Text;
using ObdGarage.Obd.Pids;

namespace ObdGarage.Obd.Transport;

/// <summary>State of the simulated vehicle — externally controllable (test scenarios, UI demo).</summary>
public sealed class SimulatedCar
{
    public string Vin { get; set; } = "WSIMTEST000000001";
    public double Rpm { get; set; } = 800;
    public double SpeedKmh { get; set; }
    public double CoolantTempC { get; set; } = 82;
    public double IntakeTempC { get; set; } = 25;
    public double EngineLoadPct { get; set; } = 18;
    public double VoltageV { get; set; } = 13.8;
    public double OdometerKm { get; set; } = 123_456.7;
    /// <summary>False simulates an older car without the standard odometer PID.</summary>
    public bool SupportsOdometer { get; set; } = true;
    /// <summary>Stored (mode 03) diagnostic trouble codes - empty by default (healthy vehicle).</summary>
    public List<string> Dtcs { get; set; } = [];
    /// <summary>Pending (mode 07) diagnostic trouble codes - empty by default.</summary>
    public List<string> PendingDtcs { get; set; } = [];
}

/// <summary>
/// Full ELM327 emulation over the transport interface: answers
/// AT commands, mode-01 PIDs (from the current <see cref="SimulatedCar"/> state),
/// supported-PID masks, and the VIN. This allows the client, polling, and
/// trip logging to be tested fully end-to-end without a car.
/// </summary>
public sealed class SimulatedCarTransport(SimulatedCar car) : IObdTransport
{
    private byte[] _pending = [];
    private int _offset;

    public SimulatedCar Car { get; } = car;

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var cmd = Encoding.ASCII.GetString(data.Span).Trim('\r', '\n', ' ').ToUpperInvariant();
        _pending = Encoding.ASCII.GetBytes(Respond(cmd));
        _offset = 0;
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset >= _pending.Length)
            return Task.FromResult(0);
        var n = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return Task.FromResult(n);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private string Respond(string cmd)
    {
        if (cmd.StartsWith("AT", StringComparison.Ordinal))
        {
            return cmd switch
            {
                "ATZ" => "\r\rELM327 v1.5 (SIM)\r\r>",
                "ATRV" => Car.VoltageV.ToString("0.0", CultureInfo.InvariantCulture) + "V\r\r>",
                _ => "OK\r\r>",
            };
        }

        if (cmd == "0902")
            return VinResponse();

        if (cmd == "03")
            return DtcResponse(0x43, Car.Dtcs);
        if (cmd == "07")
            return DtcResponse(0x47, Car.PendingDtcs);

        if (cmd.Length == 4 && cmd.StartsWith("01", StringComparison.Ordinal))
        {
            var pid = Convert.ToByte(cmd.Substring(2, 2), 16);
            return PidResponse(pid);
        }

        return "?\r\r>";
    }

    private static string DtcResponse(byte modeEcho, IReadOnlyList<string> codes)
    {
        if (codes.Count == 0)
            return "NO DATA\r\r>"; // matches how several real adapters answer "zero codes stored"

        var bytes = new List<byte> { modeEcho };
        foreach (var code in codes)
        {
            var (a, b) = Dtc.Encode(code);
            bytes.Add(a);
            bytes.Add(b);
        }
        return string.Join(' ', bytes.Select(x => x.ToString("X2"))) + "\r\r>";
    }

    private HashSet<byte> SupportedPids()
    {
        var set = new HashSet<byte> { 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x10, 0x11, 0x42 };
        if (Car.SupportsOdometer)
            set.Add(0xA6);
        return set;
    }

    private string PidResponse(byte pid)
    {
        var supported = SupportedPids();

        // Supported-PID masks (0x00, 0x20, 0x40, …)
        if (pid % 0x20 == 0 && pid <= 0xC0)
        {
            uint bits = 0;
            for (int i = 1; i <= 32; i++)
            {
                if (supported.Contains((byte)(pid + i)))
                    bits |= 1u << (32 - i);
            }
            if (supported.Any(p => p > pid + 0x20))
                bits |= 1; // Continue bit
            if (bits == 0)
                return "NO DATA\r\r>";
            var m = new[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits };
            return $"41 {pid:X2} {m[0]:X2} {m[1]:X2} {m[2]:X2} {m[3]:X2}\r\r>";
        }

        if (!supported.Contains(pid))
            return "NO DATA\r\r>";

        byte[] payload = pid switch
        {
            0x04 => [(byte)Math.Clamp(Car.EngineLoadPct * 255.0 / 100.0, 0, 255)],
            0x05 => [(byte)Math.Clamp(Car.CoolantTempC + 40, 0, 255)],
            0x0B => [101],
            0x0C => EncodeU16((ushort)Math.Clamp(Car.Rpm * 4, 0, ushort.MaxValue)),
            0x0D => [(byte)Math.Clamp(Car.SpeedKmh, 0, 255)],
            0x0F => [(byte)Math.Clamp(Car.IntakeTempC + 40, 0, 255)],
            0x10 => EncodeU16(1234),
            0x11 => [(byte)Math.Clamp(Car.EngineLoadPct * 255.0 / 100.0, 0, 255)],
            0x42 => EncodeU16((ushort)Math.Clamp(Car.VoltageV * 1000, 0, ushort.MaxValue)),
            0xA6 => EncodeU32((uint)Math.Round(Car.OdometerKm * 10)),
            _ => [],
        };

        if (payload.Length == 0)
            return "NO DATA\r\r>";
        var hex = string.Join(' ', payload.Select(b => b.ToString("X2")));
        return $"41 {pid:X2} {hex}\r\r>";
    }

    private string VinResponse()
    {
        var vinBytes = Encoding.ASCII.GetBytes(Car.Vin);
        var payload = new List<byte> { 0x49, 0x02, 0x01 };
        payload.AddRange(vinBytes);

        // ISO-TP multi-frame representation as from a real CAN vehicle
        var sb = new StringBuilder();
        sb.Append((payload.Count).ToString("X3")).Append('\r');
        for (int frame = 0; frame * 7 < payload.Count; frame++)
        {
            var chunk = payload.Skip(frame * 7).Take(7);
            sb.Append(frame.ToString("X"))
              .Append(": ")
              .Append(string.Join(' ', chunk.Select(b => b.ToString("X2"))))
              .Append('\r');
        }
        sb.Append("\r>");
        return sb.ToString();
    }

    private static byte[] EncodeU16(ushort v) => [(byte)(v >> 8), (byte)v];

    private static byte[] EncodeU32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
