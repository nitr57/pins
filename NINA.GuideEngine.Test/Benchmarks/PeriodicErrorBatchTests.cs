// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NUnit.Framework;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Benchmarks;

/// <summary>
/// Benchmark rather than a check (the table in docs/ALGORITHMS.md, "Periodic-error prediction"): periodic-error
/// prediction on ÷ off, true RA RMS in minutes 45–75 (a detected period needs about 40 minutes), over 6 seeds of the
/// mount, the seeing and the camera noise, for the presets and a 180-tooth worm. About 15 minutes per case; run by name.
/// </summary>
[TestFixture]
[Explicit("benchmark, about 15 minutes per case")]
[NonParallelizable]
[Category("Benchmark")]
public class PeriodicErrorBatchTests
{
    [TestCase("GoodMount")]
    [TestCase("HighSeeing")]
    [TestCase("Backlash")]
    [TestCase("PoorPeriodicError")]
    [TestCase("Worm180")]
    public async Task On_versus_off_over_6_seeds(string name)
    {
        var scenario = name switch
        {
            "GoodMount" => SimulatorScenario.GoodMount,
            "HighSeeing" => SimulatorScenario.HighSeeing,
            "Backlash" => SimulatorScenario.Backlash,
            "PoorPeriodicError" => SimulatorScenario.PoorPeriodicError,
            _ => PeriodicErrorHarness.Worm(),
        };
        var ratios = new List<double>();
        var on = new List<double>();
        for (int seed = 1; seed <= 6; seed++)
        {
            var s = scenario with
            {
                Mount = scenario.Mount with { Seed = seed },
                Sky = scenario.Sky with { SeeingSeed = 7 + seed },
                Camera = scenario.Camera with { NoiseSeed = 99 + seed },
            };
            var runOff = await PeriodicErrorHarness.Guide(s, periodicError: false, TimeSpan.FromMinutes(75));
            var runOn = await PeriodicErrorHarness.Guide(s, periodicError: true, TimeSpan.FromMinutes(75));
            double a = runOff.RaRms(45, 75), c = runOn.RaRms(45, 75);
            ratios.Add(c / a);
            on.Add(c);
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name} seed {seed}: off {a:F3}″, on {c:F3}″ ({c / a:F3}×), predicting {runOn.PredictingFraction(45, 75):P0}; {runOn.Final}"));
        }

        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: on ÷ off mean {ratios.Average():F3}×, worst {ratios.Max():F3}×; RA RMS with it {on.Min():F2}–{on.Max():F2}″"));
    }
}
