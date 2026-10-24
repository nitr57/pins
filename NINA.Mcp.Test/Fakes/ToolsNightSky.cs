// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Test.Fakes;

/// <summary>
/// A synthetic night with analytic curves, so planner results can be checked by hand. h is hours since the start at
/// 2026-10-13 12:00 +02:00, sampled every 5 minutes (289 samples):
/// <list type="bullet">
/// <item>sun = 40 cos(2πh/24) − 10: 30° at noon, −50° at midnight; −18° at h = 6.769 (18:46) and 17.231 (05:14).</item>
/// <item>moon = 20 sin(2π(h − 15)/24): rises at h = 15 (03:00 next morning), up 20° at h = 21; separation 90° throughout.</item>
/// <item>target = 30 cos(2π(h − 12)/24) + 40: transit at midnight with 70°; above 30° for |h − 12| &lt; 7.298 (16:42 to 07:18).</item>
/// </list>
/// </summary>
internal static class ToolsNightSky {

    public static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(2));

    public const int Samples = 289;

    public static NightSky Create(IReadOnlyList<SkyTargetRequest> targets, IReadOnlyList<double?>? horizon = null) {
        var times = new List<DateTimeOffset>();
        var sun = new List<double>();
        var moon = new List<double>();

        for (var i = 0; i < Samples; i++) {
            var h = i * 5 / 60.0;

            times.Add(Start.AddMinutes(i * 5));
            sun.Add(40 * Math.Cos(2 * Math.PI * h / 24) - 10);
            moon.Add(20 * Math.Sin(2 * Math.PI * (h - 15) / 24));
        }

        var skyTargets = targets
            .Select(target => new TargetSky(
                target.Name,
                target.RaHours,
                target.DecDegrees,
                45,
                120,
                Enumerable.Range(0, Samples).Select(i => 30 * Math.Cos(2 * Math.PI * (i * 5 / 60.0 - 12) / 24) + 40).ToList(),
                Enumerable.Range(0, Samples).Select(i => (i * 360.0 / (Samples - 1)) % 360).ToList(),
                horizon ?? Array.Empty<double?>(),
                Enumerable.Repeat(90.0, Samples).ToList()))
            .ToList();

        return new NightSky(
            true,
            "Synthetic night",
            new ObservingSite(48.7, 8.1, 130, horizon is not null),
            Start.AddHours(9),
            times,
            sun,
            moon,
            0.42,
            "WaxingCrescent",
            skyTargets);
    }
}
