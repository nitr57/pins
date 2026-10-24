// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class BacklashCompensationTests
{
    private const double YRate = 0.01; // 1 px = 100 ms
    private const double MinMove = 0.2;

    private static BacklashCompensation ReversedNorth(int pulse, int floor, int ceiling)
    {
        var blc = new BacklashCompensation(pulse, floor, ceiling, enabled: true);
        int req = 100;
        blc.Apply(MoveOptions.GuideStep, 1.0, ref req).Should().Be(0); // first move: South, no reversal
        req = 100;
        blc.Apply(MoveOptions.GuideStep, -1.0, ref req).Should().Be(pulse); // reversal to North
        req.Should().Be(100 + pulse);
        blc.TrackingWindowOpen.Should().BeTrue();
        return blc;
    }

    [Test]
    public void DefaultCeilingAndFloor()
    {
        var blc = new BacklashCompensation(400, 0, 0, enabled: true);
        blc.FloorMs.Should().Be(20);
        blc.CeilingMs.Should().Be(600);
        blc.IsFixedSize.Should().BeFalse();
        new BacklashCompensation(500, 495, 505, true).IsFixedSize.Should().BeTrue();
        new BacklashCompensation(0, 0, 0, true).IsEnabled.Should().BeFalse("pulse 0 disables BLC on load");
    }

    [Test]
    public void UndershootIncreasesPulseByAtMostTenPercent()
    {
        var blc = ReversedNorth(500, 20, 1000);

        // Still displaced north (same direction as last move) -> under-shoot of 0.8 px
        blc.TrackResults(MoveOptions.GuideStep, -0.8, MinMove, YRate);
        blc.PulseWidthMs.Should().Be(500, "needs two follow-on deflections");
        int req = 80;
        blc.Apply(MoveOptions.GuideStep, -0.8, ref req).Should().Be(0);

        blc.TrackResults(MoveOptions.GuideStep, -0.6, MinMove, YRate);

        // avg initial miss 0.8 px -> +80 ms nominal, limited to +10 % -> 550
        blc.PulseWidthMs.Should().Be(550);
        blc.AdjustmentCount.Should().Be(1);
        blc.TrackingWindowOpen.Should().BeFalse();
    }

    [Test]
    public void IncreaseIsBoundedByCeiling()
    {
        var blc = ReversedNorth(500, 20, 520);
        blc.TrackResults(MoveOptions.GuideStep, -0.8, MinMove, YRate);
        blc.TrackResults(MoveOptions.GuideStep, -0.6, MinMove, YRate);
        blc.PulseWidthMs.Should().Be(520);
    }

    [Test]
    public void OvershootDecreasesPulse()
    {
        var blc = ReversedNorth(500, 20, 1000);

        // Star now displaced south: we overshot by 0.5 px -> -50 ms nominal, bounded at -20 % (400)
        blc.TrackResults(MoveOptions.GuideStep, 0.5, MinMove, YRate);
        blc.PulseWidthMs.Should().Be(450);
        blc.History.Should().ContainSingle().Which.InitialOvershoot.Should().BeTrue();
    }

    [Test]
    public void DecreaseIsBoundedByTwentyPercentAndFloor()
    {
        var blc = ReversedNorth(500, 20, 1000);
        blc.TrackResults(MoveOptions.GuideStep, 3.0, MinMove, YRate); // -300 nominal -> 400 (−20 %)
        blc.PulseWidthMs.Should().Be(400);

        var floored = ReversedNorth(500, 480, 1000);
        floored.TrackResults(MoveOptions.GuideStep, 3.0, MinMove, YRate);
        floored.PulseWidthMs.Should().Be(480);
    }

    [Test]
    public void FixedSizeNeverAdapts()
    {
        var blc = ReversedNorth(500, 495, 505);
        blc.TrackResults(MoveOptions.GuideStep, 3.0, MinMove, YRate);
        blc.PulseWidthMs.Should().Be(500);
    }

    [Test]
    public void CalibrationMoveResetsDirection()
    {
        var blc = new BacklashCompensation(500, 20, 1000, enabled: true);
        int req = 100;
        blc.Apply(MoveOptions.GuideStep, 1.0, ref req);
        blc.TrackResults(MoveOptions.CalibrationMove, 0.3, MinMove, YRate);
        blc.LastDirection.Should().BeNull();
        req = 100;
        blc.Apply(MoveOptions.GuideStep, -1.0, ref req).Should().Be(0, "no previous direction");
    }

    [Test]
    public void RecoveryMoveCompensatesButDoesNotTrack()
    {
        var blc = new BacklashCompensation(500, 20, 1000, enabled: true);
        int req = 100;
        blc.Apply(MoveOptions.GuideStep, 1.0, ref req);
        req = 100;
        blc.Apply(MoveOptions.RecoveryMove, -1.0, ref req).Should().Be(500);
        blc.TrackingWindowOpen.Should().BeFalse();
        blc.History.Should().BeEmpty();
    }

    [Test]
    public void ZeroDistanceOrDisabledDoesNothing()
    {
        var blc = new BacklashCompensation(500, 20, 1000, enabled: true);
        int req = 0;
        blc.Apply(MoveOptions.GuideStep, 0.0, ref req).Should().Be(0);
        blc.LastDirection.Should().BeNull();
        blc.Enable(false);
        req = 100;
        blc.Apply(MoveOptions.GuideStep, 1.0, ref req).Should().Be(0);
        blc.LastDirection.Should().BeNull();
    }

    [Test]
    public void RaisesLimiterMaxDecDurationWhenPulseExceedsIt()
    {
        var limiter = new PulseLimiter();
        limiter.SetMaxDecDuration(1000);
        var blc = new BacklashCompensation(1500, 20, 2000, enabled: true, limiter: limiter);
        blc.PulseWidthMs.Should().Be(1500);
        limiter.MaxDecDurationMs.Should().Be(1500);
    }

    [Test]
    public void LastDirectionUsesPhd2Convention()
    {
        var blc = new BacklashCompensation(500, 20, 1000, enabled: true);
        int req = 10;
        blc.Apply(MoveOptions.GuideStep, 0.5, ref req);
        blc.LastDirection.Should().Be(GuideDirection.South);
    }
}
