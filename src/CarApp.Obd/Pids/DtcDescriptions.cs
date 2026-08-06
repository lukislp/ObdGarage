namespace CarApp.Obd.Pids;

/// <summary>
/// Plain-text descriptions for the generic (SAE-defined, not manufacturer-specific) DTCs a
/// typical passenger vehicle actually reports in practice. Not exhaustive - the full generic
/// set runs into the hundreds and manufacturer-specific codes are effectively unbounded - so an
/// unknown code still displays (just without a description) rather than being hidden.
/// </summary>
public static class DtcDescriptions
{
    private static readonly Dictionary<string, string> Map = new()
    {
        // --- Misfires ---
        ["P0300"] = "Zufällige/mehrere Zylinder-Fehlzündungen",
        ["P0301"] = "Fehlzündung Zylinder 1",
        ["P0302"] = "Fehlzündung Zylinder 2",
        ["P0303"] = "Fehlzündung Zylinder 3",
        ["P0304"] = "Fehlzündung Zylinder 4",
        ["P0305"] = "Fehlzündung Zylinder 5",
        ["P0306"] = "Fehlzündung Zylinder 6",
        ["P0307"] = "Fehlzündung Zylinder 7",
        ["P0308"] = "Fehlzündung Zylinder 8",

        // --- Fuel/air metering ---
        ["P0100"] = "Luftmassenmesser: Schaltkreis/Bereich",
        ["P0101"] = "Luftmassenmesser: Bereich/Plausibilität",
        ["P0110"] = "Ansauglufttemperatur: Schaltkreis",
        ["P0113"] = "Ansauglufttemperatur: Signal zu hoch",
        ["P0116"] = "Kühlmitteltemperatur: Bereich/Plausibilität",
        ["P0117"] = "Kühlmitteltemperatur: Signal zu niedrig",
        ["P0118"] = "Kühlmitteltemperatur: Signal zu hoch",
        ["P0120"] = "Drosselklappensensor: Schaltkreis",
        ["P0128"] = "Kühlmittel-Thermostat (Temperatur unter Regelwert)",
        ["P0171"] = "Gemisch zu mager (Bank 1)",
        ["P0172"] = "Gemisch zu fett (Bank 1)",
        ["P0174"] = "Gemisch zu mager (Bank 2)",
        ["P0175"] = "Gemisch zu fett (Bank 2)",

        // --- O2 sensors / catalyst / EVAP ---
        ["P0130"] = "Lambdasonde Bank 1 Sensor 1: Schaltkreis",
        ["P0133"] = "Lambdasonde Bank 1 Sensor 1: Trägeres Ansprechverhalten",
        ["P0135"] = "Lambdasonde Bank 1 Sensor 1: Heizung defekt",
        ["P0138"] = "Lambdasonde Bank 1 Sensor 2: Spannung zu hoch",
        ["P0141"] = "Lambdasonde Bank 1 Sensor 2: Heizung defekt",
        ["P0420"] = "Katalysator-Wirkungsgrad unter Schwellenwert (Bank 1)",
        ["P0430"] = "Katalysator-Wirkungsgrad unter Schwellenwert (Bank 2)",
        ["P0440"] = "Tankentlüftungssystem (EVAP): allgemeiner Fehler",
        ["P0442"] = "Tankentlüftungssystem: kleines Leck erkannt",
        ["P0446"] = "Tankentlüftungssystem: Lüftungssteuerung",
        ["P0455"] = "Tankentlüftungssystem: großes Leck erkannt",

        // --- Ignition / misc powertrain ---
        ["P0325"] = "Klopfsensor: Schaltkreis (Bank 1)",
        ["P0335"] = "Kurbelwellensensor: Schaltkreis",
        ["P0340"] = "Nockenwellensensor: Schaltkreis",
        ["P0351"] = "Zündspule A: Primär-/Sekundärkreis",
        ["P0500"] = "Geschwindigkeitssensor: kein/fehlerhaftes Signal",
        ["P0505"] = "Leerlaufregelung: Schaltkreis",
        ["P0562"] = "Bordspannung zu niedrig",
        ["P0563"] = "Bordspannung zu hoch",
        ["P0601"] = "Steuergerät: interner Speicherfehler",
        ["P0700"] = "Getriebesteuerung: Fehler gemeldet (siehe Getriebe-DTCs)",

        // --- Chassis (ABS/ESP/brakes) ---
        ["C0035"] = "Raddrehzahlsensor vorne links: Schaltkreis",
        ["C0040"] = "Raddrehzahlsensor vorne rechts: Schaltkreis",
        ["C0045"] = "Raddrehzahlsensor hinten links: Schaltkreis",
        ["C0050"] = "Raddrehzahlsensor hinten rechts: Schaltkreis",
        ["C0110"] = "ABS-Pumpenmotor: Schaltkreis",
        ["C0161"] = "ESP-Schalter: Schaltkreis",

        // --- Body (airbag/climate/lighting) ---
        ["B0001"] = "Fahrer-Airbag: Auslöseschaltkreis",
        ["B0012"] = "Beifahrer-Airbag: Auslöseschaltkreis",
        ["B0051"] = "Gurtstraffer vorne links: Schaltkreis",
        ["B1318"] = "Batteriespannung: außerhalb Toleranz",
        ["B1342"] = "Steuergerät: interner Fehler",

        // --- Network (CAN bus / module communication) ---
        ["U0100"] = "Kommunikationsverlust mit Motorsteuergerät",
        ["U0101"] = "Kommunikationsverlust mit Getriebesteuergerät",
        ["U0121"] = "Kommunikationsverlust mit ABS/ESP-Steuergerät",
        ["U0140"] = "Kommunikationsverlust mit Karosseriesteuergerät",
        ["U0155"] = "Kommunikationsverlust mit Kombiinstrument",
    };

    /// <summary>German plain-text description, or null if this exact code isn't in the (deliberately non-exhaustive) table.</summary>
    public static string? Describe(string code) => Map.GetValueOrDefault(code.ToUpperInvariant());
}
