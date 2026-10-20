// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class DecPlayProbeTests
{
    private const double CalibratedRate = 0.00127; // px/ms, the sky simulator's calibration of the 2026-10-18 guide log

    /// <summary>
    /// Runs a probe against a hard Dec play of <paramref name="playMs"/> motor time, the gear starting at
    /// <paramref name="gear"/> (-1 against the South side … 1 against the North side), the mount moving the star at
    /// <paramref name="trueRate"/> px/ms once the play is taken up.
    /// </summary>
    private static DecPlayProbe Run(double playMs, double trueRate, double gear, double noisePx = 0, int seed = 1)
    {
        var random = new Random(seed);
        var probe = new DecPlayProbe(CalibratedRate, 2500, PlayModelBacklash.SizesMs[^1]);
        double gap = gear * playMs / 2, star = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            double noise = noisePx * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            if (probe.Update(star + noise) is not { } pulse)
            {
                break;
            }

            double motor = (pulse.Direction == GuideDirection.North ? 1 : -1) * pulse.DurationMs;
            double before = gap;
            gap = Math.Clamp(gap + motor, -playMs / 2, playMs / 2);
            star += (motor - (gap - before)) * trueRate;
        }

        return probe;
    }

    [TestCase(1.0)]
    [TestCase(2.0)]
    [TestCase(0.6)]
    [TestCase(0.5)]
    [TestCase(0.3)]
    public void MeasuresThePlayWhenTheDecRateChangedSinceTheCalibration(double rateFactor)
    {
        // the sky simulator's profile: Dec pulse efficiency 0.5 on the East side at Dec 57.6, 1.0 on the West side. A
        // calibration from the East side read every take-up after a slew to the West side as "no play" (64 ms); one
        // from the West side never saw the star follow the North pulses after a slew to the East side
        const double playMs = 1100;
        var probe = Run(playMs, CalibratedRate * rateFactor, gear: -1);

        probe.Phase.Should().Be(DecPlayProbePhase.Measured);
        probe.RateMeasured.Should().BeTrue();
        probe.RatePxPerMs.Should().BeApproximately(CalibratedRate * rateFactor, 0.05 * CalibratedRate * rateFactor);

        var play = new PlayModelBacklash(500);
        play.SeedFromCalibration(probe.TakeUp, probe.RatePxPerMs).Should().BeTrue();
        play.PriorFullReversalMs.Should().BeApproximately(playMs, 0.25 * playMs);
    }

    [TestCase(1.0)]
    [TestCase(0.5)]
    [TestCase(2.0)]
    public void MeasuresThePlayThroughSeeing(double rateFactor)
    {
        const double playMs = 1100;
        var sizes = new List<double>();
        for (int seed = 1; seed <= 20; seed++)
        {
            var probe = Run(playMs, CalibratedRate * rateFactor, gear: seed % 3 - 1, noisePx: 0.15, seed);
            probe.Phase.Should().Be(DecPlayProbePhase.Measured);
            var play = new PlayModelBacklash(500);
            play.SeedFromCalibration(probe.TakeUp, probe.RatePxPerMs).Should().BeTrue();
            sizes.Add(play.PriorFullReversalMs);
        }

        sizes.Order().ElementAt(sizes.Count / 2).Should().BeApproximately(playMs, 0.25 * playMs);
    }
}
