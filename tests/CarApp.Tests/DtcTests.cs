using CarApp.Obd;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

namespace CarApp.Tests;

/// <summary>Pure DTC encode/decode tests (SAE J2012 / ISO 15031-6 2-byte packing).</summary>
public class DtcCodecTests
{
    [Theory]
    [InlineData("P0301", 0x03, 0x01)] // Powertrain, digit1=0
    [InlineData("P0133", 0x01, 0x33)]
    [InlineData("C0035", 0x40, 0x35)] // Chassis
    [InlineData("B0001", 0x80, 0x01)] // Body
    [InlineData("U0100", 0xC1, 0x00)] // Network
    [InlineData("P1234", 0x12, 0x34)] // Manufacturer-specific range (digit1=1)
    public void Decode_KnownExamples(string expected, byte a, byte b) =>
        Assert.Equal(expected, Dtc.Decode(a, b));

    [Theory]
    [InlineData("P0301")]
    [InlineData("P0133")]
    [InlineData("C0035")]
    [InlineData("B0001")]
    [InlineData("U0100")]
    [InlineData("P3421")]
    public void EncodeThenDecode_RoundTrips(string code)
    {
        var (a, b) = Dtc.Encode(code);
        Assert.Equal(code, Dtc.Decode(a, b));
    }

    [Fact]
    public void DecodeAll_SkipsZeroFillerPairs()
    {
        // P0301 (03 01), filler (00 00), P0420 (04 20)
        byte[] payload = [0x03, 0x01, 0x00, 0x00, 0x04, 0x20];

        var codes = Dtc.DecodeAll(payload);

        Assert.Equal(["P0301", "P0420"], codes);
    }

    [Fact]
    public void DecodeAll_EmptyPayload_ReturnsEmpty() =>
        Assert.Empty(Dtc.DecodeAll([]));

    [Fact]
    public void DecodeAll_IgnoresOddTrailingByte()
    {
        byte[] payload = [0x03, 0x01, 0x99]; // one full code + a stray trailing byte
        Assert.Equal(["P0301"], Dtc.DecodeAll(payload));
    }

    [Fact]
    public void Describe_KnownCode_ReturnsGermanText() =>
        Assert.Equal("Katalysator-Wirkungsgrad unter Schwellenwert (Bank 1)", DtcDescriptions.Describe("P0420"));

    [Fact]
    public void Describe_UnknownCode_ReturnsNull() =>
        Assert.Null(DtcDescriptions.Describe("P9999"));
}

/// <summary>Elm327Client.ReadDtcsAsync against a scripted transport.</summary>
public class Elm327ClientDtcTests
{
    private static ReplayTransport NewInitializedScript()
    {
        var t = new ReplayTransport();
        t.OnCommand("ATZ", "\r\rELM327 v1.5\r\r>");
        foreach (var c in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0" })
            t.OnCommand(c, "OK\r\r>");
        return t;
    }

    [Fact]
    public async Task ReadDtcs_StoredCodes_DecodesAll()
    {
        var transport = NewInitializedScript()
            .OnCommand("03", "43 03 01 04 20\r\r>"); // P0301, P0420
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var codes = await client.ReadDtcsAsync();

        Assert.Equal(["P0301", "P0420"], codes);
    }

    [Fact]
    public async Task ReadDtcs_Pending_UsesMode07()
    {
        var transport = NewInitializedScript()
            .OnCommand("07", "47 01 33\r\r>"); // pending P0133
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var codes = await client.ReadDtcsAsync(pending: true);

        Assert.Equal(["P0133"], codes);
    }

    [Fact]
    public async Task ReadDtcs_NoDataResponse_ReturnsEmptyNotError()
    {
        var transport = NewInitializedScript()
            .OnCommand("03", "NO DATA\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var codes = await client.ReadDtcsAsync();

        Assert.Empty(codes);
    }
}
