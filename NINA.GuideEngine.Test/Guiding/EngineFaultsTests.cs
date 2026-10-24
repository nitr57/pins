// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>The rate limit of <see cref="EngineFaultEvent"/>: one report per source and minute, with the count left out.</summary>
[TestFixture]
public class EngineFaultsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public void Reports_each_source_once_per_interval_and_counts_the_rest()
    {
        var faults = new EngineFaults();
        var ex = new InvalidOperationException("broken");

        var first = faults.Report(T0, "A", ex);
        first.Should().NotBeNull();
        first!.Source.Should().Be("A");
        first.Message.Should().Be("InvalidOperationException: broken");
        first.Suppressed.Should().Be(0);
        first.Timestamp.Should().Be(T0);

        faults.Report(T0.AddSeconds(10), "A", ex).Should().BeNull();
        faults.Report(T0.AddSeconds(59), "A", ex).Should().BeNull();
        faults.Report(T0.AddSeconds(20), "B", ex).Should().NotBeNull("sources are limited separately");

        var next = faults.Report(T0 + EngineFaults.Interval, "A", ex);
        next.Should().NotBeNull();
        next!.Suppressed.Should().Be(2);
        faults.Report(T0 + EngineFaults.Interval + TimeSpan.FromSeconds(1), "A", ex).Should().BeNull();
    }

    [Test]
    public void A_clock_that_went_back_does_not_silence_a_source()
    {
        var faults = new EngineFaults();
        var ex = new InvalidOperationException("broken");
        faults.Report(T0, "A", ex).Should().NotBeNull();
        faults.Report(T0.AddMinutes(-5), "A", ex).Should().NotBeNull();
    }
}
