// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.Simulation;

public class VirtualClockTests
{
    [Test]
    public void Delay_advances_time_instantly_and_completes_synchronously()
    {
        var clock = new VirtualClock();
        var start = clock.UtcNow;

        var task = clock.Delay(TimeSpan.FromMilliseconds(1500), CancellationToken.None);

        task.IsCompletedSuccessfully.Should().BeTrue();
        clock.ElapsedMs.Should().Be(1500);
        clock.ElapsedSeconds.Should().Be(1.5);
        clock.UtcNow.Should().Be(start + TimeSpan.FromMilliseconds(1500));
    }

    [Test]
    public void Zero_negative_and_cancelled_delays_do_not_advance()
    {
        var clock = new VirtualClock();
        clock.Delay(TimeSpan.Zero, CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue();
        clock.Delay(TimeSpan.FromSeconds(-1), CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue();

        var cancelled = clock.Delay(TimeSpan.FromSeconds(1), new CancellationToken(true));

        cancelled.IsCanceled.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public async Task Concurrent_delays_are_serialised()
    {
        var clock = new VirtualClock();
        await Task.WhenAll(clock.Delay(TimeSpan.FromMilliseconds(100), default), clock.Delay(TimeSpan.FromMilliseconds(200), default));
        clock.ElapsedMs.Should().Be(300);
    }

    [Test]
    public void Advance_is_thread_safe()
    {
        var clock = new VirtualClock();
        Parallel.For(0, 10000, _ => clock.Advance(TimeSpan.FromMilliseconds(1)));
        clock.ElapsedMs.Should().Be(10000);
    }

    [Test]
    public void AdvanceTo_never_goes_back()
    {
        var clock = new VirtualClock();
        clock.AdvanceTo(TimeSpan.FromSeconds(5));
        clock.AdvanceTo(TimeSpan.FromSeconds(3));
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void CancelAt_fires_when_virtual_time_reaches_deadline()
    {
        var clock = new VirtualClock();
        var token = clock.CancelAt(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9.999));
        token.IsCancellationRequested.Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        token.IsCancellationRequested.Should().BeTrue();

        clock.CancelAt(TimeSpan.FromSeconds(1)).IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public async Task Async_loop_driven_by_virtual_clock_is_deterministic()
    {
        static async Task<List<long>> RunLoop()
        {
            var clock = new VirtualClock();
            var ct = clock.CancelAt(TimeSpan.FromSeconds(60));
            var stamps = new List<long>();
            int i = 0;
            try
            {
                while (true)
                {
                    await clock.Delay(TimeSpan.FromMilliseconds(1000 + (i++ % 7) * 13), ct);
                    stamps.Add(clock.ElapsedMs);
                }
            }
            catch (OperationCanceledException)
            {
            }

            return stamps;
        }

        var a = await RunLoop();
        var b = await RunLoop();
        a.Should().Equal(b);
        a.Last().Should().BeGreaterThanOrEqualTo(60000);
    }

    [Test]
    public void NowSeconds_uses_full_resolution_for_virtual_clock()
    {
        var clock = new VirtualClock();
        clock.Advance(TimeSpan.FromTicks(12345));
        ((IClock)clock).NowSeconds().Should().BeApproximately(0.0012345, 1e-12);
    }
}
