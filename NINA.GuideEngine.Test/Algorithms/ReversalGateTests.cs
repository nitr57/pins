// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class ReversalGateTests
{
    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Test]
    public void EverythingIsAllowedUntilTheNoiseIsKnown()
    {
        var gate = new ReversalGate();
        gate.Allows(1).Should().BeTrue();
        gate.Observe(0.01, 0);
        gate.Observe(-0.01, 0);
        gate.IsWarm.Should().BeFalse();
        gate.Allows(-1).Should().BeTrue();
    }

    [Test]
    public void LearnsTheNoiseFromTheFrameToFrameChanges()
    {
        var rng = new Random(3);
        var gate = new ReversalGate();
        double sum = 0;
        for (int i = 0; i < 4000; i++)
        {
            gate.Observe(0.2 * Gauss(rng), 0);
            if (i >= 1000)
            {
                sum += gate.NoiseSigmaPx;
            }
        }

        // the estimate follows the noise over its window: on average it is the noise σ
        (sum / 3000).Should().BeApproximately(0.2, 0.02);
    }

    [Test]
    public void PureNoiseRarelyJustifiesAReversal()
    {
        var rng = new Random(7);
        var gate = new ReversalGate();
        int allowed = 0;
        const int frames = 20000;
        for (int i = 0; i < frames; i++)
        {
            gate.Observe(0.17 * Gauss(rng), 0);
            if (i > ReversalGate.WarmupFrames && (gate.Allows(1) || gate.Allows(-1)))
            {
                allowed++;
            }
        }

        // the estimated noise is a little low after quiet stretches, so the rate is above the textbook one, but rare
        allowed.Should().BeLessThan(frames / 100);
    }

    [TestCase(1.0, 25)]
    [TestCase(2.0, 8)]
    public void ARealOffsetGetsThroughWithinAFewFrames(double sigmas, int maxFrames)
    {
        var rng = new Random(11);
        var gate = new ReversalGate();
        for (int i = 0; i < 200; i++)
        {
            gate.Observe(0.17 * Gauss(rng), 0);
        }

        int frames = 0;
        while (!gate.Allows(1) && frames < 200)
        {
            gate.Observe(0.17 * (sigmas + Gauss(rng)), 0);
            frames++;
        }

        frames.Should().BeLessThanOrEqualTo(maxFrames);
    }

    [Test]
    public void ALargeOffsetGetsThroughAtOnce()
    {
        var rng = new Random(5);
        var gate = new ReversalGate();
        for (int i = 0; i < 200; i++)
        {
            gate.Observe(0.1 * Gauss(rng), 0);
        }

        gate.Observe(-0.1 * (ReversalGate.ImmediateSigmas + 1), 0);
        gate.Allows(-1).Should().BeTrue();
        gate.Allows(1).Should().BeFalse();
    }

    [Test]
    public void AStarThatWandersIsFollowed()
    {
        // a random walk of 0.12 px a frame and 0.06 px of seeing, as on the Sky Simulator session of 2026-10-18, each offset
        // corrected in full: the frame-to-frame noise is about 0.10 px, but most of it is the star moving
        var rng = new Random(17);
        var gate = new ReversalGate();
        double star = 0, corrected = 0, lastCorrection = 0;
        for (int i = 0; i < 2000; i++)
        {
            star += 0.12 * Gauss(rng);
            double offset = star - corrected + 0.06 * Gauss(rng);
            gate.Observe(offset, lastCorrection);
            corrected += offset;
            lastCorrection = offset;
        }

        gate.NoiseSigmaPx.Should().BeGreaterThan(0.09);
        gate.ResetEvidence();

        // an offset of more than five times the seeing is the star, and is followed at once
        gate.Observe(0.32, 0);
        gate.Allows(1).Should().BeTrue();
    }

    [Test]
    public void CorrectionsTheMountDidNotMakeDoNotInflateTheNoise()
    {
        // every 8th frame a correction of about 1–9 px is expected but the star does not move, as with the compensated
        // reversals of the PHD2 logs of 2026-09/10
        var rng = new Random(13);
        var gate = new ReversalGate();
        double sum = 0;
        for (int i = 0; i < 4000; i++)
        {
            double missing = i % 8 == 0 ? 1 + 8 * rng.NextDouble() : 0;
            gate.Observe(0.1 * Gauss(rng), missing);
            if (i >= 1000)
            {
                sum += gate.NoiseSigmaPx;
            }
        }

        (sum / 3000).Should().BeApproximately(0.1, 0.02);
        gate.NoiseComponents.WhitePx2.Should().BeLessThan(4 * 0.1 * 0.1);
    }

    [Test]
    public void TheExpectedCorrectionIsNotTakenForNoise()
    {
        var gate = new ReversalGate();

        // a correction of 0.5 px brings the offset back exactly: the change is the correction, not noise
        gate.Observe(0.5, 0);
        gate.Observe(0.0, 0.5);
        for (int i = 0; i < 10; i++)
        {
            gate.Observe(0.0, 0);
        }

        gate.NoiseSigmaPx.Should().Be(ReversalGate.MinNoisePx);
    }

    [Test]
    public void APastLargeOffsetDoesNotLetSmallOnesThrough()
    {
        var rng = new Random(11);
        var gate = new ReversalGate();
        for (int i = 0; i < 200; i++)
        {
            gate.Observe(0.1 * Gauss(rng), 0);
        }

        // a dither: the star 3.6 px off for a few frames, then back (frames 6089-6093 of the 2026-10-18 log)
        foreach (double offset in new[] { 3.6, 3.2, 1.25, 0.45 })
        {
            gate.Observe(offset, 0);
        }

        gate.Allows(1).Should().BeTrue("the dither itself is a real offset");
        gate.SouthEvidence.Should().BeLessThanOrEqualTo(ReversalGate.DefaultThreshold);

        gate.Observe(0.0, 0);
        gate.Observe(0.04, 0);
        gate.Allows(1).Should().BeFalse("0.04 px is noise once the star is back");
    }
}
