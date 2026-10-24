// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.Guiding;
using static NINA.GuideEngine.Test.Guiding.DecDriftGuidingTests;

namespace NINA.GuideEngine.Test.Benchmarks;

/// <summary>Dec guide mode Drift against Auto and South in the closed-loop simulator, and what it sees per case: numbers, no checks.</summary>
[TestFixture]
[NonParallelizable]
[Category("Benchmark")]
public class DecDriftBenchmarks
{
    /// <summary>
    /// The comparison in docs/ALGORITHMS.md ("Dec guide mode Drift"): Auto, South and Drift with Resist Switch and Predictive on Dec, three skies
    /// each: the true Dec RMS after the first 5 minutes (mean and per sky), the Dec reversals and when Drift picked a direction.
    /// </summary>
    [Explicit("slow: 10-20 minutes per case")]
    [TestCaseSource(typeof(DecDriftGuidingTests), nameof(DecDriftGuidingTests.Cases))]
    public async Task Compare(string name)
    {
        foreach (var dec in new[] { ResistSwitch, Predictive })
        {
            foreach (var mode in new[] { DecGuideMode.Auto, DecGuideMode.South, DecGuideMode.Drift })
            {
                var runs = new List<DecRun>();
                for (int seed = 1; seed <= 3; seed++)
                {
                    var (scenario, minutes, flip) = Case(name, seed);
                    runs.Add(await Guide(scenario, mode, dec, TimeSpan.FromMinutes(minutes), flipAfter: flip is { } f ? TimeSpan.FromMinutes(f) : null));
                }

                var picks = runs.Where(r => r.FirstPickMin is not null).Select(r => r.FirstPickMin!.Value).ToList();
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name,-10} {dec.Kind,-12} {mode,-5}: Dec RMS {runs.Average(r => r.DecRms):F3}″ ({string.Join(" / ", runs.Select(r => r.DecRms.ToString("F3", CultureInfo.InvariantCulture)))}), reversals {runs.Average(r => r.Reversals):F0}/{runs.Average(r => r.DecPulses):F0} pulses, first pick {(picks.Count > 0 ? $"{picks.Average():F1} min" : "-")} ({picks.Count}/3), switches {runs.Average(r => r.Switches):F1}, valve {runs.Sum(r => r.ValveOpenings)}; #1 {runs[0].Timeline}"));
            }
        }
    }

    /// <summary>What Drift sees in one case, minute by minute: the true drift, the estimate, the dead band and the direction.</summary>
    [Explicit("diagnostics, slow")]
    [TestCaseSource(typeof(DecDriftGuidingTests), nameof(DecDriftGuidingTests.Cases))]
    public async Task Estimates(string name)
    {
        foreach (var dec in new[] { ResistSwitch, Predictive })
        {
            var (scenario, minutes, flip) = Case(name, 1);
            TestContext.Out.WriteLine($"--- {name} {dec.Kind}");
            int frames = 0;
            VirtualClock? clock = null;
            var run = await Guide(scenario, DecGuideMode.Drift, dec, TimeSpan.FromMinutes(minutes), flipAfter: flip is { } f ? TimeSpan.FromMinutes(f) : null,
                attach: (_, c, _) => clock = c,
                onEvent: (g, e) =>
                {
                    if (e is GuideStepEvent { IsSettling: false } s && ++frames % 30 == 0)
                    {
                        double elapsed = clock!.Elapsed.TotalSeconds;
                        double truth = scenario.Mount.DecDriftArcsecPerMin + scenario.Mount.DecDriftChangePerHour * elapsed / 3600;
                        double scale = s.PixelScale * 60;
                        var ol = g.OpenLoopDecDrift();
                        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{elapsed / 60,5:F1} min truth {truth,6:F2}″/min | estimate {(ol is { } o ? $"{o.PxPerSec * scale,6:F2} ± {o.SigmaPxPerSec * scale:F2} ({o.Sigmas,4:F1} σ)" : "-"),-24} | wander {Math.Sqrt(g.DecWanderRate() * 60) * s.PixelScale:F3}″/√min | dead band {g.DecBacklashPx():0.#} px | {g.DecDirection?.Direction}{(g.DecDirection?.ValveOpen == true ? " valve" : "")}{(g.DecAlgorithm is PredictiveAlgorithm p ? $" | Predictive drift {p.State.DriftPxPerSec * scale:F2}, seeing {p.State.SeeingPx:F3} px" : "")}"));
                    }
                });
            TestContext.Out.WriteLine($"{run}");
            foreach (var n in run.Notes.Where(n => n.Kind is not DecDirectionNoteKind.Summary))
            {
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   {(n.Timestamp - run.Steps[0].Timestamp).TotalMinutes:F1} min {n.Kind}: {n.Message}"));
            }
        }
    }
}
