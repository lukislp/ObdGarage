namespace CarApp.Core;

public enum SyncState
{
    Synced = 0,
    Pending = 1,
}

public enum OdometerSource
{
    Manual = 0,
    ObdStandardPid = 1,
    Estimated = 2,
}

public enum AdapterTransportType
{
    BluetoothClassic = 0,
    Ble = 1,
    WifiTcp = 2,
    Simulator = 3,
}

public enum TripCategory
{
    Private = 0,
    Business = 1,
    Commute = 2,
}

public enum MaintenanceType
{
    OilChange = 0,
    Inspection = 1,          // TÜV/HU
    Service = 2,
    TireChange = 3,
    Custom = 99,
}

/// <summary>
/// Basisklasse aller synchronisierbaren Entitäten (offline-first, siehe Plan 2.3).
/// IDs werden clientseitig erzeugt (sync-freundlich), Zeitstempel sind UTC.
/// </summary>
public abstract class SyncEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;

    public void Touch()
    {
        ModifiedAt = DateTimeOffset.UtcNow;
        SyncState = SyncState.Pending;
    }
}

/// <summary>Fahrzeug — gehört genau einem Nutzer (kein Teilen).</summary>
public class Vehicle : SyncEntity
{
    public Guid OwnerUserId { get; set; }
    public required string Name { get; set; }
    public string? Vin { get; set; }
    public string? LicensePlate { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? ModelYear { get; set; }
    public string? PhotoPath { get; set; }
    /// <summary>Nächster TÜV/HU-Termin — Komfortfeld für die Fahrzeugkarte.</summary>
    public DateOnly? InspectionDueDate { get; set; }
    public OdometerSource OdometerSource { get; set; } = OdometerSource.Manual;
    public double? LastKnownOdometerKm { get; set; }
    /// <summary>Vom Fahrzeug unterstützte Mode-01-PIDs (einmalig gescannt).</summary>
    public List<byte> SupportedPids { get; set; } = [];
}

public class AdapterProfile : SyncEntity
{
    public Guid? VehicleId { get; set; }
    public required string Name { get; set; }
    public AdapterTransportType TransportType { get; set; }
    /// <summary>MAC-Adresse (BT), Geräte-UUID (BLE) oder "host:port" (WLAN).</summary>
    public required string Address { get; set; }
}

public class OdometerReading : SyncEntity
{
    public Guid VehicleId { get; set; }
    public double ValueKm { get; set; }
    public OdometerSource Source { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public class Trip : SyncEntity
{
    public Guid VehicleId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double? StartKm { get; set; }
    public double? EndKm { get; set; }
    public double DistanceKm { get; set; }
    public TripCategory Category { get; set; } = TripCategory.Private;
    public string? Note { get; set; }
}

/// <summary>
/// Historisierter Livewert: JEDER gepollte OBD-Wert wird gespeichert (Long-Format),
/// damit Verläufe angeschaut werden können — auch außerhalb von Fahrten.
/// Append-only; alte Rohdaten werden per Retention zu Minuten-Aggregaten verdichtet.
/// </summary>
public class ObdSample : SyncEntity
{
    public Guid VehicleId { get; set; }
    public Guid? TripId { get; set; }
    /// <summary>Wert-Schlüssel aus der PID-Registry, z.B. "rpm", "coolant_temp".</summary>
    public required string PidKey { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
    /// <summary>False = Rohwert; True = Minuten-Aggregat nach Retention-Verdichtung.</summary>
    public bool IsAggregated { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
}

public class MaintenanceTask : SyncEntity
{
    public Guid VehicleId { get; set; }
    public MaintenanceType Type { get; set; }
    public required string Title { get; set; }
    public int? IntervalKm { get; set; }
    public int? IntervalMonths { get; set; }
    public double? LastDoneAtKm { get; set; }
    public DateOnly? LastDoneOn { get; set; }
    public DateOnly? FixedDueDate { get; set; }
}

public class FuelEntry : SyncEntity
{
    public Guid VehicleId { get; set; }
    public DateOnly Date { get; set; }
    public double Liters { get; set; }
    public decimal TotalPrice { get; set; }
    public double? OdometerKm { get; set; }
    public bool FullTank { get; set; } = true;
}

public class Expense : SyncEntity
{
    public Guid VehicleId { get; set; }
    public DateOnly Date { get; set; }
    public required string Category { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}
