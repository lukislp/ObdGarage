using CarApp.Core;

namespace CarApp.Application;

/// <summary>Consumption and cost analysis (full-tank method). Pure calculation logic.</summary>
public static class FuelStatistics
{
    /// <summary>
    /// Average consumption l/100km across all full-tank intervals.
    /// Requires at least two full-tank entries with an odometer reading.
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

        // All liters AFTER the first full tank up to and including the last full tank.
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

        // Bound both fuel and expenses to the same date window the distance was measured
        // over - otherwise a cost entry from outside this stretch of driving (or a fuel entry
        // with no odometer reading at all) dominates the per-km figure instead of being
        // excluded the same way the distance calculation already excludes it.
        var from = withKm[0].Date;
        var to = withKm[^1].Date;
        var windowedFuel = fuel.Where(f => f.Date >= from && f.Date <= to);
        var windowedExpenses = expenses.Where(e => e.Date >= from && e.Date <= to);
        return TotalCost(windowedFuel, windowedExpenses) / (decimal)distance;
    }
}
