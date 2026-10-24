// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class CalibrationProcessTests
{
    [TestCase(30.0, 120.0)]
    [TestCase(0.0, 90.0)]
    [TestCase(-100.0, -10.0)]
    [TestCase(170.0, -100.0)]
    [TestCase(45.0, -45.0)] // mirrored image (north = east - 90)
    public void RecoversAnglesAndRates(double east, double north)
    {
        var m = new KinematicMount { EastAngleDeg = east, NorthAngleDeg = north };
        var (p, updates) = CalibrationRunner.Run(m);

        CalibrationUpdate last = updates[^1];
        last.State.Should().Be(CalibrationState.Complete);
        CalibrationData cal = last.Result!;
        CalibrationRunner.AngleDiffDeg(cal.XAngle, m.EastAngle).Should().BeLessThan(0.01);
        CalibrationRunner.AngleDiffDeg(cal.YAngle, m.NorthAngle).Should().BeLessThan(0.01);
        cal.XRate.Should().BeApproximately(m.RaRatePxPerMs, 1e-9);
        cal.YRate.Should().BeApproximately(m.DecRatePxPerMs, 1e-9);
        cal.IsValid.Should().BeTrue();
        cal.HasDecCalibration.Should().BeTrue();
        cal.RaGuideParity.Should().Be(GuideParity.Even);
        cal.DecGuideParity.Should().Be(GuideParity.Even);
        cal.RaStepCount.Should().Be(9); // 2.8125 px per 750 ms step, 25 px distance
        cal.DecStepCount.Should().Be(9);
        p.Result.Should().BeSameAs(cal);
        last.Sanity!.Passed.Should().BeTrue();
    }

    [Test]
    public void CompletedCalibrationCarriesPointingAndEquipment()
    {
        var m = new KinematicMount { DeclinationDeg = 20.0, PierSide = PierSide.West, RotatorAngleDeg = 12.5, GuideRateRa = 0.5, GuideRateDec = 0.4 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        cal.Declination.Should().NotBeNull();
        MountTransform.Degrees(cal.Declination!.Value).Should().BeApproximately(20.0, 0.1);
        cal.PierSide.Should().Be(PierSide.West);
        cal.RotatorAngleDeg.Should().Be(12.5);
        cal.Binning.Should().Be(1);
        cal.GuideRateRa.Should().Be(0.5);
        cal.GuideRateDec.Should().Be(0.4);
        cal.PixelScale.Should().BeApproximately(2.0, 1e-9);
        cal.FocalLengthMm.Should().BeGreaterThan(0);
        cal.PixelSizeUm.Should().Be(3.8);
        cal.CalibrationStepMs.Should().Be(750);
        cal.CalibrationDistancePx.Should().Be(25);
        cal.Timestamp.Should().Be(new FixedClock().UtcNow);
        cal.RaSteps.Should().NotBeEmpty();
        cal.DecSteps.Should().NotBeEmpty();
        cal.DecSteps[0].Should().Be(new GuidePoint(0, 0));
    }

    [Test]
    public void CompletedCalibrationCarriesEveryStepPosition()
    {
        var m = new KinematicMount { DeclinationDeg = 10.0, DecBacklashMs = 1500 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        var dirs = cal.Points.Select(p => p.Direction).ToList();
        dirs[0].Should().Be("Start");
        dirs.Should().ContainInOrder("Start", "West", "East", "Backlash", "North", "South");
        // PHD2 also logs the West step that reaches the distance limit
        dirs.Count(d => d == "West").Should().BeGreaterThanOrEqualTo(cal.RaStepCount);
        var start = cal.Points[0];
        var lastWest = cal.Points.Last(p => p.Direction == "West");
        Math.Sqrt(Math.Pow(lastWest.X - start.X, 2) + Math.Pow(lastWest.Y - start.Y, 2)).Should().BeGreaterThanOrEqualTo(cal.CalibrationDistancePx);
        cal.Points.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void TheDecReturnRecordsHowTheBacklashIsTakenUp()
    {
        var m = new KinematicMount { DeclinationDeg = 10.0, DecBacklashMs = 1500 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        // the North leg ends with the gear engaged North: the South return first loses the 1500 ms of backlash
        cal.DecTakeUp.Should().HaveCountGreaterThanOrEqualTo(3);
        cal.DecTakeUp.Should().BeInAscendingOrder(p => p.PulseMs);
        foreach (var p in cal.DecTakeUp)
        {
            p.MovedPx.Should().BeApproximately(Math.Max(0, p.PulseMs - 1500) * m.DecRatePxPerMs, 1e-6);
        }

        var play = new NINA.GuideEngine.Algorithms.PlayModelBacklash();
        play.SeedFromCalibration(cal.DecTakeUp, cal.YRate).Should().BeTrue();
        play.PriorFullReversalMs.Should().BeApproximately(1500, 100);
    }

    [Test]
    public void WithoutBacklashTheDecReturnMovesFromTheFirstPulse()
    {
        var m = new KinematicMount { DeclinationDeg = 10.0 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        cal.DecTakeUp.Should().NotBeEmpty();
        cal.DecTakeUp[0].MovedPx.Should().BeApproximately(cal.DecTakeUp[0].PulseMs * m.DecRatePxPerMs, 1e-6);
    }

    [Test]
    public void OddParitiesFromMountCoordinates()
    {
        var m = new KinematicMount { OddRaParity = true, OddDecParity = true };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;
        cal.RaGuideParity.Should().Be(GuideParity.Odd);
        cal.DecGuideParity.Should().Be(GuideParity.Odd);
    }

    [Test]
    public void UnknownCoordinatesGiveUnknownParity()
    {
        var m = new KinematicMount { ReportCoordinates = false };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;
        cal.RaGuideParity.Should().Be(GuideParity.Unknown);
        cal.DecGuideParity.Should().Be(GuideParity.Unknown);
        cal.Declination.Should().BeNull();
    }

    [Test]
    public void RaRateScalesWithDeclination()
    {
        var m = new KinematicMount { DeclinationDeg = 60.0 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;
        cal.XRate.Should().BeApproximately(m.RaRatePxPerMs, 1e-9);
        (cal.XRate / cal.YRate).Should().BeApproximately(0.5, 1e-6);
        updates[^1].Sanity!.Passed.Should().BeTrue();
    }

    [Test]
    public void NoisyStarStillWithinTolerance()
    {
        var m = new KinematicMount { NoisePx = 0.15, Seed = 42, EastAngleDeg = 63, NorthAngleDeg = 153 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;
        CalibrationRunner.AngleDiffDeg(cal.XAngle, m.EastAngle).Should().BeLessThan(1.5);
        CalibrationRunner.AngleDiffDeg(cal.YAngle, m.NorthAngle).Should().BeLessThan(1.5);
        cal.XRate.Should().BeApproximately(m.RaRatePxPerMs, m.RaRatePxPerMs * 0.05);
        cal.YRate.Should().BeApproximately(m.DecRatePxPerMs, m.DecRatePxPerMs * 0.05);
    }

    [Test]
    public void NonOrthogonalAxesAreMeasured()
    {
        var m = new KinematicMount { EastAngleDeg = 10, NorthAngleDeg = 105 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;
        cal.OrthogonalityErrorDeg.Should().BeApproximately(5.0, 0.01);
    }

    [Test]
    public void AssumeOrthogonalForcesPerpendicularDecAxis()
    {
        var m = new KinematicMount { EastAngleDeg = 10, NorthAngleDeg = 105 };
        var (_, updates) = CalibrationRunner.Run(m, new CalibrationSettings { AssumeOrthogonal = true });
        CalibrationData cal = updates[^1].Result!;
        CalibrationRunner.AngleDiffDeg(cal.YAngle, MountTransform.Radians(100)).Should().BeLessThan(1e-9);
        cal.OrthogonalityErrorDeg.Should().BeLessThan(1e-9);

        // yRate is the projection of the measured north move onto the forced axis: cos(5°) of the true rate
        cal.YRate.Should().BeApproximately(m.DecRatePxPerMs * Math.Cos(MountTransform.Radians(5)), 1e-6);
    }

    [Test]
    public void DecBacklashIsClearedBeforeMeasuringDec()
    {
        var m = new KinematicMount { DecBacklashMs = 3000 };
        var (p, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        cal.YRate.Should().BeApproximately(m.DecRatePxPerMs, 1e-9);
        CalibrationRunner.AngleDiffDeg(cal.YAngle, m.NorthAngle).Should().BeLessThan(0.01);

        // 3000 ms backlash = 4 pulses without motion, then three accepted moves (the last one counts as North step 1)
        List<CalibrationStepInfo> backlash = updates.SelectMany(u => u.LoggedSteps).Where(s => s.Direction == "Backlash").ToList();
        backlash.Count.Should().Be(1 + 4 + 3);
        updates.Select(u => u.Status?.Message).Should().Contain("Clearing backlash step   7");
        p.StepsIssued.Should().Be(m.Pulses.Count);
    }

    [Test]
    public void BacklashClearingGivesUpButProceedsWhenStarMovedAtLeast3Px()
    {
        // 0.1 px per step: 3 consistent moves of >= 1.69 px never happen; after max(8, 60000/750)=80 pulses the star moved 8 px
        var m = new KinematicMount { GuideRateDec = 0.5 * 0.1 / 2.8125, ReportGuideRates = false };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationUpdate last = updates[^1];

        updates.Count(u => u.Status?.Message?.StartsWith("Clearing backlash", StringComparison.Ordinal) == true).Should().Be(80);
        updates.SelectMany(u => u.LoggedSteps).Count(s => s.Direction == "Backlash").Should().Be(81);

        // then the North leg fails after MAX_CALIBRATION_STEPS
        last.State.Should().Be(CalibrationState.Failed);
        last.ErrorCode.Should().Be(CalibrationErrorCode.DecStarDidNotMove);
        last.FailureReason.Should().Be("DEC Calibration Failed: star did not move enough");
    }

    [Test]
    public void BacklashClearingFailsWhenDecDoesNotMove()
    {
        var m = new KinematicMount { DecEfficiency = 0.0 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationUpdate last = updates[^1];
        last.State.Should().Be(CalibrationState.Failed);
        last.ErrorCode.Should().Be(CalibrationErrorCode.BacklashClearingFailed);
        last.FailureReason.Should().Be("Backlash Clearing Failed: star did not move enough");
        m.Pulses.Count(p => p.Direction == GuideDirection.North).Should().Be(80);
    }

    [Test]
    public void RaFailsAfterMaxStepsWhenStarDoesNotMove()
    {
        var m = new KinematicMount { WestEfficiency = 0.0 };
        var (p, updates) = CalibrationRunner.Run(m);
        CalibrationUpdate last = updates[^1];
        last.State.Should().Be(CalibrationState.Failed);
        last.IsFailed.Should().BeTrue();
        last.ErrorCode.Should().Be(CalibrationErrorCode.RaStarDidNotMove);
        last.FailureReason.Should().Be("RA Calibration Failed: star did not move enough");
        last.Pulses.Should().BeEmpty();

        // PHD2: if (m_calibrationSteps++ > MAX_CALIBRATION_STEPS) -> 61 pulses, failure on the 62nd frame
        m.Pulses.Should().HaveCount(61).And.OnlyContain(x => x.Direction == GuideDirection.West && x.DurationMs == 750);
        p.State.Should().Be(CalibrationState.Failed);
        FluentActions.Invoking(() => p.Update(m.Observe(), m.Snapshot())).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void LostStarFramesAreSkipped()
    {
        var reference = new KinematicMount();
        var (_, refUpdates) = CalibrationRunner.Run(reference);

        var m = new KinematicMount();
        var (_, updates) = CalibrationRunner.Run(m, lostFrame: f => f % 3 == 1);

        updates.Where(u => u.StarLost).Should().NotBeEmpty()
            .And.OnlyContain(u => u.Pulses.Count == 0 && u.LoggedSteps.Count == 0);
        updates[^1].Result.Should().BeEquivalentTo(refUpdates[^1].Result);
        m.Pulses.Should().Equal(reference.Pulses);
    }

    [Test]
    public void BeginFailsWithoutValidPosition()
    {
        var m = new KinematicMount();
        var p = new CalibrationProcess(new CalibrationSettings(), CalibrationRunner.Context(m));
        CalibrationUpdate u = p.Begin(GuidePoint.Invalid, m.Snapshot());
        u.IsFailed.Should().BeTrue();
        u.ErrorCode.Should().Be(CalibrationErrorCode.InvalidLockPosition);
    }

    [Test]
    public void DecOffCalibratesRaOnly()
    {
        var m = new KinematicMount();
        var (_, updates) = CalibrationRunner.Run(m, new CalibrationSettings { DecGuideMode = DecGuideMode.Off });
        CalibrationData cal = updates[^1].Result!;

        m.Pulses.Should().OnlyContain(x => x.Direction == GuideDirection.West || x.Direction == GuideDirection.East);
        cal.HasDecCalibration.Should().BeFalse();
        cal.YRate.Should().Be(0);
        cal.DecStepCount.Should().Be(0);
        cal.DecGuideParity.Should().Be(GuideParity.Unknown);
        CalibrationRunner.AngleDiffDeg(cal.YAngle, m.EastAngle + Math.PI / 2).Should().BeLessThan(1e-9);
        cal.IsValid.Should().BeTrue();

        // PHD2 finishes an RA-only calibration on the frame after the East leg
        updates[^2].State.Should().Be(CalibrationState.Complete);
        updates[^2].Result.Should().BeNull();
        updates[^1].Sanity!.Passed.Should().BeTrue();
    }

    [Test]
    public void FastRecenterUsesLargerEastAndSouthPulses()
    {
        var m = new KinematicMount();
        CalibrationRunner.Run(m);

        // recenter = floor(15 / 0.00375) = 4000 ms, clamped to max 2500 ms; 9 x 750 = 6750 ms -> 2500, 2500, 1750
        m.Pulses.Where(x => x.Direction == GuideDirection.East).Select(x => x.DurationMs).Should().Equal(2500, 2500, 1750);

        // south: floor(0.8 * 15 / 0.00375) = 3200 -> 2500; 9 north steps -> 2500, 2500, 1750
        m.Pulses.Where(x => x.Direction == GuideDirection.South).Take(3).Select(x => x.DurationMs).Should().Equal(2500, 2500, 1750);

        var slow = new KinematicMount();
        CalibrationRunner.Run(slow, new CalibrationSettings { FastRecenter = false });
        slow.Pulses.Count(x => x.Direction == GuideDirection.East).Should().Be(9);
        slow.Pulses.Where(x => x.Direction == GuideDirection.East).Should().OnlyContain(x => x.DurationMs == 750);
    }

    [Test]
    public void FastRecenterNeverShorterThanCalibrationStep()
    {
        var m = new KinematicMount { GuideRateRa = 2.0, GuideRateDec = 2.0, PixelScale = 1.0 }; // fast: 0.03 px/ms
        CalibrationRunner.Run(m, new CalibrationSettings { StepMs = 750, MaxMovePixels = 15 });

        // floor(15 / 0.03) = 500 < 750 -> 750
        m.Pulses.Where(x => x.Direction == GuideDirection.East).Should().OnlyContain(x => x.DurationMs == 750);
    }

    [Test]
    public void NudgeSouthReturnsStarNearStartingPoint()
    {
        var m = new KinematicMount { EastAngleDeg = 20, NorthAngleDeg = 110 };
        GuidePoint start = m.Position;
        var (_, updates) = CalibrationRunner.Run(m);

        updates.Select(u => u.Status).Where(s => s?.Direction == "NudgeSouth").Should().NotBeEmpty();
        m.Position.Distance(start).Should().BeLessThanOrEqualTo(CalibrationProcess.NudgeTolerance + 0.01);
        updates.Select(u => u.Status?.Message).Should().Contain("Nudge South   1");
    }

    [Test]
    public void NudgesAreLimitedLikePhd2()
    {
        // Large Dec backlash: south return pulses are partly absorbed so the star ends far north of the start
        var m = new KinematicMount { DecBacklashMs = 2400 };
        var (_, updates) = CalibrationRunner.Run(m);
        int nudges = updates.Count(u => u.Status?.Direction == "NudgeSouth");
        nudges.Should().BeLessThanOrEqualTo(CalibrationProcess.MaxNudges + 1); // PHD2 tests steps <= MAX_NUDGES before incrementing
        m.Pulses.Where(x => x.Direction == GuideDirection.South).Skip(3).Should().OnlyContain(x => x.DurationMs <= 750);
        updates[^1].IsComplete.Should().BeTrue();
    }

    [Test]
    public void St4EastAdvisoryWhenEastDoesNotMove()
    {
        var m = new KinematicMount { EastEfficiency = 0.0 };
        var (_, updates) = CalibrationRunner.Run(m, new CalibrationSettings { IsSt4 = true });
        updates.SelectMany(u => u.Advisories).Should().Contain(a => a.Code == "CAL_NO_EAST_MOVEMENT" && a.ShowToUser);

        var pulseGuided = new KinematicMount { EastEfficiency = 0.0 };
        var (_, updates2) = CalibrationRunner.Run(pulseGuided, new CalibrationSettings { IsSt4 = false });
        updates2.SelectMany(u => u.Advisories).Should().NotContain(a => a.Code == "CAL_NO_EAST_MOVEMENT");

        var fine = new KinematicMount();
        var (_, updates3) = CalibrationRunner.Run(fine, new CalibrationSettings { IsSt4 = true });
        updates3.SelectMany(u => u.Advisories).Should().BeEmpty();
    }

    [Test]
    public void SouthAdvisoryIsDebugOnly()
    {
        var m = new KinematicMount { DecBacklashMs = 6500 };
        var (_, updates) = CalibrationRunner.Run(m, new CalibrationSettings { IsSt4 = true });
        CalibrationAdvisory south = updates.SelectMany(u => u.Advisories).Single(a => a.Code == "CAL_LITTLE_SOUTH_MOVEMENT");
        south.ShowToUser.Should().BeFalse();
        south.Message.Should().Contain("faulty guide cable");
    }

    [Test]
    public void StepInfoMatchesPhd2Events()
    {
        var m = new KinematicMount { EastAngleDeg = 0, NorthAngleDeg = 90 };
        var (_, updates) = CalibrationRunner.Run(m);

        CalibrationUpdate first = updates[1];
        first.State.Should().Be(CalibrationState.GoWest);
        first.Pulses.Should().Equal(new PulseCommand(GuideDirection.West, 750));
        first.LoggedSteps.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CalibrationStepInfo("West", 0, 0, 0, new GuidePoint(500, 400), 0));
        first.Status!.Direction.Should().Be("West");
        first.Status.StepNumber.Should().Be(0); // PHD2 builds the step info before incrementing the step counter
        first.Status.Message.Should().Be("West step   1, dist= 0.0");

        // second frame: star moved 2.8125 px west (-x), dx = start - current = +2.8125
        CalibrationStepInfo second = updates[2].Status!;
        second.Dx.Should().BeApproximately(2.8125, 1e-9);
        second.Dy.Should().BeApproximately(0, 1e-9);
        second.Distance.Should().BeApproximately(2.8125, 1e-9);
        second.Message.Should().Be("West step   2, dist= 2.8");

        // West completes and falls through to East in the same frame
        CalibrationUpdate westDone = updates.First(u => u.DirectionsCompleted.Count > 0);
        westDone.DirectionsCompleted.Single().Direction.Should().Be("West");
        westDone.LoggedSteps.Select(s => s.Direction).Should().Equal("West", "East");
        westDone.Status!.Message.Should().Be("East step   3, dist=25.3");
        westDone.Pulses.Should().Equal(new PulseCommand(GuideDirection.East, 2500));

        updates.Where(u => u.DirectionsCompleted.Count > 0).SelectMany(u => u.DirectionsCompleted).Select(d => d.Direction)
            .Should().Equal("West", "North");
    }

    [Test]
    public void ProgressEstimateIsMonotonicAndExactAtEnd()
    {
        var m = new KinematicMount { DecBacklashMs = 1500 };
        var (p, updates) = CalibrationRunner.Run(m);

        updates.Should().OnlyContain(u => u.EstimatedTotalSteps >= u.StepsIssued);
        updates[0].EstimatedTotalSteps.Should().BeGreaterThan(20);
        updates[^1].StepsIssued.Should().Be(m.Pulses.Count);
        updates[^1].EstimatedTotalSteps.Should().Be(updates[^1].StepsIssued);
        p.StepsIssued.Should().Be(m.Pulses.Count);

        // estimate from guide rate / scale is accurate for an ideal mount
        int total = m.Pulses.Count;
        Math.Abs(updates[0].EstimatedTotalSteps - total).Should().BeLessThanOrEqualTo(4);
    }

    [Test]
    public void StepSizeRecomputedWhenGuideRateChanged()
    {
        var previous = new CalibrationData { XAngle = 0, YAngle = Math.PI / 2, XRate = 0.005, YRate = 0.005, GuideRateRa = 1.0, GuideRateDec = 1.0, Binning = 1 };
        var m = new KinematicMount { GuideRateRa = 0.5, GuideRateDec = 0.5 };
        var ctx = CalibrationRunner.Context(m, previous);
        var (p, updates) = CalibrationRunner.Run(m, new CalibrationSettings { StepMs = 400 }, ctx);

        // distance 25 px * 2"/px / (15 * 0.5) = 6.667 s / 12 = 555.6 ms -> 600 ms
        p.StepCheck!.StepChanged.Should().BeTrue();
        p.StepCheck.Reason.Should().Be("mount guide speed");
        p.StepMs.Should().Be(600);
        m.Pulses[0].DurationMs.Should().Be(600);
        updates[^1].Result!.CalibrationStepMs.Should().Be(600);
    }

    [Test]
    public void DifferentFromPreviousCalibrationIsFlagged()
    {
        var m = new KinematicMount();
        var previous = new CalibrationData
        {
            XAngle = m.EastAngle, YAngle = m.NorthAngle, XRate = m.RaRatePxPerMs, YRate = m.DecRatePxPerMs * 2,
            PixelScale = 2.0, Binning = 1, GuideRateRa = 0.5, GuideRateDec = 0.5,
        };
        var (_, updates) = CalibrationRunner.Run(m, context: CalibrationRunner.Context(m, previous));
        updates[^1].Sanity!.ReportedType.Should().Be(CalibrationIssueType.Different);
        updates[^1].Result!.LastIssue.Should().Be(CalibrationIssueType.Different);
    }
}
