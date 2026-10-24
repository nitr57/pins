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
        bool decFlip = false, double rate = 0.5) => new()
    {
        Mount = new MountSnapshot { IsConnected = true, DeclinationDeg = dec, PierSide = side, GuideRateRa = rate, GuideRateDec = rate },
        Binning = bin,
        PixelSizeUm = 3.8,
        RotatorAngleDeg = rot,
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

        // tiny delta ignored
        CalibrationAdjuster.AdjustForScopePointing(Cal() with { RotatorAngleDeg = 10 }, Pointing(rot: 10.04)).Calibration.XAngle
            .Should().Be(Cal().XAngle);

        // unknown at calibration time
        CalibrationAdjustment c = CalibrationAdjuster.AdjustForScopePointing(Cal(), Pointing(rot: 40));
        c.Alerts.Should().ContainSingle(x => x.Type == CalibrationAlertType.RotatorPositionUnknown);
        c.Calibration.RotatorAngleDeg.Should().Be(40);
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
