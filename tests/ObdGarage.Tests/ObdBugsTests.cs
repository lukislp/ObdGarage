using ObdGarage.Obd;
using ObdGarage.Obd.Pids;
using ObdGarage.Obd.Transport;

namespace ObdGarage.Tests;

/// <summary>
/// Bug-pinning tests for the OBD protocol/transport layer (src/ObdGarage.Obd/), covering
/// Elm327Client's response parsing and PID-discovery logic. Neither bug involves the
/// read-only command whitelist (Elm327Client.IsCommandAllowed/AllowedAtCommand/
/// AllowedObdRequest) itself - that whitelist was reviewed line-by-line and additionally
/// probed empirically against ~50 allowed/disallowed command strings (legitimate AT/mode-01/
/// 02/03/07/09 commands, mode-04 DTC-clear attempts, UDS write-style hex, ATSH/ATCRA/ATMA
/// header/monitor tricks, case variants, embedded-CR injection attempts, whitespace edge
/// cases); every one of them was classified correctly, so no whitelist-bypass bug was found.
/// </summary>
public class Elm327ClientBugTests
{
    private static ReplayTransport NewInitializedScript()
    {
        var t = new ReplayTransport();
        t.OnCommand("ATZ", "\r\rELM327 v1.5\r\r>");
        foreach (var c in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0" })
            t.OnCommand(c, "OK\r\r>");
        return t;
    }

    /// <summary>
    /// FIXED (Elm327Client.cs, ExtractHexBytes): both the multi-frame prefix stripper
    /// (<c>^[0-9A-F]{1,3}:</c>) and QueryAsync's line filter used to assume ELM327
    /// "headers off" formatting ("41 0C 1A F8"). "ATH1" (turn CAN headers on) is explicitly
    /// allowed by the read-only whitelist - the class doc comment even calls out AT commands
    /// as "has no effect on the vehicle side" - so any code holding an Elm327Client can
    /// legally call SendRawAsync("ATH1"). Once headers were on, every subsequent PID response
    /// arrived prefixed with the CAN arbitration ID ("7E8 41 0C 1A F8"), which the old parser
    /// silently discarded, failing ReadPidAsync for perfectly healthy responses. ExtractHexBytes
    /// now also strips a leading 11-bit/29-bit CAN-ID header before the hex checks, and
    /// QueryAsync no longer pre-filters lines with a separate (and stricter) regex.
    /// </summary>
    [Fact]
    public async Task ReadPidAsync_AfterWhitelistedHeadersOn_ParsesHealthyResponse()
    {
        var transport = NewInitializedScript();
        transport.OnCommand("ATH1", "OK\r\r>");
        // Real ELM327 response for RPM (010C) with CAN headers on: 26 (0x1A) * 256 + 248 (0xF8)
        // all divided by 4 = 1726 RPM - a perfectly valid, healthy reading.
        transport.OnCommand("010C", "7E8 41 0C 1A F8\r\r>");

        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        // ATH1 is on the whitelist, so this must not throw.
        await client.SendRawAsync("ATH1");

        // Fixed: the CAN-header-prefixed response now parses correctly.
        var rpm = await client.ReadPidAsync(StandardPids.Rpm);
        Assert.Equal(1726, rpm);
    }

    /// <summary>
    /// FIXED (Elm327Client.cs, GetSupportedPidsAsync): the loop's own comment says "Last bit
    /// of the mask indicates whether the next range exists", and it does check
    /// <c>(mask[3] &amp; 0x01) == 0</c> for that - but that condition used to be OR'd with a
    /// hard <c>range >= 0xC0</c> cutoff that fired unconditionally once the 0xC0 range (PIDs
    /// 0xC1-0xE0) had been queried, regardless of what the mask's own continuation bit said.
    /// A real vehicle that supports PIDs beyond 0xE0 (SAE J1979 defines range queries up to
    /// 0xE0, whose mask covers PIDs 0xE1-0x100) sets that continuation bit on its 0x01C0
    /// response precisely to tell the scanner to go on and query "01E0" - the cutoff is now
    /// 0xE0 instead of 0xC0, so that last legal range query still gets issued (while still
    /// bounding the loop - the SAE spec defines nothing past 0xE0).
    /// </summary>
    [Fact]
    public async Task GetSupportedPidsAsync_HonorsContinuationBitUpToRange0xE0()
    {
        var transport = NewInitializedScript();
        // Ranges 0x00..0xA0: no PIDs supported in-range, but each mask sets bit0 (D0) to say
        // "the next range's query PID is supported" so the scan keeps going.
        foreach (var range in new[] { 0x00, 0x20, 0x40, 0x60, 0x80, 0xA0 })
            transport.OnCommand($"01{range:X2}", $"41 {range:X2} 00 00 00 01\r\r>");

        // Range 0xC0: reports PID 0xC5 supported (bit 27) AND sets the continuation bit (D0)
        // to say PIDs 0xE1-0x100 should also be probed via "01E0".
        transport.OnCommand("01C0", "41 C0 08 00 00 01\r\r>");

        // Range 0xE0: reports PID 0xE5 as supported, and does NOT set the continuation bit
        // (there's nothing past 0xE0 per SAE J1979), so the scan correctly stops here.
        transport.OnCommand("01E0", "41 E0 08 00 00 00\r\r>");

        await using var client = new Elm327Client(transport);
        await client.InitializeAsync();

        var supported = await client.GetSupportedPidsAsync();

        Assert.Contains((byte)0xC5, supported); // found via the 0xC0 mask itself.

        // Fixed: "01E0" is now sent, per the 0xC0 mask's continuation bit.
        Assert.Contains("01E0", transport.SentCommands);
        Assert.Contains((byte)0xE5, supported);
    }
}
