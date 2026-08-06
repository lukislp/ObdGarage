using System.Text.Json;
using System.Text.RegularExpressions;
using CarApp.Core;
using CarApp.Data;
using CarApp.Web.Services;

namespace CarApp.Tests;

/// <summary>
/// Regression tests for bugs found in the Blazor Server web UI layer
/// (src/CarApp.Web/Services/AppState.cs, Fmt.cs, SvgChart.cs), all fixed.
/// </summary>
public class AppStateBugTests
{
    /// <summary>
    /// FIXED (Program.cs: AppState/SyncManager are now <c>AddScoped</c>, not
    /// <c>AddSingleton</c>): previously every browser tab/circuit connected to this
    /// self-hosted, multi-user instance shared the exact same AppState object, so one person
    /// logging in on one tab silently switched every OTHER already-open tab's identity too -
    /// including which account newly-created vehicles got attributed to (Home.razor filters
    /// by <c>State.CurrentUserId</c>, VehicleForm.razor stamps new vehicles with it). Scoped
    /// registration gives each circuit its own independent AppState/SyncManager instance, so
    /// an already-open circuit is no longer affected by a login/logout happening elsewhere. A
    /// brand-new circuit still conveniently resumes whichever account was last persisted to
    /// sync-auth.json (via MainLayout's SyncManager pre-warm) - only already-open sessions are
    /// isolated, matching the previous single-user convenience for a fresh tab.
    /// </summary>
    [Fact]
    public async Task ScopedAppState_AlreadyOpenCircuitIsUnaffectedByLoginPersistedElsewhere()
    {
        var dir = Path.Combine(Path.GetTempPath(), "carapp-appstate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vehicles = new JsonFileRepository<Vehicle>(dir);
            var localVehicle = new Vehicle { Name = "Familienkombi", OwnerUserId = AppState.LocalUserId };
            await vehicles.UpsertAsync(localVehicle);

            // --- "Circuit A": already open before anyone has ever logged in. Program.cs
            // registers AppState as Scoped, so this gets its own independent instance. ---
            var stateA = new AppState();
            Assert.Equal(AppState.LocalUserId, stateA.CurrentUserId);

            // --- Someone logs in via a DIFFERENT circuit ("Browser B"), persisting to the
            // shared sync-auth.json exactly like a real SyncManager.LoginAsync would. ---
            var userA = Guid.NewGuid();
            File.WriteAllText(Path.Combine(dir, "sync-auth.json"), JsonSerializer.Serialize(new
            {
                ServerUrl = "http://localhost:5299",
                Email = "user-a@example.com",
                Token = "session-token",
                UserId = userA,
            }));

            // Fixed: circuit A's own, already-constructed AppState is completely unaffected -
            // with the old singleton registration this would have flipped along with it.
            Assert.Equal(AppState.LocalUserId, stateA.CurrentUserId);
            var visibleToCircuitA = (await vehicles.GetAllAsync())
                .Where(v => v.OwnerUserId == stateA.CurrentUserId)
                .ToList();
            Assert.Single(visibleToCircuitA);
            Assert.Equal(localVehicle.Id, visibleToCircuitA[0].Id);

            // A brand-new circuit opened AFTER the login still conveniently resumes it (its own
            // SyncManager's constructor LoadAuth()s from the same file) - only already-open
            // sessions are isolated, not brand-new ones.
            var stateC = new AppState();
            _ = new SyncManager(
                vehicles,
                new JsonFileRepository<AdapterProfile>(dir),
                new JsonFileRepository<OdometerReading>(dir),
                new JsonFileRepository<Trip>(dir),
                new JsonFileRepository<MaintenanceTask>(dir),
                new JsonFileRepository<FuelEntry>(dir),
                new JsonFileRepository<Expense>(dir),
                new JsonlObdSampleStore(dir),
                new FakeClock2(DateTimeOffset.UtcNow),
                stateC,
                dir);
            Assert.Equal(userA, stateC.CurrentUserId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

public class FmtBugTests
{
    /// <summary>
    /// FIXED (Fmt.cs, Duration): the method used to assume a non-negative TimeSpan and format
    /// span.Minutes/span.Seconds directly. Those TimeSpan components carry the SAME SIGN as a
    /// negative span (e.g. TimeSpan.FromSeconds(-90) has Minutes == -1 and Seconds == -30, not
    /// a "wrapped" positive value), so a negative duration used to render as a confusing
    /// double-negative string. Reachable wherever Fmt.Duration is called with a trip's
    /// (EndedAt - StartedAt) (FahrtenTab.razor) if EndedAt ever precedes StartedAt - e.g. a
    /// synced trip record whose EndedAt/StartedAt were merged from conflicting last-write-wins
    /// updates, or a clock adjustment on the phone/dongle mid-trip. A duration is conceptually
    /// never negative, so Duration now clamps to TimeSpan.Zero first.
    /// </summary>
    [Fact]
    public void Duration_NegativeTimeSpan_ClampsToZero()
    {
        var negative = TimeSpan.FromSeconds(-90); // -1 min 30 s

        var text = Fmt.Duration(negative);

        Assert.Equal("0 min 00 s", text);
    }
}

public class SvgChartBugTests
{
    private static ObdSample Sample(DateTimeOffset t, double v) => new()
    {
        PidKey = "rpm",
        Timestamp = t,
        Value = v,
    };

    /// <summary>
    /// FIXED (SvgChart.cs Render, x-axis label loop): with exactly one sample, tMin == tMax,
    /// so spanTicks used to be clamped to the Math.Max(1, ...) fallback of a single tick
    /// (100ns), and the 5-mark loop's INTEGER division (`spanTicks * i / xLabels`) truncated
    /// to 0 ticks for i = 0..3, collapsing 4 of the 5 x-axis time labels onto the exact same
    /// x position and completely overlapping/illegible text - concretely triggerable any time
    /// a user opens the Verlauf tab right after a vehicle's first poll, or picks a custom time
    /// window narrow enough to capture just one recorded sample. Render now special-cases a
    /// single sample: exactly one, centered label instead of 5 overlapping ones.
    /// </summary>
    [Fact]
    public void Render_SingleSample_RendersOneCenteredXAxisLabel()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new List<ObdSample> { Sample(now, 2500) };

        var svg = SvgChart.Render(samples, "U/min");

        // x-axis labels are the <text class="chart-label"> elements pinned to the bottom of the
        // chart (y = Height - PadBottom + 18 = 320 - 30 + 18 = 308); the y-axis value labels and
        // the unit label use different y coordinates, so this pattern isolates just the x-axis
        // time mark(s).
        var xAxisLabelXs = Regex.Matches(svg, "<text class=\"chart-label\" x=\"([\\d.]+)\" y=\"308\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Fixed: one single, centered label instead of 5 overlapping ones. Chart usable width
        // is [PadLeft=56, Width-PadRight=846], so the center is (56+846)/2 = 451.
        Assert.Equal(["451"], xAxisLabelXs);
    }
}
