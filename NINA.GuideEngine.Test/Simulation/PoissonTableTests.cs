// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;
using static NINA.GuideEngine.Test.Simulation.SimTestHelpers;

namespace NINA.GuideEngine.Test.Simulation;

public class PoissonTableTests
{
    [TestCase(0.3)]
    [TestCase(2.0)]
    [TestCase(10.025)]
    [TestCase(19.9)]
    public void Sampling_is_the_inversion_of_the_poisson_distribution(double mean)
    {
        var table = new PoissonTable(mean);
        var cumulative = ReferenceCumulative(mean);
        var rng = new SimRng(17, (ulong)(mean * 1000));
        for (int i = 0; i < 200_000; i++)
        {
            ulong bits = rng.NextULong();
            double u = (bits >> 11) * (1.0 / (1UL << 53));
            int expected = Array.FindIndex(cumulative, c => c > u);
            table.Sample(bits).Should().Be(expected);
        }
    }

    [TestCase(0.3)]
    [TestCase(2.0)]
    [TestCase(10.025)]
    [TestCase(19.9)]
    public void Frequencies_mean_and_variance_match_the_poisson_distribution(double mean)
    {
        const int n = 1_000_000;
        var table = new PoissonTable(mean);
        var rng = new SimRng(23, (ulong)(mean * 1000));
        var counts = new int[table.MaxValue + 1];
        double sum = 0, sum2 = 0;
        for (int i = 0; i < n; i++)
        {
            int k = table.Sample(rng.NextULong());
            counts[k]++;
            sum += k;
            sum2 += (double)k * k;
        }

        double m = sum / n, v = sum2 / n - m * m;
        m.Should().BeApproximately(mean, 5 * Math.Sqrt(mean / n));
        v.Should().BeApproximately(mean, 5 * mean * Math.Sqrt(2.0 / n) + 5 * Math.Sqrt(mean / n));
        for (int k = 0; k < counts.Length; k++)
        {
            double expected = n * Math.Exp(LogPmf(mean, k));
            if (expected >= 20) counts[k].Should().BeCloseTo((int)Math.Round(expected), (uint)(5 * Math.Sqrt(expected) + 1), $"P(K = {k})");
        }
    }

    [Test]
    public void The_extreme_uniforms_stay_inside_the_table()
    {
        var table = new PoissonTable(10.0);
        table.Sample(0).Should().Be(0);
        table.Sample(ulong.MaxValue).Should().BeInRange(35, table.MaxValue, "1 − 2^−53 lies far out in the tail");
        table.MaxValue.Should().BeGreaterThan(40);
    }

    [TestCase(0.0)]
    [TestCase(-1.0)]
    [TestCase(double.NaN)]
    [TestCase(700.0)]
    public void Means_outside_the_range_are_refused(double mean)
    {
        var create = () => new PoissonTable(mean);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void A_faint_background_has_the_skew_of_photon_noise()
    {
        // 0.5 s at 20 e⁻/s: 10 e⁻ per pixel, below the 20 e⁻ where the camera switches to a normal approximation
        var cfg = new CameraSimConfig { HotPixelCount = 0, ColdPixelCount = 0, SkyBackgroundElectronsPerSec = 20, DarkCurrentElectronsPerSec = 0, ReadNoiseElectrons = 3.5 };
        var sim = new Simulator(Clean() with { Sky = new SkySimConfig { Stars = [] }, Camera = cfg });

        var frame = sim.Camera.Render(0, 500, 1, new IntRect(0, 0, 800, 800), 1);
        var r = frame.Subframe;
        double n = 0, sum = 0;
        for (int y = r.Top; y <= r.Bottom; y++)
        {
            for (int x = r.Left; x <= r.Right; x++)
            {
                sum += frame[x, y];
                n++;
            }
        }

        double mean = sum / n, m2 = 0, m3 = 0;
        for (int y = r.Top; y <= r.Bottom; y++)
        {
            for (int x = r.Left; x <= r.Right; x++)
            {
                double d = frame[x, y] - mean;
                m2 += d * d;
                m3 += d * d * d;
            }
        }

        // Poisson(10) + N(0, 3.5²) + rounding to whole ADU (variance 1/12): third central moment 10, a Gaussian has 0
        mean.Should().BeApproximately(210, 0.05);
        (m2 / n).Should().BeApproximately(10 + 3.5 * 3.5 + 1.0 / 12, 0.25);
        (m3 / n).Should().BeApproximately(10, 1.6);
    }

    // cumulative probabilities from the closed form, for a plain inversion: the smallest k whose entry exceeds u
    private static double[] ReferenceCumulative(double mean)
    {
        var c = new double[200];
        double sum = 0;
        for (int k = 0; k < c.Length; k++)
        {
            sum += Math.Exp(LogPmf(mean, k));
            c[k] = sum;
        }

        c[^1] = 1.0;
        return c;
    }

    private static double LogPmf(double mean, int k)
    {
        double logFactorial = 0;
        for (int i = 2; i <= k; i++) logFactorial += Math.Log(i);
        return -mean + k * Math.Log(mean) - logFactorial;
    }
}
