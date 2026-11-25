// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

[TestFixture]
public class CorrectionEffectMonitorTests
{
    [Test]
    public void A_still_star_with_growing_corrections_warns_within_seven_minutes()
    {
        // the guide log of 2026-11-21: a "star" fixed on the sensor 0.01 px off the lock position, Predictive RA taking the
        // offset for drift, pulses growing from 20 to 220 ms in 5 minutes, the star never moving
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.015, DownloadJitterSec = 0.1 };
        var warnings = loop.Run(TimeSpan.FromMinutes(6));

        warnings.Should().NotBeEmpty();
        warnings[0].Time.Should().BeLessThan(420);
        warnings[0].Effect.Effect.Should().BeLessThan(0.5);
        loop.Monitor.Estimate()!.Value.Effect.Should().BeApproximately(0, 0.3);
    }

    [Test]
    public void Corrections_that_work_against_a_large_drift_are_not_taken_for_no_effect()
    {
        // the same corrections as above, but they move the star and cancel a real drift: the star stays put just as well
        var loop = new Loop { Effect = 1, DriftPxPerSec = 0.25, NoisePx = 0.015, DownloadJitterSec = 0.1 };
        var warnings = loop.Run(TimeSpan.FromMinutes(30));

        warnings.Should().BeEmpty();
        var e = loop.Monitor.Estimate()!.Value;
        e.Effect.Should().BeApproximately(1, 3 * e.Sigma + 0.05);
        e.ExpectedPx.Should().BeGreaterThan(20);
    }

    [TestCase(0.0, 0.1)]
    [TestCase(0.1, 0.0)]
    [TestCase(0.1, 0.1)]
    public void The_measured_effect_is_unbiased_whether_the_camera_starts_late_or_downloads_late(double startJitterSec, double downloadJitterSec)
    {
        // Each correction carries its frame's measurement noise and the next move the same noise with the opposite sign.
        // With late starts plain least squares took that for drift: 1.21 to 1.27 instead of 1 with 0.015 px of noise, 1.5
        // with 0.03 px. Averaged over 20 runs of 30 minutes the effect must come out at 1 within a few hundredths.
        foreach (double noise in new[] { 0.015, 0.03 })
        {
            var effects = Enumerable.Range(1, 20).Select(seed =>
            {
                var loop = new Loop { Seed = seed, Effect = 1, DriftPxPerSec = 0.25, NoisePx = noise, StartJitterSec = startJitterSec, DownloadJitterSec = downloadJitterSec };
                loop.Run(TimeSpan.FromMinutes(30)).Should().BeEmpty();
                return loop.Monitor.Estimate()!.Value.Effect;
            }).ToList();

            TestContext.Out.WriteLine(FormattableString.Invariant($"noise {noise} px: mean effect {effects.Average():F3}, lowest {effects.Min():F2}, highest {effects.Max():F2}"));
            effects.Average().Should().BeApproximately(1, noise < 0.02 ? 0.04 : 0.08);
        }
    }

    [Test]
    public void A_mount_that_moves_a_third_of_the_calibrated_amount_reads_a_third_with_late_starts()
    {
        // plain least squares read a true effect of 0.3 at about 0.5 with late starts
        var effects = Enumerable.Range(1, 20).Select(seed =>
        {
            var loop = new Loop { Seed = seed, Effect = 0.3, DriftPxPerSec = 0.25, NoisePx = 0.015, StartJitterSec = 0.1 };
            loop.Run(TimeSpan.FromMinutes(30));
            return loop.Monitor.Estimate()!.Value.Effect;
        }).ToList();

        TestContext.Out.WriteLine(FormattableString.Invariant($"mean effect {effects.Average():F3}, lowest {effects.Min():F2}, highest {effects.Max():F2}"));
        effects.Average().Should().BeApproximately(0.3, 0.05);
    }

    [Test]
    public void A_still_star_warns_with_late_starts_as_well()
    {
        int warned = Enumerable.Range(1, 10).Count(seed =>
        {
            var loop = new Loop { Seed = seed, Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.015, StartJitterSec = 0.1 };
            return loop.Run(TimeSpan.FromMinutes(8)).Count > 0;
        });

        warned.Should().Be(10);
    }

    [Test]
    public void Without_timing_jitter_nothing_is_judged()
    {
        // the intervals vary only with the pulses, which says nothing about the drift
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.015 };
        loop.Run(TimeSpan.FromMinutes(10)).Should().BeEmpty();

        var working = new Loop { Effect = 1, DriftPxPerSec = 0.25, NoisePx = 0.015 };
        working.Run(TimeSpan.FromMinutes(10)).Should().BeEmpty();
    }

    [Test]
    public void A_noisy_star_is_not_judged()
    {
        // seeing of 0.3 px: the effect can't be measured precisely enough in two minutes
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.3, DownloadJitterSec = 0.1 };
        loop.Run(TimeSpan.FromMinutes(10)).Should().BeEmpty();
        loop.Monitor.Estimate()!.Value.Sigma.Should().BeGreaterThan(0.25);
    }

    [Test]
    public void Small_corrections_are_not_judged()
    {
        // no drift learned: the corrections only follow the noise and add up to less than 3 px
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, NoisePx = 0.015, DownloadJitterSec = 0.1, IntegratorGain = 0 };
        loop.Run(TimeSpan.FromMinutes(10)).Should().BeEmpty();
        Math.Abs(loop.Monitor.Estimate()!.Value.ExpectedPx).Should().BeLessThan(3);
    }

    [Test]
    public void The_warning_repeats_at_most_every_five_minutes_and_comes_again_after_a_restart()
    {
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.015, DownloadJitterSec = 0.1 };
        var warnings = loop.Run(TimeSpan.FromMinutes(20));

        warnings.Count.Should().BeInRange(3, 4);
        warnings.Zip(warnings.Skip(1), (a, b) => b.Time - a.Time).Should().OnlyContain(gap => gap >= 300);

        loop.Monitor.Restart();
        loop.Monitor.Estimate().Should().BeNull("the window starts over");
        loop.Run(TimeSpan.FromMinutes(3)).Should().NotBeEmpty("a new guiding run warns again at once");
    }

    [Test]
    public void A_break_keeps_the_window_and_a_reset_clears_it()
    {
        var loop = new Loop { Effect = 0, DriftPxPerSec = 0, OffsetPx = 0.01, NoisePx = 0.015, DownloadJitterSec = 0.1 };
        loop.Run(TimeSpan.FromMinutes(3));
        var before = loop.Monitor.Estimate()!.Value;

        // a correction after a break belongs to no pair
        loop.Monitor.Break();
        loop.Monitor.CorrectionApplied(100);
        loop.Monitor.Estimate()!.Value.Should().Be(before);

        loop.Monitor.Reset();
        loop.Monitor.Estimate().Should().BeNull();
    }

    /// <summary>
    /// A frame-by-frame RA loop in mount px: the star moves with the drift over the time between the exposures' middles
    /// and by <see cref="Effect"/> × each correction, which goes out after its frame and delays the next exposure by the
    /// pulse length. The correction takes out half the offset and a learned drift (an integrator of the offset, as the
    /// Predictive drift term does when the offset never goes away).
    /// </summary>
    private sealed class Loop
    {
        private Random? rng;
        private double requestSec;
        private double x;
        private double? lastMidSec;
        private double lastCorrection;
        private double learnedDrift;

        public CorrectionEffectMonitor Monitor { get; } = new(new SafetySettings());

        public int Seed { get; init; } = 11;

        private Random Rng => rng ??= new Random(Seed);

        public double Effect { get; init; } = 1;

        public double DriftPxPerSec { get; init; }

        /// <summary>Where a star that doesn't move sits relative to the lock position.</summary>
        public double OffsetPx { get; init; }

        public double NoisePx { get; init; }

        public double ExposureSec { get; init; } = 0.5;

        public double DownloadSec { get; init; } = 0.1;

        /// <summary>Mean of an exponential extra delay after the exposure.</summary>
        public double DownloadJitterSec { get; init; }

        /// <summary>Mean of an exponential delay between the request and the start of the exposure.</summary>
        public double StartJitterSec { get; init; }

        /// <summary>Pulse length per px of correction (the guide log's RA rate at Dec 57.6°: 1.18 px/s).</summary>
        public double PulseSecPerPx { get; init; } = 1 / 1.18;

        /// <summary>Drift learned per px of offset and frame; 0.09 ramps the pulses as fast as in the guide log.</summary>
        public double IntegratorGain { get; init; } = 0.09;

        public List<(double Time, CorrectionEffect Effect)> Run(TimeSpan duration)
        {
            var warnings = new List<(double, CorrectionEffect)>();
            double end = requestSec + duration.TotalSeconds;
            while (requestSec < end)
            {
                double startSec = requestSec + Exponential(StartJitterSec);
                double midSec = startSec + ExposureSec / 2;
                if (lastMidSec is { } lm)
                {
                    x += DriftPxPerSec * (midSec - lm) - Effect * lastCorrection;
                }

                double measured = (Effect == 0 ? OffsetPx : x) + NoisePx * Gaussian();
                double arrivalSec = startSec + ExposureSec + DownloadSec + Exponential(DownloadJitterSec);
                Monitor.Measured(requestSec, arrivalSec, ExposureSec * 1000, measured);
                if (Monitor.Evaluate(arrivalSec, out var effect))
                {
                    warnings.Add((arrivalSec, effect));
                }

                double interval = ExposureSec + DownloadSec;
                double correction = 0.5 * measured + learnedDrift * interval;
                learnedDrift += IntegratorGain * measured;

                // pulses shorter than 20 ms are rounded, as the guider does, and the length is whole milliseconds
                double ms = Math.Round(Math.Abs(correction) * PulseSecPerPx * 1000);
                ms = ms < 20 ? (ms >= 10 ? 20 : 0) : ms;
                correction = Math.Sign(correction) * ms / 1000 / PulseSecPerPx;
                Monitor.CorrectionApplied(correction);

                lastMidSec = midSec;
                lastCorrection = correction;
                requestSec = arrivalSec + 0.02 + ms / 1000;
            }

            return warnings;
        }

        private double Exponential(double mean) => mean > 0 ? -mean * Math.Log(1 - Rng.NextDouble()) : 0;

        private double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - Rng.NextDouble())) * Math.Cos(2 * Math.PI * Rng.NextDouble());
    }
}
