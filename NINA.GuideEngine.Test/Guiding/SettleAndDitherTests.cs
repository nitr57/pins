// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

[TestFixture]
public class SettleAndDitherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public void Settle_succeeds_after_time_in_range()
    {
        var m = new SettleMonitor();
        m.Begin(new SettleParams(1.5, 10, 60), T0);
        var t = T0;
        m.Update(true, 3.0, t).Outcome.Should().Be(SettleOutcome.InProgress);
        for (int i = 1; i <= 5; i++)
        {
            t = t.AddSeconds(2);
            m.Update(true, 1.0, t).Outcome.Should().Be(SettleOutcome.InProgress);
        }

        t = t.AddSeconds(2); // 10 s after first in-range frame at t0+2
        var p = m.Update(true, 1.0, t.AddSeconds(0.5));
        p.Outcome.Should().Be(SettleOutcome.Succeeded);
        p.TotalFrames.Should().Be(7);
        m.IsActive.Should().BeFalse();
    }

    [Test]
    public void Settle_restarts_in_range_timer_when_leaving_range()
    {
        var m = new SettleMonitor();
        m.Begin(new SettleParams(1.5, 4, 60), T0);
        m.Update(true, 1.0, T0.AddSeconds(1));
        m.Update(true, 1.0, T0.AddSeconds(3));
        m.Update(true, 2.0, T0.AddSeconds(5)).Outcome.Should().Be(SettleOutcome.InProgress);
        m.Update(true, 1.0, T0.AddSeconds(7)).Outcome.Should().Be(SettleOutcome.InProgress);
        m.Update(true, 1.0, T0.AddSeconds(9)).Outcome.Should().Be(SettleOutcome.InProgress);
        m.Update(true, 1.0, T0.AddSeconds(11)).Outcome.Should().Be(SettleOutcome.Succeeded);
    }

    [Test]
    public void Settle_times_out_and_counts_dropped_frames()
    {
        var m = new SettleMonitor();
        m.Begin(new SettleParams(0.5, 5, 10), T0);
        SettleProgress p = null!;
        for (int i = 1; i <= 6; i++)
        {
            p = m.Update(i % 2 == 0, 2.0, T0.AddSeconds(2 * i));
            if (p.Outcome != SettleOutcome.InProgress) break;
        }

        p.Outcome.Should().Be(SettleOutcome.TimedOut);
        p.DroppedFrames.Should().Be(3);
    }

    [Test]
    public void Settle_with_zero_time_succeeds_on_first_in_range_frame()
    {
        var m = new SettleMonitor();
        m.Begin(new SettleParams(1, 0, 10), T0);
        m.Update(false, 0.1, T0.AddSeconds(1)).Outcome.Should().Be(SettleOutcome.InProgress);
        m.Update(true, 0.1, T0.AddSeconds(2)).Outcome.Should().Be(SettleOutcome.Succeeded);
    }

    [Test]
    public void Random_dither_is_within_amount_and_ra_only_has_no_dec()
    {
        var p = new DitherPlanner(42);
        for (int i = 0; i < 200; i++)
        {
            var o = p.NextMountOffset(3, raOnly: i % 2 == 0);
            Math.Abs(o.X).Should().BeLessThanOrEqualTo(3);
            Math.Abs(o.Y).Should().BeLessThanOrEqualTo(3);
            if (i % 2 == 0) o.Y.Should().Be(0);
        }
    }

    [Test]
    public void Spiral_dither_matches_phd2_sequence()
    {
        var p = new DitherPlanner { Mode = DitherMode.Spiral };
        var seq = Enumerable.Range(0, 8).Select(_ => p.NextMountOffset(1, false)).Select(o => ((int)o.X, (int)o.Y)).ToList();

        // PHD2: dx=-1,dy=0 initially; ROT(t=-dx; dx=dy; dy=t) when on a corner. Position walks a square spiral.
        var pos = (0, 0);
        var visited = new HashSet<(int, int)> { pos };
        foreach (var (dx, dy) in seq)
        {
            (Math.Abs(dx) + Math.Abs(dy)).Should().Be(1);
            pos = (pos.Item1 + dx, pos.Item2 + dy);
            visited.Add(pos).Should().BeTrue("a spiral never revisits a position");
        }

        seq[0].Should().Be((0, 1));
    }

    [Test]
    public void Spiral_ra_only_follows_documented_x_sequence()
    {
        var p = new DitherPlanner { Mode = DitherMode.Spiral };
        int x = 0;
        var xs = new List<int>();
        for (int i = 0; i < 9; i++)
        {
            x += (int)p.NextMountOffset(1, true).X;
            xs.Add(x);
        }

        xs.Should().Equal(1, -1, -2, 2, 3, -3, -4, 4, 5);
    }

    [Test]
    public void Dither_near_edge_is_reflected()
    {
        var lockPos = new GuidePoint(20, 100);
        bool Valid(GuidePoint pt) => DitherPlanner.IsValidLockPosition(pt, 200, 200, 15);
        var plan = DitherPlanner.Plan(lockPos, new GuidePoint(-8, 0), 200, 200, m => m, Valid);
        plan.LockPositionValid.Should().BeTrue();
        plan.MountDelta.X.Should().Be(8);
        plan.NewLockPosition.X.Should().Be(28);
    }

    [Test]
    public void FastRecenter_steps_until_done()
    {
        var r = new FastRecenter();
        r.Start(new GuidePoint(10, -5), 15);
        double sumX = 0, sumY = 0;
        int steps = 0;
        bool done = false;
        while (!done)
        {
            var (ofs, d) = r.NextStep();
            sumX += ofs.X;
            sumY += ofs.Y;
            done = d;
            steps++;
            steps.Should().BeLessThan(10);
        }

        sumX.Should().BeApproximately(-10, 0.5);
        sumY.Should().BeApproximately(5, 0.5);
        r.IsActive.Should().BeFalse();
    }
}
