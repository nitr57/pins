// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class PulseLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public void ClampsToMaxAndFlagsLimited()
    {
        var l = new PulseLimiter();
        var r = l.Limit(GuideDirection.West, 3000, T0);
        r.DurationMs.Should().Be(2500);
        r.Limited.Should().BeTrue();
        l.Limit(GuideDirection.West, 2500, T0).Limited.Should().BeFalse();
    }

    [Test]
    public void AlertsOnSixthConsecutiveSameDirectionClamp()
    {
        // PHD2: the first clamp only records the direction; the counter reaches 5 on the 6th clamp.
        var l = new PulseLimiter();
        for (int i = 0; i < 5; i++)
            l.Limit(GuideDirection.East, 4000, T0.AddSeconds(i)).Alert.Should().BeNull($"clamp {i + 1}");
        var alert = l.Limit(GuideDirection.East, 4000, T0.AddSeconds(5)).Alert;
        alert.Should().NotBeNull();
        alert!.Axis.Should().Be(GuideAxis.Ra);
        alert.Kind.Should().Be(PulseLimitAlertKind.InsufficientCorrection);
        alert.DurationMs.Should().Be(2500);
    }

    [Test]
    public void DirectionChangeOrUnclampedPulseResetsCounter()
    {
        var l = new PulseLimiter();
        for (int i = 0; i < 4; i++)
            l.Limit(GuideDirection.North, 4000, T0);
        l.DecLimitReachedCount.Should().Be(3);
        l.Limit(GuideDirection.South, 4000, T0);
        l.DecLimitReachedCount.Should().Be(0);
        l.Limit(GuideDirection.South, 4000, T0);
        l.DecLimitReachedCount.Should().Be(1);
        l.Limit(GuideDirection.South, 100, T0);
        l.DecLimitReachedCount.Should().Be(0);
    }

    [Test]
    public void AlertIsThrottledTo30SecondsAcrossAxes()
    {
        var l = new PulseLimiter();
        for (int i = 0; i < 5; i++)
        {
            l.Limit(GuideDirection.East, 4000, T0);
            l.Limit(GuideDirection.North, 4000, T0);
        }

        l.Limit(GuideDirection.East, 4000, T0).Alert.Should().NotBeNull();
        l.Limit(GuideDirection.North, 4000, T0.AddSeconds(1)).Alert.Should().BeNull("throttle is shared");
        l.Limit(GuideDirection.East, 4000, T0.AddSeconds(29)).Alert.Should().BeNull();
        l.Limit(GuideDirection.North, 4000, T0.AddSeconds(30)).Alert.Should().NotBeNull();
    }

    [Test]
    public void GracePeriodSuppressesAlertsAndStillConsumesThrottle()
    {
        var l = new PulseLimiter();
        l.DeferAlertCheck(T0);
        for (int i = 0; i < 5; i++)
            l.Limit(GuideDirection.West, 4000, T0);
        l.Limit(GuideDirection.West, 4000, T0.AddSeconds(100)).Alert.Should().BeNull("within 120 s grace");
        l.Limit(GuideDirection.West, 4000, T0.AddSeconds(121)).Alert.Should().BeNull("throttled by the suppressed attempt at 100 s");
        l.Limit(GuideDirection.West, 4000, T0.AddSeconds(130)).Alert.Should().NotBeNull();
    }

    [Test]
    public void AlertKindDependsOnConfiguredMaximum()
    {
        var low = new PulseLimiter();
        low.SetMaxDecDuration(1000).Should().BeFalse();
        for (int i = 0; i < 5; i++)
            low.Limit(GuideDirection.South, 4000, T0);
        low.Limit(GuideDirection.South, 4000, T0).Alert!.Kind.Should().Be(PulseLimitAlertKind.MaxDurationTooLow);

        var max = new PulseLimiter();
        max.SetMaxRaDuration(8000);
        for (int i = 0; i < 5; i++)
            max.Limit(GuideDirection.West, 9000, T0);
        max.Limit(GuideDirection.West, 9000, T0).Alert!.Kind.Should().Be(PulseLimitAlertKind.AtAbsoluteMaximum);
    }

    [Test]
    public void SetterValidation()
    {
        var l = new PulseLimiter();
        l.SetMaxRaDuration(-1).Should().BeTrue();
        l.MaxRaDurationMs.Should().Be(2500);
        l.SetMaxRaDuration(10).Should().BeTrue();
        l.MaxRaDurationMs.Should().Be(50);
        l.SetMaxRaDuration(9000).Should().BeTrue();
        l.MaxRaDurationMs.Should().Be(8000);
        l.SetMaxDecDuration(1200).Should().BeFalse();
        l.MaxDecDurationMs.Should().Be(1200);
    }

    [Test]
    public void NonAlgorithmMovesPassThrough()
    {
        var l = new PulseLimiter();
        var r = l.Limit(GuideDirection.West, 6000, T0, isAlgorithmOrDeducedMove: false);
        r.DurationMs.Should().Be(6000);
        r.Limited.Should().BeFalse();
    }
}
