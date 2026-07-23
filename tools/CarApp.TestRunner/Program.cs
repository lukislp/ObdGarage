// Paketfreier Test-Runner (NuGet ist in der Cloud-Sandbox blockiert).
// Führt dieselben Fälle aus wie tests/CarApp.Tests — dort als xunit für `dotnet test` zuhause.
using CarApp.Obd;
using CarApp.Obd.Pids;
using CarApp.Obd.Transport;

var failures = 0;
var passed = 0;

void Check(string name, bool condition)
{
    if (condition) { passed++; Console.WriteLine($"  OK   {name}"); }
    else { failures++; Console.WriteLine($"  FAIL {name}"); }
}

void CheckEqual(string name, double expected, double actual, double tol = 1e-9) =>
    Check($"{name} (erwartet {expected}, war {actual})", Math.Abs(expected - actual) <= tol);

static ReplayTransport NewScript()
{
    var t = new ReplayTransport();
    t.OnCommand("ATZ", "\r\rELM327 v1.5\r\r>");
    foreach (var c in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0" })
        t.OnCommand(c, "OK\r\r>");
    return t;
}

Console.WriteLine("PID-Dekodierung:");
CheckEqual("RPM 1A F8", 1726.0, StandardPids.Rpm.Decode([0x1A, 0xF8]));
CheckEqual("Speed 4B", 75.0, StandardPids.Speed.Decode([0x4B]));
CheckEqual("Coolant 7B", 83.0, StandardPids.CoolantTemp.Decode([0x7B]));
CheckEqual("Voltage 33 90", 13.2, StandardPids.ControlModuleVoltage.Decode([0x33, 0x90]), 1e-3);
CheckEqual("MAF 04 D2", 12.34, StandardPids.Maf.Decode([0x04, 0xD2]), 1e-3);
CheckEqual("Load FF", 100.0, StandardPids.EngineLoad.Decode([0xFF]), 1e-3);
CheckEqual("Odometer A6", 123456.7, StandardPids.Odometer.Decode([0x00, 0x12, 0xD6, 0x87]), 1e-3);

var mask = StandardPids.DecodeSupportedPidMask(0x00, [0xBE, 0x1F, 0xA8, 0x13]).ToHashSet();
Check("Supported-Maske enthält 0C/0D/11/20", mask.Contains(0x0C) && mask.Contains(0x0D) && mask.Contains(0x11) && mask.Contains(0x20));
Check("Supported-Maske ohne 02/08", !mask.Contains(0x02) && !mask.Contains(0x08));

Console.WriteLine("Whitelist:");
foreach (var ok in new[] { "010C", "0100", "01A6", "0902", "03", "ATZ", "ATE0", "ATSP0", "ATRV" })
    Check($"erlaubt: {ok}", Elm327Client.IsCommandAllowed(ok));
foreach (var bad in new[] { "04", "2E1234FF", "ATSH7E0", "10", "3101FF00" })
    Check($"blockiert: {bad}", !Elm327Client.IsCommandAllowed(bad));

Console.WriteLine("Client-Integration (ReplayTransport):");
{
    var t = NewScript();
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    Check("Init-Sequenz", client.IsInitialized &&
        t.SentCommands.SequenceEqual(["ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"]));
}
{
    var t = NewScript().OnCommand("010C", "010C\rSEARCHING...\r41 0C 1A F8\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    CheckEqual("RPM über Client (mit Echo+SEARCHING)", 1726.0, await client.ReadPidAsync(StandardPids.Rpm));
}
{
    var t = NewScript().OnCommand("01A6", "41 A6 00 12 D6 87\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    CheckEqual("Odometer über Client", 123456.7, await client.ReadPidAsync(StandardPids.Odometer), 1e-3);
}
{
    var t = NewScript().OnCommand("0105", "NO DATA\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    try { await client.QueryAsync("0105"); Check("NO DATA wirft ObdErrorException", false); }
    catch (ObdErrorException) { Check("NO DATA wirft ObdErrorException", true); }
}
{
    var t = NewScript()
        .OnCommand("0100", "41 00 BE 1F A8 13\r\r>")
        .OnCommand("0120", "41 20 80 00 00 00\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    var s = await client.GetSupportedPidsAsync();
    Check("Supported-PID-Scan (0C, 0D, 21; kein 0140)",
        s.Contains(0x0C) && s.Contains(0x0D) && s.Contains(0x21) &&
        !s.Contains(0x02) && !t.SentCommands.Contains("0140"));
}
{
    var t = NewScript().OnCommand("0902",
        "014\r0: 49 02 01 57 30 4C\r1: 30 30 30 30 34 33 4D\r2: 45 35 34 33 32 31 39\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    Check("VIN (CAN-Multiframe)", await client.ReadVinAsync() == "W0L000043ME543219");
}
{
    var t = NewScript().OnCommand("0902",
        "49 02 01 00 00 00 57\r49 02 02 30 4C 30 30\r49 02 03 30 30 34 33\r49 02 04 4D 45 35 34\r49 02 05 33 32 31 39\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    Check("VIN (Legacy-Format)", await client.ReadVinAsync() == "W0L000043ME543219");
}
{
    var t = NewScript().OnCommand("ATRV", "12.6V\r\r>");
    await using var client = new Elm327Client(t);
    await client.InitializeAsync();
    var v = await client.ReadAdapterVoltageAsync();
    CheckEqual("ATRV Spannung", 12.6, v ?? double.NaN, 1e-3);
}
{
    await using var client = new Elm327Client(new ReplayTransport());
    try { await client.SendRawAsync("04"); Check("Blockierter Befehl wirft Exception", false); }
    catch (ObdCommandBlockedException) { Check("Blockierter Befehl wirft Exception", true); }
}

await CarApp.TestRunner.E2ETests.RunAsync(Check, (n, e, a, t) => CheckEqual(n, e, a, t));
await CarApp.TestRunner.SyncTests.RunAsync(Check);

Console.WriteLine($"\n{passed} bestanden, {failures} fehlgeschlagen.");
return failures == 0 ? 0 : 1;
