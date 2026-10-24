// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;

namespace NINA.GuideEngine.Test.TestSupport;

/// <summary>
/// Wall-clock limit of a closed-loop run in virtual time, scaled to the simulated duration. The simulator needs about 1 s of
/// wall time per simulated minute on an x86 desktop; the guard allows 15 times that plus a minute, so slower CI and ARM
/// hosts don't fail while a loop that hangs still does.
/// </summary>
internal static class WallClockGuard
{
    /// <summary>The wall time allowed for a run of <paramref name="simulated"/> virtual time.</summary>
    public static TimeSpan For(TimeSpan simulated) => TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(15 * Math.Max(0, simulated.TotalMinutes));

    /// <summary>Awaits <paramref name="run"/>; fails the test when it takes longer than <see cref="For"/> of <paramref name="simulated"/>.</summary>
    public static async Task Within(this Task run, TimeSpan simulated)
    {
        (await Task.WhenAny(run, Task.Delay(For(simulated)))).Should().BeSameAs(run, "the virtual-time run must finish in reasonable wall time");
        await run;
    }
}
