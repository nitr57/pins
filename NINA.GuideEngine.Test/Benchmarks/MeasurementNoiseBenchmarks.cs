// SPDX-License-Identifier: MPL-2.0

using NUnit.Framework;
using NINA.GuideEngine.Test.Guiding;
using static NINA.GuideEngine.Test.Guiding.MeasurementNoiseGuidingTests;

namespace NINA.GuideEngine.Test.Benchmarks;

/// <summary>Predictive with and without the per-frame measurement uncertainty on a faint field under clouds: numbers, no checks.</summary>
[TestFixture]
[NonParallelizable]
[Category("Benchmark")]
public class MeasurementNoiseBenchmarks
{
    // Numbers for docs/ALGORITHMS.md ("Measurement uncertainty"): paired seeds with and without the frame weighting from
    // minute 6 on (after the learning): true RMS, the seeing learned and the model in use (wander/seeing ratio), under the
    // clouds and in clear sky
    [Explicit("benchmark: 12 guided runs of 40 min")]
    [TestCase(Clouds.None)]
    [TestCase(Clouds.Short)]
    [TestCase(Clouds.Long)]
    public async Task Benchmark(Clouds clouds)
    {
        const int seeds = 6;
        var rows = new List<(Summary On, Summary Off)>();
        for (int seed = 1; seed <= seeds; seed++)
        {
            var scenario = FaintField(seed, clouds);
            var runs = await Task.WhenAll(Guide(scenario, true, TimeSpan.FromMinutes(40)), Guide(scenario, false, TimeSpan.FromMinutes(40)));
            var (on, off) = (Summarize(runs[0]), Summarize(runs[1]));
            rows.Add((on, off));
            TestContext.Out.WriteLine($"{scenario.Name}: with frame weighting {on}; without {off}");
        }

        static string Ratio(IReadOnlyList<double> r)
        {
            double mean = r.Average();
            double se = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Count - 1) / r.Count);
            return $"{mean:F3} ± {se:F3} (better in {r.Count(x => x < 1)}/{r.Count})";
        }

        List<double> Of(Func<Summary, double> get) => rows.Select(r => get(r.On) / get(r.Off)).Where(double.IsFinite).ToList();
        TestContext.Out.WriteLine($"{clouds}: true RMS with frame weighting ÷ without {Ratio(Of(s => s.Rms))}" +
            (clouds == Clouds.None ? "" : $", under clouds {Ratio(Of(s => s.RmsDim))}, clear {Ratio(Of(s => s.RmsClear))}"));
        foreach (var (label, pick) in new (string, Func<(Summary On, Summary Off), Summary>)[] { ("with frame weighting", r => r.On), ("without", r => r.Off) })
        {
            var s = rows.Select(pick).ToList();
            TestContext.Out.WriteLine($"{clouds} {label}: seeing σ {s.Average(x => x.SeeingClear):F3} px clear, {s.Average(x => x.SeeingDim):F3} px under clouds; " +
                $"log10 wander ratio {s.Average(x => x.LogWanderClear):F2} clear, {s.Average(x => x.LogWanderDim):F2} under clouds; {s.Average(x => x.Takeovers):F1} takeovers; " +
                $"frames trusted less {s.Average(x => x.Noisy):P1}, clear frames not usual {s.Average(x => x.ClearNotUsual):P1}");
        }
    }
}
