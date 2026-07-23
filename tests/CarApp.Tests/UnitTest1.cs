using CarApp.Obd;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

namespace CarApp.Tests;

/// <summary>Reine Dekodier-Tests: Antwort-Bytes → physikalischer Wert (SAE J1979).</summary>
public class PidDecodeTests
{
    [Fact]
    public void Rpm_DecodesCorrectly() =>
        Assert.Equal(1726.0, StandardPids.Rpm.Decode([0x1A, 0xF8]));

    [Fact]
    public void Speed_DecodesCorrectly() =>
        Assert.Equal(75.0, StandardPids.Speed.Decode([0x4B]));

    [Fact]
    public void CoolantTemp_DecodesWithOffset() =>
        Assert.Equal(83.0, StandardPids.CoolantTemp.Decode([0x7B]));

    [Fact]
    public void ModuleVoltage_DecodesToVolts() =>
        Assert.Equal(13.2, StandardPids.ControlModuleVoltage.Decode([0x33, 0x90]), 3);

    [Fact]
    public void Maf_DecodesToGramsPerSecond() =>
        Assert.Equal(12.34, StandardPids.Maf.Decode([0x04, 0xD2]), 3);

    [Fact]
    public void EngineLoad_DecodesToPercent() =>
        Assert.Equal(100.0, StandardPids.EngineLoad.Decode([0xFF]), 3);

    [Fact]
    public void Odometer_PidA6_DecodesToTenthKilometers()
    {
        // 0x0012D687 = 1.234.567 → 123.456,7 km
        Assert.Equal(123456.7, StandardPids.Odometer.Decode([0x00, 0x12, 0xD6, 0x87]), 3);
    }

    [Fact]
    public void SupportedPidMask_DecodesKnownExample()
    {
        // Klassisches Beispiel: 41 00 BE 1F A8 13
        var supported = StandardPids
            .DecodeSupportedPidMask(0x00, [0xBE, 0x1F, 0xA8, 0x13])
            .ToHashSet();

        Assert.Contains((byte)0x0C, supported); // Drehzahl
        Assert.Contains((byte)0x0D, supported); // Geschwindigkeit
        Assert.Contains((byte)0x11, supported); // Drosselklappe
        Assert.Contains((byte)0x20, supported); // nächster Bereich existiert
        Assert.DoesNotContain((byte)0x02, supported);
        Assert.DoesNotContain((byte)0x08, supported);
    }
}

/// <summary>Sicherheits-Whitelist: Es dürfen nur Lese-Befehle rausgehen.</summary>
public class CommandWhitelistTests
{
    [Theory]
    [InlineData("010C")]   // Livedaten
    [InlineData("0100")]   // Supported PIDs
    [InlineData("01A6")]   // Odometer
    [InlineData("0902")]   // VIN
    [InlineData("03")]     // DTCs lesen
    [InlineData("ATZ")]
    [InlineData("ATE0")]
    [InlineData("ATSP0")]
    [InlineData("ATRV")]
    public void Allows_ReadOnlyCommands(string command) =>
        Assert.True(Elm327Client.IsCommandAllowed(command));

    [Theory]
    [InlineData("04")]        // Fehlercodes löschen — verboten!
    [InlineData("2E1234FF")]  // UDS WriteDataByIdentifier — verboten!
    [InlineData("ATSH7E0")]   // Header setzen — verboten
    [InlineData("10")]        // UDS Session Control — verboten
    [InlineData("3101FF00")]  // UDS Routine Control — verboten
    public void Blocks_WriteCommands(string command) =>
        Assert.False(Elm327Client.IsCommandAllowed(command));

    [Fact]
    public async Task SendRaw_ThrowsOnBlockedCommand()
    {
        await using var client = new Elm327Client(new ReplayTransport());
        await Assert.ThrowsAsync<ObdCommandBlockedException>(() => client.SendRawAsync("04"));
    }
}

/// <summary>Integrationstests des Clients gegen den simulierten Adapter.</summary>
public class Elm327ClientTests
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
    public async Task Initialize_SendsStandardSequence()
    {
        var transport = NewInitializedScript();
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        Assert.True(client.IsInitialized);
        Assert.Equal(["ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"], transport.SentCommands);
    }

    [Fact]
    public async Task ReadPid_Rpm_ParsesResponseWithEchoAndSearching()
    {
        var transport = NewInitializedScript()
            .OnCommand("010C", "010C\rSEARCHING...\r41 0C 1A F8\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var rpm = await client.ReadPidAsync(StandardPids.Rpm);

        Assert.Equal(1726.0, rpm);
    }

    [Fact]
    public async Task ReadPid_Odometer_ReturnsKilometers()
    {
        var transport = NewInitializedScript()
            .OnCommand("01A6", "41 A6 00 12 D6 87\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var km = await client.ReadPidAsync(StandardPids.Odometer);

        Assert.Equal(123456.7, km, 3);
    }

    [Fact]
    public async Task Query_NoData_ThrowsObdError()
    {
        var transport = NewInitializedScript()
            .OnCommand("0105", "NO DATA\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        await Assert.ThrowsAsync<ObdErrorException>(() => client.QueryAsync("0105"));
    }

    [Fact]
    public async Task GetSupportedPids_WalksRangesUntilLastBitClear()
    {
        var transport = NewInitializedScript()
            .OnCommand("0100", "41 00 BE 1F A8 13\r\r>")
            .OnCommand("0120", "41 20 80 00 00 00\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var supported = await client.GetSupportedPidsAsync();

        Assert.Contains((byte)0x0C, supported);
        Assert.Contains((byte)0x0D, supported);
        Assert.Contains((byte)0x21, supported); // aus Bereich 0120
        Assert.DoesNotContain((byte)0x02, supported);
        // Bereich 0140 wurde nicht mehr abgefragt (letztes Bit von 0120 war 0):
        Assert.DoesNotContain("0140", transport.SentCommands);
    }

    [Fact]
    public async Task ReadVin_ParsesCanMultiFrameResponse()
    {
        var transport = NewInitializedScript()
            .OnCommand("0902", "014\r0: 49 02 01 57 30 4C\r1: 30 30 30 30 34 33 4D\r2: 45 35 34 33 32 31 39\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var vin = await client.ReadVinAsync();

        Assert.Equal("W0L000043ME543219", vin);
    }

    [Fact]
    public async Task ReadVin_ParsesLegacyMultiLineResponse()
    {
        var transport = NewInitializedScript()
            .OnCommand("0902",
                "49 02 01 00 00 00 57\r49 02 02 30 4C 30 30\r49 02 03 30 30 34 33\r49 02 04 4D 45 35 34\r49 02 05 33 32 31 39\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var vin = await client.ReadVinAsync();

        Assert.Equal("W0L000043ME543219", vin);
    }

    [Fact]
    public async Task ReadAdapterVoltage_ParsesAtRv()
    {
        var transport = NewInitializedScript()
            .OnCommand("ATRV", "12.6V\r\r>");
        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var volts = await client.ReadAdapterVoltageAsync();

        Assert.Equal(12.6, volts!.Value, 3);
    }
}
