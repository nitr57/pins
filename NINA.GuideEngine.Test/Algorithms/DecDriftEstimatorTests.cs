// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// The open-loop Dec drift of Dec guide mode Drift on a synthetic axis whose true motion is known: drift, random-walk wander
/// and seeing, guided by a simple proportional controller whose corrections are reported like the guider does.
/// </summary>
[TestFixture]
public class DecDriftEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public void Needs_two_minutes_and_twenty_frames()
    {
        var axis = new Axis(driftPxPerSec: 0.01, seeingPx: 0.1);
        axis.Run(TimeSpan.FromSeconds(118));
        axis.Estimator.Estimate().Should().BeNull("less than 2 minutes of guiding");
        axis.Run(TimeSpan.FromSeconds(4));
        axis.Estimator.Estimate().Should().NotBeNull();

        var sparse = new Axis(driftPxPerSec: 0.01, seeingPx: 0.1, frameSec: 10);
        sparse.Run(TimeSpan.FromMinutes(3));
        sparse.Estimator.Estimate().Should().BeNull("19 frames");
    }

    [Test]
    public void Long_exposures_stretch_the_window_and_the_wander_still_ignores_gusts()
    {
        // 16 s frames: 300 s hold 19 of them, one short of an estimate; the window covers 25 frame intervals instead
        var axis = new Axis(driftPxPerSec: 0.005, seeingPx: 0.1, frameSec: 16);
        axis.Run(TimeSpan.FromMinutes(8));
        axis.Estimator.WindowLengthSec.Should().Be(DecDriftEstimator.WindowFrames * 16);
        var d = axis.Estimator.Estimate()!.Value;
        TestContext.Out.WriteLine($"drift {d.PxPerSec * 60:F3} ± {d.SigmaPxPerSec * 60:F3} px/min over {d.SpanSec:F0} s, {d.Samples} frames");
        d.SpanSec.Should().BeGreaterThan(DecDriftEstimator.WindowSec);
        d.PxPerSec.Should().BeApproximately(0.005, 3 * d.SigmaPxPerSec);

        // an unannounced step within the first 30 frames (too early to be told as a jump) counts in the wander only up to
        // 4 σ of what the noise explains, a large one as much as a smaller one; long frames carry little weight each in the
        // noise sums, and the clipping waits for a frame count, not for a weight they would never reach
        double Wander(double stepPx)
        {
            var gusty = new Axis(driftPxPerSec: 0.005, seeingPx: 0.1, frameSec: 16);
            gusty.Run(TimeSpan.FromMinutes(4));
            gusty.Jump(stepPx, announce: false);
            gusty.Run(TimeSpan.FromMinutes(8));
            gusty.Estimator.Jumps.Should().Be(0);
            return gusty.Estimator.WanderRate(0);
        }

        double small = Wander(3), large = Wander(6);
        TestContext.Out.WriteLine($"wander rate after a 3 px step {small:G3}, after a 6 px step {large:G3} px²/s");
        large.Should().BeLessThan(1.3 * small, "clipped: without it the 6 px step would count four times the 3 px one");
    }

    [Test]
    public void Finds_the_drift_behind_the_corrections()
    {
        var axis = new Axis(driftPxPerSec: 0.012, seeingPx: 0.15);
        axis.Run(TimeSpan.FromMinutes(6));
        var d = axis.Estimator.Estimate()!.Value;
        TestContext.Out.WriteLine($"drift {d.PxPerSec * 60:F3} ± {d.SigmaPxPerSec * 60:F3} px/min, noise {d.NoisePx:F3} px");
        axis.MeanAbsOffset.Should().BeLessThan(0.3, "the axis is guided: the offsets hide the drift");
        d.PxPerSec.Should().BeApproximately(0.012, 3 * d.SigmaPxPerSec);
        d.Sigmas.Should().BeGreaterThan(5);
        d.NoisePx.Should().BeApproximately(0.15, 0.03);
        d.SpanSec.Should().BeApproximately(DecDriftEstimator.WindowSec, 3);
    }

    [TestCase(0.0, 0.0)]
    [TestCase(0.005, 0.0)]
    [TestCase(0.0, 0.02)]
    [TestCase(-0.008, 0.02)]
    public void Its_uncertainty_is_honest_with_wander(double drift, double wanderPxPerSqrtSec)
    {
        // z-scores over many independent skies: with a random-walk mount a plain white-noise fit would claim a significant
        // drift most of the time; the wander term keeps |z| ≥ 2.5 rare
        var z = new List<double>();
        for (int seed = 1; seed <= 60; seed++)
        {
            var axis = new Axis(drift, seeingPx: 0.12, wanderPxPerSqrtSec: wanderPxPerSqrtSec, seed: seed);
            for (int k = 0; k < 4; k++)
            {
                axis.Run(TimeSpan.FromMinutes(k == 0 ? 10 : 7));
                var d = axis.Estimator.Estimate()!.Value;
                z.Add((d.PxPerSec - drift) / d.SigmaPxPerSec);
            }
        }

        double beyond = z.Count(v => Math.Abs(v) >= DecDirectionPolicy.SignificanceSigmas) / (double)z.Count;
        double spread = Math.Sqrt(z.Sum(v => v * v) / z.Count);
        TestContext.Out.WriteLine($"drift {drift}, wander {wanderPxPerSqrtSec}: rms z {spread:F2}, |z| ≥ 2.5 in {beyond:P1}");
        beyond.Should().BeLessThan(0.05);
        spread.Should().BeLessThan(1.35, "hardly more confident than it should be (the wander prior makes it cautious without wander)");
    }

    [Test]
    public void Wander_is_learned_from_the_session()
    {
        var calm = new Axis(0, seeingPx: 0.1, wanderPxPerSqrtSec: 0, seed: 3);
        var wandering = new Axis(0, seeingPx: 0.1, wanderPxPerSqrtSec: 0.03, seed: 3);
        calm.Run(TimeSpan.FromMinutes(30));
        wandering.Run(TimeSpan.FromMinutes(30));
        double prior = 0.1 * 0.1 / DecDriftEstimator.PriorWanderSec;
        double calmRate = calm.Estimator.WanderRate(0);
        double wanderRate = wandering.Estimator.WanderRate(0);
        TestContext.Out.WriteLine($"wander rate calm {calmRate:G3}, wandering {wanderRate:G3} (true 9e-4), prior {prior:G3} px²/s");
        calmRate.Should().BeLessThan(0.4 * prior, "30 minutes without wander outweigh the prior");

        // about 7 independent one-minute slope changes in the memory (and the prior still weighs 30 %): within their scatter
        wanderRate.Should().BeInRange(9e-4 / 3, 9e-4 * 1.5);
        calm.Estimator.BacklashPx.Should().Be(0);
        wandering.Estimator.BacklashPx.Should().Be(0, "a dead band that swallows the corrections hides the wander, no evidence of backlash");
        calm.Estimator.Estimate()!.Value.SigmaPxPerSec.Should().BeLessThan(wandering.Estimator.Estimate()!.Value.SigmaPxPerSec);
    }

    [Test]
    public void A_dither_starts_a_new_segment_and_keeps_the_drift()
    {
        var axis = new Axis(driftPxPerSec: 0.01, seeingPx: 0.05);
        axis.Run(TimeSpan.FromMinutes(3));
        axis.Dither(4.0);
        axis.Run(TimeSpan.FromMinutes(3));
        axis.Estimator.Estimate()!.Value.PxPerSec.Should().BeApproximately(0.01, 0.002, "the offset jumps with the lock, the estimate stays");
    }

    [Test]
    public void A_jump_stays_out_of_the_drift()
    {
        // 3 px within one frame, announced by the guider (a break) or not (no dead band explains it); 0.2 px is within the
        // frame-to-frame noise and counts as motion
        var announced = new Axis(driftPxPerSec: 0.01, seeingPx: 0.05);
        var unannounced = new Axis(driftPxPerSec: 0.01, seeingPx: 0.05);
        var small = new Axis(driftPxPerSec: 0.01, seeingPx: 0.05);
        foreach (var (axis, px, announce) in new[] { (announced, 3.0, true), (unannounced, 3.0, false), (small, 0.2, false) })
        {
            axis.Run(TimeSpan.FromMinutes(2.5));
            axis.Jump(px, announce);
            axis.Run(TimeSpan.FromMinutes(2.5));
        }

        announced.Estimator.Estimate()!.Value.PxPerSec.Should().BeApproximately(0.01, 0.002);
        announced.Estimator.Jumps.Should().Be(0, "the break came first");
        unannounced.Estimator.Estimate()!.Value.PxPerSec.Should().BeApproximately(0.01, 0.002);
        unannounced.Estimator.Jumps.Should().Be(1);
        small.Estimator.Jumps.Should().Be(0);
    }

    [Test]
    public void A_doubted_dead_band_makes_the_drift_uncertain()
    {
        // a guided mount that wanders: dead bands that swallow the corrections look smoother (no evidence of backlash, and
        // none is used), but while the dead band is in doubt the drift must hold whatever it is
        var axis = new Axis(driftPxPerSec: 0.005, seeingPx: 0.1, wanderPxPerSqrtSec: 0.03, seed: 3);
        axis.Run(TimeSpan.FromMinutes(20));
        var plain = axis.Estimator.Estimate()!.Value;
        axis.Estimator.DoubtBacklash = true;
        var doubted = axis.Estimator.Estimate()!.Value;
        TestContext.Out.WriteLine($"drift {plain.PxPerSec * 60:F3} px/min, ± {plain.SigmaPxPerSec * 60:F3} → ± {doubted.SigmaPxPerSec * 60:F3} in doubt");
        axis.Estimator.BacklashPx.Should().Be(0);
        doubted.PxPerSec.Should().Be(plain.PxPerSec);
        doubted.SigmaPxPerSec.Should().BeGreaterThan(1.5 * plain.SigmaPxPerSec);
    }

    [Test]
    public void Reset_forgets_everything()
    {
        var axis = new Axis(driftPxPerSec: 0.01, seeingPx: 0.1);
        axis.Run(TimeSpan.FromMinutes(5));
        axis.Estimator.Reset();
        axis.Estimator.Count.Should().Be(0);
        axis.Estimator.Estimate().Should().BeNull();
        axis.Estimator.WanderRate(0.01).Should().BeApproximately(0.01 / DecDriftEstimator.PriorWanderSec, 1e-12, "only the prior is left");
    }

    [Test]
    public void While_one_direction_is_guided_backlash_after_a_reversal_is_left_out()
    {
        // South against a steady drift; at 3 min a dither puts the star 2 px on the North side: North corrections (as while
        // settling), then South again, each first taking up the 3 px dead band
        var axis = new Axis(driftPxPerSec: 0.01, seeingPx: 0.05, backlashPx: 3.0);
        axis.Estimator.GuardReversals = true;
        axis.Run(TimeSpan.FromMinutes(3));
        axis.Dither(2.0);
        axis.Run(TimeSpan.FromSeconds(2));
        axis.Estimator.TakingUpBacklash.Should().BeTrue("North after South");
        int frames = axis.Estimator.Count;
        axis.Run(TimeSpan.FromSeconds(2));
        axis.Estimator.Count.Should().Be(frames, "the mount hasn't followed yet: the frame is left out");
        axis.Run(TimeSpan.FromMinutes(3));
        var d = axis.Estimator.Estimate()!.Value;
        TestContext.Out.WriteLine($"drift {d.PxPerSec * 60:F3} ± {d.SigmaPxPerSec * 60:F3} px/min, {axis.Reversals} reversals");
        axis.Reversals.Should().BeGreaterThanOrEqualTo(2);
        d.PxPerSec.Should().BeApproximately(0.01, Math.Max(3 * d.SigmaPxPerSec, 0.001));
    }

    [Test]
    public void Learns_the_dead_band_from_frequent_reversals()
    {
        // a controller that corrects every offset in full chases the seeing and reverses all the time (like Predictive in
        // Auto); the mount ignores up to 3 px after each reversal
        var axis = new Axis(driftPxPerSec: 0.008, seeingPx: 0.15, backlashPx: 3.0) { Aggression = 1.0, MinMovePx = 0 };
        var plain = new Axis(driftPxPerSec: 0.008, seeingPx: 0.15, backlashPx: 0) { Aggression = 1.0, MinMovePx = 0 };
        axis.Run(TimeSpan.FromMinutes(20));
        plain.Run(TimeSpan.FromMinutes(20));
        axis.Reversals.Should().BeGreaterThan(100);
        var d = axis.Estimator.Estimate()!.Value;
        TestContext.Out.WriteLine($"dead band {axis.Estimator.BacklashPx} px, drift {d.PxPerSec * 60:F3} ± {d.SigmaPxPerSec * 60:F3} px/min, {axis.Reversals} reversals");
        axis.Estimator.BacklashPx.Should().Be(3);
        axis.Estimator.Jumps.Should().Be(0, "the dead band explains every take-up");
        d.PxPerSec.Should().BeApproximately(0.008, 3 * d.SigmaPxPerSec);
        d.Sigmas.Should().BeGreaterThan(3);
        plain.Estimator.BacklashPx.Should().Be(0, "no dead band: the plain reconstruction is the smoothest");
    }

    [Test]
    public void The_take_up_ends_when_the_mount_follows_or_after_the_timeout()
    {
        var e = new DecDriftEstimator { GuardReversals = true };
        var t = T0;
        for (int i = 0; i < 10; i++, t = t.AddSeconds(2))
        {
            e.Add(t, 0.5);
            e.CorrectionApplied(0.2);
        }

        e.CorrectionApplied(-0.2);
        e.TakingUpBacklash.Should().BeTrue("North after South");
        int before = e.Count;
        e.Add(t, -0.4);
        e.Count.Should().Be(before, "the offset has not crossed the lock position yet");
        e.Add(t.AddSeconds(2), 0.1);
        e.TakingUpBacklash.Should().BeFalse("North pushed the star across the lock position");
        e.Count.Should().Be(before + 1);

        e.CorrectionApplied(0.3);
        e.TakingUpBacklash.Should().BeTrue();
        e.Add(t.AddSeconds(4), 1.0);
        e.Add(t.AddSeconds(4 + DecDriftEstimator.TakeUpTimeoutSec), 1.0);
        e.TakingUpBacklash.Should().BeFalse("never waits longer than the timeout");

        e.GuardReversals = false;
        e.CorrectionApplied(-0.1);
        e.TakingUpBacklash.Should().BeFalse("both directions: no guard");
    }

    /// <summary>
    /// A guided axis: position = drift + wander − the corrections that moved it (after the dead band of the gears); measured
    /// with seeing; corrected by <c>Aggression</c> × the measured offset beyond <c>MinMovePx</c>; the estimator sees what the
    /// guider would.
    /// </summary>
    private sealed class Axis(double driftPxPerSec, double seeingPx, double wanderPxPerSqrtSec = 0, int seed = 1, double frameSec = 2,
        double backlashPx = 0)
    {
        private int lastSign;

        private readonly SimRng rng = new((ulong)seed);
        private readonly List<double> offsets = [];
        private DateTimeOffset now = T0;
        private double position;
        private double lockPosition;
        private double play;

        public DecDriftEstimator Estimator { get; } = new();

        public double Drift { get; } = driftPxPerSec;

        public double MeanAbsOffset => offsets.Count == 0 ? 0 : offsets.Average(Math.Abs);

        public double Aggression { get; init; } = 0.7;

        public double MinMovePx { get; init; } = 0.1;

        public int Reversals { get; private set; }

        public void Run(TimeSpan duration)
        {
            var end = now + duration;
            while (now < end)
            {
                double offset = position - lockPosition + seeingPx * rng.NextGaussian();
                offsets.Add(offset);
                Estimator.Add(now, offset);
                double correction = Math.Abs(offset) > MinMovePx ? Aggression * offset : 0;
                if (correction != 0 && Math.Sign(correction) != lastSign)
                {
                    Reversals += lastSign != 0 ? 1 : 0;
                    lastSign = Math.Sign(correction);
                }

                Move(-correction);
                Estimator.CorrectionApplied(correction);
                now = now.AddSeconds(frameSec);
                position += Drift * frameSec + wanderPxPerSqrtSec * Math.Sqrt(frameSec) * rng.NextGaussian();
            }
        }

        public void Dither(double px)
        {
            lockPosition += px;
            Estimator.Break();
        }

        public void Jump(double px, bool announce)
        {
            position += px;
            if (announce)
            {
                Estimator.Break();
            }
        }

        // a pulse moves the mount only once the gears engaged: the play in [−backlash/2, +backlash/2] is taken up first
        private void Move(double px)
        {
            if (backlashPx <= 0)
            {
                position += px;
                return;
            }

            double half = backlashPx / 2;
            double next = Math.Clamp(play + px, -half, half);
            position += px - (next - play);
            play = next;
        }
    }
}
