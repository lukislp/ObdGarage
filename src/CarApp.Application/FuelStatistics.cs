using CarApp.Core;

namespace CarApp.Application;

/// <summary>Verbrauchs- und Kostenauswertung (Volltankmethode). Reine Rechenlogik.</summary>
public static class FuelStatistics
{
    /// <summary>
    /// Durchschnittsverbrauch l/100km über alle Volltank-Intervalle.
    /// Braucht mindestens zwei Volltank-Einträge mit km-Stand.
    /// </summary>
    public static double? ConsumptionPer100Km(IReadOnlyList<FuelEntry> entries)
    {
        var fullTanks = entries
            .Where(e => e.FullTank && e.OdometerKm is not null)
            .OrderBy(e => e.OdometerKm)
            .ToList();
        if (fullTanks.Count < 2)
            return null;

        var firstKm = fullTanks[0].OdometerKm!.Value;
        var lastKm = fullTanks[^1].OdometerKm!.Value;
        var distance = lastKm - firstKm;
        if (distance <= 0)
            return null;

        // Alle Liter NACH dem ersten Volltank bis einschließlich zum letzten Volltank.
        var liters = entries
            .Where(e => e.OdometerKm is { } km && km > firstKm && km <= lastKm)
            .Sum(e => e.Liters);

        return liters / distance * 100.0;
    }

    public static decimal TotalCost(IEnumerable<FuelEntry> fuel, IEnumerable<Expense> expenses) =>
        fuel.Sum(f => f.TotalPrice) + expenses.Sum(e => e.Amount);

    public static decimal? CostPerKm(IReadOnlyList<FuelEntry> fuel, IReadOnlyList<Expense> expenses)
    {
        var withKm = fuel.Where(f => f.OdometerKm is not null).OrderBy(f => f.OdometerKm).ToList();
        if (withKm.Count < 2)
            return null;
        var distance = withKm[^1].OdometerKm!.Value - withKm[0].OdometerKm!.Value;
        if (distance <= 0)
            return null;
        return TotalCost(fuel, expenses) / (decimal)distance;
    }
}
