using CarApp.Core;

namespace CarApp.Application;

public enum DueBadge { Green, Yellow, Red }

/// <summary>Fälligkeitsstatus einer Wartungsaufgabe — Grundlage für Karte + Erinnerungen.</summary>
public sealed record MaintenanceStatus(
    MaintenanceTask Task,
    double? RemainingKm,
    int? RemainingDays,
    bool IsOverdue,
    DueBadge Badge);

/// <summary>Reine Rechenlogik, komplett UI- und IO-frei (voll testbar).</summary>
public static class MaintenanceCalculator
{
    public static MaintenanceStatus GetStatus(MaintenanceTask task, double? currentKm, DateOnly today)
    {
        double? remainingKm = null;
        if (task.IntervalKm is { } intervalKm && task.LastDoneAtKm is { } lastKm && currentKm is { } km)
            remainingKm = lastKm + intervalKm - km;

        int? remainingDays = null;
        DateOnly? dueDate = task.FixedDueDate;
        if (dueDate is null && task.IntervalMonths is { } months && task.LastDoneOn is { } lastOn)
            dueDate = lastOn.AddMonths(months);
        if (dueDate is { } due)
            remainingDays = due.DayNumber - today.DayNumber;

        var overdue = remainingKm is < 0 || remainingDays is < 0;
        return new MaintenanceStatus(task, remainingKm, remainingDays, overdue, GetBadge(remainingKm, remainingDays));
    }

    /// <summary>Die dringendste Aufgabe eines Fahrzeugs (für die Fahrzeugkarte).</summary>
    public static MaintenanceStatus? MostUrgent(
        IEnumerable<MaintenanceTask> tasks, double? currentKm, DateOnly today) =>
        tasks.Select(t => GetStatus(t, currentKm, today))
             .OrderBy(Urgency)
             .FirstOrDefault();

    public static DueBadge GetBadge(double? remainingKm, int? remainingDays)
    {
        if (remainingKm is < 0 || remainingDays is < 0) return DueBadge.Red;
        if (remainingKm is < 1000 || remainingDays is < 90) return DueBadge.Yellow;
        return DueBadge.Green;
    }

    private static double Urgency(MaintenanceStatus s)
    {
        // Normalisieren: Tage und km auf eine vergleichbare Skala bringen.
        var byDays = s.RemainingDays is { } d ? d : double.MaxValue;
        var byKm = s.RemainingKm is { } km ? km / 40.0 : double.MaxValue; // ~40 km/Tag
        return Math.Min(byDays, byKm);
    }
}
