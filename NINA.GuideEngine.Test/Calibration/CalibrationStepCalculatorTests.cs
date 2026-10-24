// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class CalibrationStepCalculatorTests
{
    [Test]
    public void DistanceIsAtLeast25Px()
    {
        // 400 mm, 3.75 um: 1.93"/px -> 20"/1.93 = 10.3 px -> 25
        CalibrationStepCalculator.GetCalibrationDistance(400, 3.75, 1).Should().Be(25);
    }

    [Test]
    public void DistanceCoversTwentyArcsecAtFineScale()
    {
        // 2000 mm, 2.9 um: 0.2991"/px -> 20 / 0.2991 = 66.87 -> 67
        double scale = CalibrationStepCalculator.PixelScale(2.9, 2000, 1);
        CalibrationStepCalculator.GetCalibrationDistance(2000, 2.9, 1).Should().Be((int)Math.Ceiling(20.0 / scale)).And.Be(67);
        CalibrationStepCalculator.GetCalibrationDistance(new GuideOptics(2000, 2.9, 2)).Should().Be(34);
    }

    [Test]
    public void StepSizeFormula()
    {
        // scale = 206.265 * 3.75 / 400 = 1.9337; total = 25 * 1.9337 / 7.5 = 6.4458 s; /12 = 537.1 ms -> 550
        int step = CalibrationStepCalculator.GetCalibrationStepSize(400, 3.75, 1, 0.5, 12, 0.0, 25, out double scale);
        scale.Should().BeApproximately(1.93373, 1e-4);
        step.Should().Be(550);
    }

    [Test]
    public void StepSizeGrowsWithDeclinationButIsCappedByMinSteps()
    {
        // at dec 40: 537.1 / cos(40) = 701.2 -> 750
        CalibrationStepCalculator.GetCalibrationStepSize(400, 3.75, 1, 0.5, 12, 40.0, 25, out _).Should().Be(750);

        // at dec 60: 1074 > max pulse (total / MIN_STEPS = 1074.3) -> capped to 1074.3 -> 1100
        CalibrationStepCalculator.GetCalibrationStepSize(400, 3.75, 1, 0.5, 12, 60.0, 25, out _).Should().Be(1100);

        // at dec 80 the cap applies: 6445.8 / 6 = 1074.3 -> 1100
        CalibrationStepCalculator.GetCalibrationStepSize(400, 3.75, 1, 0.5, 12, 80.0, 25, out _).Should().Be(1100);
    }

    [Test]
    public void StepSizeRoundsUpTo50Ms()
    {
        for (double speed = 0.1; speed <= 2.0; speed += 0.05)
        {
            int step = CalibrationStepCalculator.GetCalibrationStepSize(500, 4.5, 1, speed, 12, 10, 25, out _);
            (step % 50).Should().Be(0);
            step.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void RecommendDefaultsTo750WhenRateUnknown()
    {
        var optics = new GuideOptics(400, 3.75);
        CalibrationStepRecommendation r = CalibrationStepCalculator.Recommend(optics, new MountSnapshot { IsConnected = true });
        r.StepMs.Should().Be(750);
        r.DistancePx.Should().Be(25);
        r.GuideSpeedKnown.Should().BeFalse();

        CalibrationStepRecommendation r2 = CalibrationStepCalculator.Recommend(optics,
            new MountSnapshot { GuideRateRa = 0.5, GuideRateDec = 0.25, DeclinationDeg = -40 });
        r2.GuideSpeed.Should().Be(0.5); // larger of the two rates
        r2.StepMs.Should().Be(750); // |dec| used
        r2.GuideSpeedKnown.Should().BeTrue();
    }

    [Test]
    public void GuideSpeedFromMountIsClampedToMinimum()
    {
        CalibrationStepCalculator.GuideSpeedFromMount(new MountSnapshot { GuideRateRa = 0.05, GuideRateDec = 0.02 }).Should().Be(0.10);
        CalibrationStepCalculator.GuideSpeedFromMount(new MountSnapshot { GuideRateDec = 0.8 }).Should().Be(0.8);
        CalibrationStepCalculator.GuideSpeedFromMount(null).Should().BeNull();
    }

    [Test]
    public void CheckDurationDoesNothingOnFirstCalibration()
    {
        var check = CalibrationStepCalculator.CheckCalibrationDuration(750, 25, new GuideOptics(400, 3.75), 0.5, null);
        check.StepChanged.Should().BeFalse();
        check.DistanceChanged.Should().BeFalse();
        check.StepMs.Should().Be(750);
    }

    [Test]
    public void CheckDurationIgnoresSmallRateChange()
    {
        var prev = new CalibrationData { XRate = 0.01, GuideRateRa = 0.5, Binning = 1 };
        var check = CalibrationStepCalculator.CheckCalibrationDuration(750, 25, new GuideOptics(400, 3.75), 0.52, prev);
        check.StepChanged.Should().BeFalse();
        check.StepMs.Should().Be(750);
    }

    [Test]
    public void CheckDurationRecomputesOnRateChange()
    {
        var prev = new CalibrationData { XRate = 0.01, GuideRateRa = 0.5, Binning = 1 };
        var check = CalibrationStepCalculator.CheckCalibrationDuration(750, 25, new GuideOptics(400, 3.75), 1.0, prev);
        check.StepChanged.Should().BeTrue();
        check.Reason.Should().Be("mount guide speed");

        // 25 * 1.9337 / 15 = 3.2229 s / 12 = 268.6 -> 300 (declination 0 as in PHD2)
        check.StepMs.Should().Be(300);
    }

    [Test]
    public void CheckDurationRecomputesDistanceAndStepOnBinningChange()
    {
        var prev = new CalibrationData { XRate = 0.01, GuideRateRa = 0.5, Binning = 1 };
        var optics = new GuideOptics(2000, 2.9, 2);
        var check = CalibrationStepCalculator.CheckCalibrationDuration(750, 67, optics, 0.5, prev);
        check.DistanceChanged.Should().BeTrue();
        check.DistancePx.Should().Be(34);
        check.Reason.Should().Be("binning");
        check.StepMs.Should().Be(CalibrationStepCalculator.GetCalibrationStepSize(2000, 2.9, 2, 0.5, 12, 0, 34, out _));

        // no mount rates: distance still updated, step unchanged
        var noRates = CalibrationStepCalculator.CheckCalibrationDuration(750, 67, optics, null, prev);
        noRates.DistancePx.Should().Be(34);
        noRates.StepChanged.Should().BeFalse();
    }
}
