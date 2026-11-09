// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class CalibrationAdjusterTests
{
    private static CalibrationData Cal() => new()
    {
        XAngle = MountTransform.Radians(30),
        YAngle = MountTransform.Radians(120),
        XRate = 0.004,
        YRate = 0.005,
        Declination = MountTransform.Radians(10),
        PierSide = PierSide.East,
        Binning = 1,
        RaGuideParity = GuideParity.Even,
        DecGuideParity = GuideParity.Even,
        GuideRateRa = 0.5,
        GuideRateDec = 0.5,
        PixelScale = 2.0,
        PixelSizeUm = 3.8,
        FocalLengthMm = 391.9,
    };

    private static ScopePointing Pointing(double dec = 10, PierSide side = PierSide.East, double? rot = null, int bin = 1,
        bool decFlip = false, double rate = 0.5, RotatorGuideCamera turns = RotatorGuideCamera.Yes) => new()
    {
        Mount = new MountSnapshot { IsConnected = true, DeclinationDeg = dec, PierSide = side, GuideRateRa = rate, GuideRateDec = rate },
        Binning = bin,
        PixelSizeUm = 3.8,
        RotatorAngleDeg = rot,
        RotatorTurnsGuideCamera = turns,
        DecFlipRequired = decFlip,
    };

    [Test]
    public void UnchangedPointingKeepsCalibration()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing());
        a.IsValid.Should().BeTrue();
        a.Flipped.Should().BeFalse();
        a.DecCompensated.Should().BeFalse();
        a.Alerts.Should().BeEmpty();
        a.Calibration.Should().Be(Cal());
        a.EffectiveXRate.Should().Be(0.004);
        a.CalibrationChanged.Should().BeFalse();
    }

    [Test]
    public void FlipWithoutDecFlip()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(side: PierSide.West));
        a.Flipped.Should().BeTrue();
        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(210)).Should().BeLessThan(1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.YAngle, MountTransform.Radians(120)).Should().BeLessThan(1e-9);
        a.Calibration.PierSide.Should().Be(PierSide.West);
        a.Calibration.DecGuideParity.Should().Be(GuideParity.Odd);
        a.Calibration.RaGuideParity.Should().Be(GuideParity.Even);
        a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.Flipped);
        a.CalibrationChanged.Should().BeTrue();
    }

    [Test]
    public void FlipWithDecFlip()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(side: PierSide.West, decFlip: true));
        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(210)).Should().BeLessThan(1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.YAngle, MountTransform.Radians(300)).Should().BeLessThan(1e-9);
        a.Calibration.DecGuideParity.Should().Be(GuideParity.Even);
        a.Calibration.XAngle.Should().BeInRange(-Math.PI, Math.PI);
        a.Calibration.YAngle.Should().BeInRange(-Math.PI, Math.PI);
    }

    [Test]
    public void FlippedCalibrationGuidesCorrectlyOnGermanMount()
    {
        // A GEM after a meridian flip: the camera turns 180°, so the East vector rotates by 180°; with Dec reversed
        // by the flip (typical ASCOM/INDI behaviour) the North vector stays where it was.
        var m = new KinematicMount { EastAngleDeg = 30, NorthAngleDeg = 120 };
        var (_, updates) = CalibrationRunner.Run(m);
        CalibrationData cal = updates[^1].Result!;

        var flipped = new KinematicMount { EastAngleDeg = 210, NorthAngleDeg = 120, PierSide = PierSide.West };
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(cal, Pointing(side: PierSide.West) with
        {
            Mount = flipped.Snapshot() with { DeclinationDeg = 0 },
        });
        var t = new MountTransform(a.Effective);
        GuidePoint lockPos = flipped.Position;
        flipped.Position = new GuidePoint(lockPos.X - 2, lockPos.Y + 3);
        GuidePoint mnt = t.CameraToMount(flipped.Position - lockPos);
        flipped.Apply(t.RaPulse(mnt.X));
        flipped.Apply(t.DecPulse(mnt.Y));
        flipped.Position.Distance(lockPos).Should().BeLessThan(0.01);
    }

    [Test]
    public void DeclinationCompensation()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(dec: 50));
        a.DecCompensated.Should().BeTrue();
        a.EffectiveXRate.Should().BeApproximately(0.004 / Math.Cos(MountTransform.Radians(10)) * Math.Cos(MountTransform.Radians(50)), 1e-12);
        a.Calibration.XRate.Should().Be(0.004, "the dec-compensated rate is never persisted");
        a.Effective.XRate.Should().Be(a.EffectiveXRate);
    }

    [Test]
    public void DeclinationCompensationClampsAt89()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(dec: 89.9));
        a.EffectiveXRate.Should().BeApproximately(0.004 / Math.Cos(MountTransform.Radians(10)) * Math.Cos(MountTransform.Radians(89)), 1e-12);
    }

    [Test]
    public void DeclinationCompensationSkippedAboveLimit()
    {
        CalibrationData cal = Cal() with { Declination = MountTransform.Radians(65) };
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(cal, Pointing(dec: 20));
        a.DecCompensated.Should().BeFalse();
        a.EffectiveXRate.Should().Be(0.004);
        a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.CalibrationTooFarFromEquator);

        // disabled: no alert, no compensation
        CalibrationAdjustment b = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(dec: 50) with { DecCompensationEnabled = false });
        b.DecCompensated.Should().BeFalse();
        b.Alerts.Should().BeEmpty();

        // unknown declination
        CalibrationAdjustment c = CalibrationAdjuster.AdjustForScopePointing(Cal() with { Declination = null }, Pointing(dec: 50));
        c.DecCompensated.Should().BeFalse();
    }

    [Test]
    public void BinningChangeRescalesRates()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(bin: 2));
        a.IsValid.Should().BeTrue();
        a.Calibration.Binning.Should().Be(2);
        a.Calibration.XRate.Should().BeApproximately(0.002, 1e-12);
        a.Calibration.YRate.Should().BeApproximately(0.0025, 1e-12);
        a.Calibration.PixelScale.Should().BeApproximately(4.0, 1e-12);
        a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.BinningChanged);

        CalibrationAdjustment b = CalibrationAdjuster.AdjustForScopePointing(Cal() with { Binning = 2 }, Pointing(bin: 1));
        b.Calibration.XRate.Should().BeApproximately(0.008, 1e-12);
    }

    [Test]
    public void PixelSizeChangeInvalidates()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing() with { PixelSizeUm = 5.2 });
        a.IsValid.Should().BeFalse();
        a.Alerts.Should().Contain(x => x.Type == CalibrationAlertType.PixelSizeChanged);

        CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing() with { PixelSizeUm = 4.5 }).IsValid.Should().BeTrue();
    }

    [Test]
    public void GuideSpeedChangeAlerts()
    {
        CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rate: 0.75)).Alerts
            .Should().ContainSingle(x => x.Type == CalibrationAlertType.GuideSpeedChanged);
        CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rate: 0.52)).Alerts.Should().BeEmpty();
        CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rate: 0.75) with { CanReportPosition = false }).Alerts.Should().BeEmpty();
    }

    [Test]
    public void MissingPierSideAlerts()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { PierSide = PierSide.Unknown }, Pointing(side: PierSide.West));
        a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.NoPierSideInformation);
        a.Flipped.Should().BeFalse();
    }

    [Test]
    public void RotatorDeltaRotatesAngles()
    {
        CalibrationAdjustment a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 40));
        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(0)).Should().BeLessThan(1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.YAngle, MountTransform.Radians(90)).Should().BeLessThan(1e-9);
        a.Calibration.RotatorAngleDeg.Should().Be(40);
        a.CalibrationChanged.Should().BeTrue();
        a.RotatorTurn.Should().Be(new RotatorTurn(10, 40, -30));
        a.Alerts.Should().OnlyContain(x => !x.ShowToUser, "the guider tells the user, together with the new guide star");

        // tiny delta ignored
        var tiny = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 10.04));
        tiny.Calibration.XAngle.Should().Be(Cal().XAngle);
        tiny.RotatorTurn.Should().BeNull();
    }

    [Test]
    public void ReversedRotatorTurnsTheCalibrationTheOtherWay()
    {
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 40, turns: RotatorGuideCamera.Reversed));

        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(60)).Should().BeLessThan(1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.YAngle, MountTransform.Radians(150)).Should().BeLessThan(1e-9);
        a.Calibration.RotatorAngleDeg.Should().Be(40);
    }

    [Test]
    public void RotatorDeltaTakesTheShorterWayAroundZero()
    {
        // the mechanical angle wraps: 350° to 10° is a move of +20°, not -340°
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 350 }, Pointing(rot: 10));

        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(10)).Should().BeLessThan(1e-9);
        a.RotatorTurn!.CalibrationTurnDeg.Should().BeApproximately(-20, 1e-9);
    }

    [Test]
    public void RotatorThatDoesNotTurnTheGuideCameraKeepsTheAnglesAndFollowsTheRotator()
    {
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 40, turns: RotatorGuideCamera.No));

        a.Calibration.XAngle.Should().Be(Cal().XAngle);
        a.Calibration.YAngle.Should().Be(Cal().YAngle);
        a.Calibration.RotatorAngleDeg.Should().Be(40, "saying later that the guide camera turns starts from where the rotator is");
        a.CalibrationChanged.Should().BeTrue();
        a.RotatorTurn.Should().BeNull();
        a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.RotatorFollowed).Which.ShowToUser.Should().BeFalse();
    }

    [Test]
    public void RotatorJitterLeavesACalibrationThatDoesNotTurnAlone()
    {
        // within the tolerance the calibration is not changed, so it is not stored again at every start
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 10.04, turns: RotatorGuideCamera.No));

        a.Calibration.Should().Be(Cal() with { RotatorAngleDeg = 10 });
        a.CalibrationChanged.Should().BeFalse();
        a.Alerts.Should().BeEmpty();

        // also across 0°
        var wrap = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 359.98 }, Pointing(rot: 0.02, turns: RotatorGuideCamera.No));
        wrap.CalibrationChanged.Should().BeFalse();
    }

    [TestCase(RotatorGuideCamera.No, false)]
    [TestCase(RotatorGuideCamera.Yes, true)]
    [TestCase(RotatorGuideCamera.Reversed, true)]
    public void CalibrationWithoutRotatorAngleTakesTheCurrentOne(RotatorGuideCamera turns, bool shown)
    {
        // Deviation from PHD2, which asks for a new calibration. When the rotator turns the guide camera, a turn since the
        // calibration is not known, so the user is told
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rot: 40, turns: turns));

        a.IsValid.Should().BeTrue();
        a.Calibration.XAngle.Should().Be(Cal().XAngle);
        a.Calibration.RotatorAngleDeg.Should().Be(40);
        a.CalibrationChanged.Should().BeTrue();
        a.RotatorTurn.Should().BeNull();
        var alert = a.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.RotatorPositionUnknown).Subject;
        alert.ShowToUser.Should().Be(shown);
        alert.Message.Contains("Calibrate again").Should().Be(shown);
    }

    [Test]
    public void CalibrationWithoutRotatorAngleAndNoRotatorStaysWithout()
    {
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rot: null));

        a.Calibration.RotatorAngleDeg.Should().BeNull();
        a.CalibrationChanged.Should().BeFalse();
        a.Alerts.Should().BeEmpty();
    }

    [Test]
    public void FlipAndHalfATurnOfTheRotatorAddUp()
    {
        // after a meridian flip the rotator turned by 180° (e.g. NINA's "rotate image after flip"): the flip turns RA by
        // 180°, the rotator both angles by 180°
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 0 }, Pointing(side: PierSide.West, rot: 180));

        a.Flipped.Should().BeTrue();
        Math.Abs(a.RotatorTurn!.CalibrationTurnDeg).Should().BeApproximately(180, 1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.XAngle, MountTransform.Radians(30)).Should().BeLessThan(1e-9);
        CalibrationRunner.AngleDiffDeg(a.Calibration.YAngle, MountTransform.Radians(300)).Should().BeLessThan(1e-9);
        a.Calibration.RotatorAngleDeg.Should().Be(180);
    }

    [Test]
    public void NoRotatorLeavesTheCalibrationAlone()
    {
        var a = CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: null));

        a.Calibration.Should().Be(Cal() with { RotatorAngleDeg = 10 });
        a.CalibrationChanged.Should().BeFalse();
        a.Alerts.Should().BeEmpty();
    }

    [Test]
    public void InvertDecFlipsYAngleAndParity()
    {
        CalibrationData inv = CalibrationAdjuster.InvertDec(Cal());
        CalibrationRunner.AngleDiffDeg(inv.YAngle, MountTransform.Radians(300)).Should().BeLessThan(1e-9);
        inv.XAngle.Should().Be(Cal().XAngle);
        inv.DecGuideParity.Should().Be(GuideParity.Odd);
    }
}
