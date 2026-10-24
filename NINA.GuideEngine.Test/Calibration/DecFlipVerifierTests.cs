// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class DecFlipVerifierTests
{
    private const double YRate = 0.00375; // px/ms

    /// <summary>
    /// 1-D Dec loop: the guider corrects the measured error with the given aggression (PHD2 convention: error &gt; 0 →
    /// South). If <paramref name="inverted"/>, the mount moves the star the opposite way.
    /// </summary>
    private static DecFlipCheckResult RunLoop(double initialError, bool inverted, double aggression = 1.0, double minMove = 0.15,
        double noise = 0.0, double driftPerFrame = 0.0, double backlashMs = 0.0, int seed = 1, int frames = 12,
        DecFlipVerifier? verifier = null, bool mountResponds = true)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());

        verifier ??= new DecFlipVerifier();
        verifier.Start();
        var transform = new MountTransform(0, Math.PI / 2, YRate, YRate);
        double truth = initialError;
        GuideDirection? lastDir = null;
        double backlashLeft = 0;
        DecFlipCheckResult result = null!;

        for (int i = 0; i < frames && verifier.IsActive; i++)
        {
            double measured = truth + noise * Gauss();
            PulseCommand? pulse = null;
            if (Math.Abs(measured) >= minMove)
            {
                PulseCommand p = transform.DecPulse(measured * aggression);
                pulse = p.DurationMs > 0 ? p : null;
            }

            result = verifier.Observe(measured, pulse, YRate);

            if (pulse is { } pc)
            {
                if (pc.Direction != lastDir)
                {
                    backlashLeft = backlashMs;
                    lastDir = pc.Direction;
                }

                double eff = Math.Max(0, pc.DurationMs - backlashLeft);
                backlashLeft = Math.Max(0, backlashLeft - pc.DurationMs);
                double move = DecFlipVerifier.ExpectedMove(pc with { DurationMs = (int)eff }, YRate);
                if (mountResponds)
                {
                    truth += inverted ? -move : move;
                }
            }

            truth += driftPerFrame;
        }

        return result;
    }

    [TestCase(3.0)]
    [TestCase(-4.0)]
    [TestCase(1.5)]
    public void InvertedDecIsDetected(double initialError)
    {
        DecFlipCheckResult r = RunLoop(initialError, inverted: true);
        r.Verdict.Should().Be(DecFlipVerdict.InvertDec);
        r.WrongSign.Should().BeGreaterThanOrEqualTo(3);
        r.RightSign.Should().Be(0);
        r.FramesObserved.Should().BeLessThanOrEqualTo(5);
    }

    [Test]
    public void InvertedDecWithNoiseAndGentleAggressionIsDetected()
    {
        int detected = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            if (RunLoop(2.5, inverted: true, aggression: 0.7, noise: 0.2, seed: seed).Verdict == DecFlipVerdict.InvertDec)
            {
                detected++;
            }
        }

        detected.Should().BeGreaterThan(190);
    }

    [TestCase(3.0)]
    [TestCase(-4.0)]
    [TestCase(10.0)]
    public void CorrectSignIsOk(double initialError)
    {
        DecFlipCheckResult r = RunLoop(initialError, inverted: false);
        r.Verdict.Should().Be(DecFlipVerdict.DecOk);
        r.WrongSign.Should().Be(0);
    }

    [Test]
    public void NoiseOnlyNeverTriggers()
    {
        // star jitters around the lock position (seeing) while the corrections have no net effect
        for (int seed = 0; seed < 1000; seed++)
        {
            RunLoop(0.0, inverted: true, noise: 0.3, seed: seed, mountResponds: false).Verdict
                .Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
            RunLoop(0.0, inverted: true, noise: 0.5, aggression: 0.5, seed: seed, mountResponds: false).Verdict
                .Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
        }
    }

    [Test]
    public void CorrectSignWithSeeingNoiseNeverTriggers()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            RunLoop(0.0, inverted: false, noise: 0.5, seed: seed).Verdict.Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
            RunLoop(1.0, inverted: false, noise: 0.3, aggression: 0.7, seed: seed).Verdict.Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
        }
    }

    [Test]
    public void CorrectSignWithNoiseAndDriftNeverTriggers()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            RunLoop(2.0, inverted: false, aggression: 0.7, noise: 0.4, driftPerFrame: 0.3, seed: seed).Verdict
                .Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
            RunLoop(-3.0, inverted: false, aggression: 1.0, noise: 0.25, driftPerFrame: -0.2, seed: seed).Verdict
                .Should().NotBe(DecFlipVerdict.InvertDec, $"seed {seed}");
        }
    }

    [Test]
    public void BacklashOnlyNeverTriggers()
    {
        // Large Dec backlash: corrections have no visible effect -> no evidence either way
        DecFlipCheckResult r = RunLoop(3.0, inverted: false, backlashMs: 20000, driftPerFrame: 0.05);
        r.Verdict.Should().Be(DecFlipVerdict.DecOk);
        r.WrongSign.Should().Be(0);
    }

    [Test]
    public void SmallErrorsGiveNoEvidence()
    {
        // corrections expecting less than 0.5 px of movement are ignored even with the wrong sign
        DecFlipCheckResult r = RunLoop(2.0, inverted: true, aggression: 0.2, frames: 8, mountResponds: false);
        r.Verdict.Should().Be(DecFlipVerdict.DecOk);
        r.CorrectionsEvaluated.Should().Be(0);
    }

    [Test]
    public void LostStarFramesBreakThePairing()
    {
        var v = new DecFlipVerifier();
        v.Start();
        v.Observe(3.0, new PulseCommand(GuideDirection.South, 800), YRate);
        v.Observe(double.NaN, null, YRate).CorrectionsEvaluated.Should().Be(0);
        v.Observe(6.0, new PulseCommand(GuideDirection.South, 1600), YRate).CorrectionsEvaluated.Should().Be(0);
        v.Observe(12.0, new PulseCommand(GuideDirection.South, 3200), YRate).WrongSign.Should().Be(1);
    }

    [Test]
    public void InactiveUntilStartedAndStopsAfterWindow()
    {
        var v = new DecFlipVerifier();
        v.Verdict.Should().Be(DecFlipVerdict.Inactive);
        v.Observe(3, new PulseCommand(GuideDirection.South, 800), YRate).FramesObserved.Should().Be(0);

        v.Start();
        for (int i = 0; i < 8; i++)
        {
            v.Observe(0.05, null, YRate);
        }

        v.Verdict.Should().Be(DecFlipVerdict.DecOk);
        v.IsActive.Should().BeFalse();

        v.Reset();
        v.Verdict.Should().Be(DecFlipVerdict.Inactive);
    }

    [Test]
    public void ExpectedMoveSigns()
    {
        DecFlipVerifier.ExpectedMove(new PulseCommand(GuideDirection.South, 100), 0.01).Should().BeApproximately(-1.0, 1e-12);
        DecFlipVerifier.ExpectedMove(new PulseCommand(GuideDirection.North, 100), 0.01).Should().BeApproximately(1.0, 1e-12);
        DecFlipVerifier.ExpectedMove(new PulseCommand(GuideDirection.West, 100), 0.01).Should().Be(0);
        DecFlipVerifier.ExpectedMove(null, 0.01).Should().Be(0);
    }

    [Test]
    public void EndToEndAfterWrongFlipSetting()
    {
        // Calibrate on the east side, flip to the west on a mount whose Dec does NOT reverse (needs decFlipRequired),
        // but the setting is off: the verifier must request inversion, and the inverted calibration must guide correctly.
        var m = new KinematicMount { EastAngleDeg = 30, NorthAngleDeg = 120 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        var west = new KinematicMount { EastAngleDeg = 210, NorthAngleDeg = 300, PierSide = PierSide.West };
        CalibrationAdjustment adj = CalibrationAdjuster.AdjustForScopePointing(cal, new ScopePointing
        {
            Mount = west.Snapshot() with { DeclinationDeg = 0 },
            PixelSizeUm = cal.PixelSizeUm,
            DecFlipRequired = false,
        });
        adj.Flipped.Should().BeTrue();

        CalibrationData active = adj.Effective;
        var verifier = new DecFlipVerifier();
        verifier.Start();
        GuidePoint lockPos = west.Position;
        west.Position = new GuidePoint(lockPos.X + 1, lockPos.Y + 3);

        for (int i = 0; i < 8 && verifier.IsActive; i++)
        {
            var t = new MountTransform(active);
            GuidePoint mnt = t.CameraToMount(west.Position - lockPos);
            PulseCommand dec = t.DecPulse(mnt.Y);
            verifier.Observe(mnt.Y, dec, active.YRate);
            west.Apply(t.RaPulse(mnt.X));
            west.Apply(dec);
        }

        verifier.Verdict.Should().Be(DecFlipVerdict.InvertDec);

        active = CalibrationAdjuster.InvertDec(active);
        west.Position = new GuidePoint(lockPos.X - 2, lockPos.Y + 2);
        var fixedT = new MountTransform(active);
        GuidePoint m2 = fixedT.CameraToMount(west.Position - lockPos);
        west.Apply(fixedT.RaPulse(m2.X));
        west.Apply(fixedT.DecPulse(m2.Y));
        west.Position.Distance(lockPos).Should().BeLessThan(0.05);
    }
}
