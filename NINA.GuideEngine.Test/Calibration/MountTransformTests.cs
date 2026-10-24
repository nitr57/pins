// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class MountTransformTests
{
    private static IEnumerable<TestCaseData> Geometries()
    {
        for (int i = -12; i < 12; i += 3)
        {
            double x = i * 15.0;
            yield return new TestCaseData(x, x + 90.0).SetName($"Normal_x{x}");
            yield return new TestCaseData(x, x - 90.0).SetName($"Mirrored_x{x}");
        }
    }

    [TestCaseSource(nameof(Geometries))]
    public void RoundTripAndAxes(double xDeg, double yDeg)
    {
        var t = new MountTransform(MountTransform.Radians(xDeg), MountTransform.Radians(yDeg), 0.004, 0.003);

        // camera vector along the RA axis -> pure mount X, along the Dec axis -> pure mount Y
        GuidePoint ra = t.CameraToMount(new GuidePoint(10 * Math.Cos(t.XAngle), 10 * Math.Sin(t.XAngle)));
        ra.X.Should().BeApproximately(10, 1e-9);
        ra.Y.Should().BeApproximately(0, 1e-9);
        GuidePoint dec = t.CameraToMount(new GuidePoint(5 * Math.Cos(t.YAngle), 5 * Math.Sin(t.YAngle)));
        dec.X.Should().BeApproximately(0, 1e-9);
        dec.Y.Should().BeApproximately(5, 1e-9);

        for (double a = -3; a < 3; a += 0.7)
        {
            var cam = new GuidePoint(3 * Math.Cos(a), 3 * Math.Sin(a) - 1.2);
            GuidePoint back = t.MountToCamera(t.CameraToMount(cam));
            back.X.Should().BeApproximately(cam.X, 1e-9);
            back.Y.Should().BeApproximately(cam.Y, 1e-9);
        }
    }

    [Test]
    public void NonOrthogonalAxesProjectLikePhd2()
    {
        // yAngleError = -5 deg; PHD2 computes y = hyp * sin(theta - (xAngle + yAngleError)), faithfully reproduced
        var t = new MountTransform(0.0, MountTransform.Radians(95), 0.004, 0.004);
        t.YAngleError.Should().BeApproximately(MountTransform.Radians(-5), 1e-12);
        GuidePoint m = t.CameraToMount(new GuidePoint(Math.Cos(MountTransform.Radians(95)), Math.Sin(MountTransform.Radians(95))));
        m.Y.Should().BeApproximately(Math.Sin(MountTransform.Radians(100)), 1e-9);
        m.X.Should().BeApproximately(Math.Cos(MountTransform.Radians(95)), 1e-9);
    }

    [Test]
    public void InvalidInputGivesInvalidOutput()
    {
        var t = new MountTransform(0, Math.PI / 2, 1, 1);
        t.CameraToMount(GuidePoint.Invalid).IsValid.Should().BeFalse();
        t.MountToCamera(GuidePoint.Invalid).IsValid.Should().BeFalse();
    }

    [Test]
    public void PulsesUsePhd2DirectionsAndRounding()
    {
        var cal = new CalibrationData { XAngle = 0, YAngle = Math.PI / 2, XRate = 0.004, YRate = 0.002 };
        var t = new MountTransform(cal);
        t.RaPulse(1.0).Should().Be(new PulseCommand(GuideDirection.West, 250));
        t.RaPulse(-1.0).Should().Be(new PulseCommand(GuideDirection.East, 250));
        t.DecPulse(0.5).Should().Be(new PulseCommand(GuideDirection.South, 250));
        t.DecPulse(-0.5).Should().Be(new PulseCommand(GuideDirection.North, 250));
        MountTransform.PulseMs(0.001, 0.004).Should().Be(0);
        MountTransform.PulseMs(0.0019, 0.001).Should().Be(2); // ROUND = floor(x + 0.5)
        MountTransform.PulseMs(1, 0).Should().Be(0);
        MountTransform.PulseDistancePx(250, 0.004).Should().BeApproximately(1.0, 1e-12);

        new MountTransform(cal, effectiveXRate: 0.002).RaPulse(1.0).DurationMs.Should().Be(500);
    }

    [Test]
    public void CorrectionsMoveStarBackInKinematicModel()
    {
        // pulses derived from the transform must reduce the offset in the kinematic model for any geometry
        foreach ((double e, double n) in new[] { (30.0, 120.0), (30.0, -60.0), (-150.0, -60.0), (200.0, 110.0) })
        {
            var m = new KinematicMount { EastAngleDeg = e, NorthAngleDeg = n };
            var (_, updates) = CalibrationRunner.Run(m);
            var t = new MountTransform(updates[^1].Result!);
            var lockPos = m.Position;
            m.Position = new GuidePoint(lockPos.X + 4, lockPos.Y - 3);

            GuidePoint mount = t.CameraToMount(m.Position - lockPos);
            m.Apply(t.RaPulse(mount.X));
            m.Apply(t.DecPulse(mount.Y));
            m.Position.Distance(lockPos).Should().BeLessThan(0.01, $"geometry {e}/{n}");
        }
    }

    [Test]
    public void NormAngleRange()
    {
        MountTransform.NormAngle(Math.PI).Should().BeApproximately(-Math.PI, 1e-12);
        MountTransform.NormAngle(3 * Math.PI / 2).Should().BeApproximately(-Math.PI / 2, 1e-12);
        MountTransform.NormAngle(-Math.PI).Should().BeApproximately(-Math.PI, 1e-12);
        MountTransform.OrthogonalityErrorDegrees(0, MountTransform.Radians(-80)).Should().BeApproximately(10, 1e-9);
    }
}
