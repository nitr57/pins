// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.Test.Stars;

[TestFixture]
public class MassCheckerTests
{
    private static MassChecker Filled(double mass, int n, long t0 = 0, long step = 2000)
    {
        var m = new MassChecker();
        m.SetExposure(2000, false);
        for (int i = 0; i < n; i++)
            m.AppendData(mass, t0 + i * step);
        return m;
    }

    [Test]
    public void NeedsFiveSamples()
    {
        var m = Filled(1000, 4);
        m.CheckMass(10, 0.5, out _).Should().BeFalse();
        m.AppendData(1000, 8000);
        m.CheckMass(10, 0.5, out _).Should().BeTrue();
    }

    [Test]
    public void RejectsDropAndSpike_AcceptsModerateChange()
    {
        var m = Filled(1000, 10);
        m.CheckMass(1000, 0.5, out var lim).Should().BeFalse();
        lim.Median.Should().Be(1000);
        lim.High.Should().Be(1500);
        lim.Spike.Should().Be(2000);
        m.CheckMass(700, 0.5, out _).Should().BeFalse();
        m.CheckMass(400, 0.5, out _).Should().BeTrue("below low water mark * 0.5");
        m.CheckMass(1600, 0.5, out _).Should().BeTrue("above high water mark * 1.5");
    }

    [Test]
    public void OldSamplesLeaveTheWindow()
    {
        var m = Filled(1000, 10, 0, 1000);
        m.Count.Should().Be(10);
        // 45 s window (2 x 22.5 s): at t = 60 s everything before 15 s is dropped
        m.AppendData(1000, 60000);
        m.Count.Should().Be(1);
    }

    [Test]
    public void ExposureChangeResets_UnlessAutoExposure()
    {
        var m = Filled(1000, 10);
        m.SetExposure(3000, false);
        m.Count.Should().Be(0);

        var a = new MassChecker();
        a.SetExposure(1000, true);
        for (int i = 0; i < 10; i++) a.AppendData(1000, i * 1000);
        a.SetExposure(2000, true);
        a.Count.Should().Be(10);
        // normalised by exposure: 2000 at 2 s equals 1000 at 1 s
        a.CheckMass(2000, 0.5, out _).Should().BeFalse();
        a.CheckMass(500, 0.5, out var lim).Should().BeTrue();
        lim.Median.Should().Be(2000, "limits are converted back to mass units when rejecting");
    }

    [Test]
    public void LowWaterMarkFollowsMedianUpwards()
    {
        var m = Filled(400, 10);
        m.CheckMass(400, 0.5, out _);
        for (int i = 0; i < 40; i++)
            m.AppendData(1000, 20000 + i * 2000);
        MassLimits lim = default;
        for (int i = 0; i < 30; i++)
            m.CheckMass(1000, 0.5, out lim);
        lim.Low.Should().BeGreaterThan(0.5 * 400 * 1.5, "low water mark drifts 5 % per check towards the median");
    }
}
