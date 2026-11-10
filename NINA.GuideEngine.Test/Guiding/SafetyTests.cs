// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

[TestFixture]
public class SafetyTests
{
    [Test]
    public void After_the_timeout_the_full_frame_search_backs_off_to_every_30_s()
    {
        // it ran on every second frame for as long as the star stayed lost
        var p = new ReacquirePolicy(new SafetySettings());
        p.StarLost(T0, 1000, 20);
        var due = new List<double>();
        for (double t = 0; t <= 200; t += 0.5)
        {
            if (p.FullFrameSearchDue(T0.AddSeconds(t)))
            {
                due.Add(t);
            }
        }

        due.Take(6).Should().Equal(0, 2, 6, 14, 30, 60);
        due.Zip(due.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap <= 30);

        p.Reset();
        p.StarLost(T0.AddSeconds(400), 1000, 20);
        p.FullFrameSearchDue(T0.AddSeconds(400)).Should().BeTrue("a new loss starts over");
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);
    private readonly SafetySettings settings = new();

    [Test]
    public void Runaway_detected_when_corrections_make_error_grow()
    {
        var d = new RunawayDetector(settings);
        double err = 0.8;
        SafetyVerdict v = SafetyVerdict.Ok;
        int frame = 0;
        while (v == SafetyVerdict.Ok && frame < 20)
        {
            v = d.Record(T0.AddSeconds(2 * frame), err, 300, 0.1, 0, out _);
            err *= 1.5; // wrong-sign correction pushes the star further away
            frame++;
        }

        v.Should().Be(SafetyVerdict.Runaway);
        frame.Should().BeLessThanOrEqualTo(8);
        d.Cause.Should().Be(RunawayCause.ErrorGrowing);
    }

    [Test]
    public void No_runaway_for_noisy_normal_guiding()
    {
        var d = new RunawayDetector(settings);
        var rnd = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            double ra = rnd.NextDouble() * 1.6 - 0.8;
            double dec = rnd.NextDouble() * 1.2 - 0.6;
            d.Record(T0.AddSeconds(2 * i), ra, (int)(-ra * 400), dec, (int)(-dec * 300), out _).Should().Be(SafetyVerdict.Ok);
        }
    }

    [Test]
    public void No_runaway_while_recovering_from_dither()
    {
        var d = new RunawayDetector(settings);
        double err = 8;
        for (int i = 0; i < 15; i++)
        {
            d.Record(T0.AddSeconds(2 * i), err, 1500, 0, 0, out _).Should().Be(SafetyVerdict.Ok);
            err *= 0.5;
        }
    }

    [Test]
    public void Excessive_pulse_duty_is_a_runaway()
    {
        var d = new RunawayDetector(settings);
        SafetyVerdict v = SafetyVerdict.Ok;
        for (int i = 0; i <= 30 && v == SafetyVerdict.Ok; i++)
        {
            v = d.Record(T0.AddSeconds(2 * i), 0.5 * ((i % 2) * 2 - 1), 1500, 0, 0, out var axis);
            if (v != SafetyVerdict.Ok) axis.Should().Be(GuideAxis.Ra);
        }

        v.Should().Be(SafetyVerdict.Runaway);
        d.Cause.Should().Be(RunawayCause.PulseDuty, "the error swung around zero and never grew");
        d.Reset();
        d.Cause.Should().BeNull();
    }

    [Test]
    public void Mount_not_responding_after_three_ignored_large_pulses()
    {
        var m = new MountResponseMonitor(settings);
        m.PulseIssued(GuideAxis.Ra, 5, -5);
        m.Evaluate(5.1, 0).Should().BeFalse();
        m.PulseIssued(GuideAxis.Ra, 5.1, -5);
        m.Evaluate(5.0, 0).Should().BeFalse();
        m.PulseIssued(GuideAxis.Ra, 5.0, -5);
        m.Evaluate(4.9, 0).Should().BeTrue();
    }

    [Test]
    public void Responding_mount_resets_counter_and_small_pulses_are_ignored()
    {
        var m = new MountResponseMonitor(settings);
        for (int i = 0; i < 10; i++)
        {
            m.PulseIssued(GuideAxis.Dec, 5, -5);
            m.Evaluate(0, i % 2 == 0 ? 4.9 : 1.0).Should().BeFalse();
        }

        m.PulseIssued(GuideAxis.Dec, 1, -1); // too small to judge
        m.Evaluate(0, 1).Should().BeFalse();
    }

    [Test]
    public void Mount_state_watcher_pauses_and_requires_resume_after_large_move()
    {
        var w = new MountStateWatcher(settings);
        var tracking = new MountSnapshot { IsConnected = true, RightAscensionHours = 5, DeclinationDeg = 20, PierSide = PierSide.West };
        w.Update(tracking).Should().Be(MountPauseReason.None);
        w.Update(tracking with { IsSlewing = true }).Should().Be(MountPauseReason.Slewing);
        w.Update(tracking with { RightAscensionHours = 6 }).Should().Be(MountPauseReason.None);
        w.RequiresExplicitResume.Should().BeTrue();
        w.ClearExplicitResume();

        w.Update(tracking with { IsTracking = false }).Should().Be(MountPauseReason.TrackingOff);
        w.Update(tracking).Should().Be(MountPauseReason.None);
        w.RequiresExplicitResume.Should().BeFalse();

        MountStateWatcher.Evaluate(tracking with { IsParked = true, IsSlewing = true }).Should().Be(MountPauseReason.Parked);
        MountStateWatcher.Evaluate(tracking with { IsConnected = false }).Should().Be(MountPauseReason.Disconnected);
    }

    [Test]
    public void Pier_side_change_during_pause_requires_explicit_resume()
    {
        var w = new MountStateWatcher(settings);
        var s = new MountSnapshot { IsConnected = true, PierSide = PierSide.West };
        w.Update(s);
        w.Update(s with { IsSlewing = true });
        w.Update(s with { PierSide = PierSide.East });
        w.RequiresExplicitResume.Should().BeTrue();
    }

    [Test]
    public void Camera_retry_policy_escalates()
    {
        var p = new CameraRetryPolicy(settings);
        p.Failure().Should().Be(CameraFailureAction.Retry);
        p.Failure().Should().Be(CameraFailureAction.Retry);
        p.Failure().Should().Be(CameraFailureAction.Reconnect);
        p.Failure().Should().Be(CameraFailureAction.Retry);
        p.Failure().Should().Be(CameraFailureAction.Retry);
        p.Failure().Should().Be(CameraFailureAction.Fail);
        p.Success();
        p.Failure().Should().Be(CameraFailureAction.Retry);
    }

    [Test]
    public void Reacquire_policy_phases_and_plausibility()
    {
        var r = new ReacquirePolicy(settings);
        r.StarLost(T0, 10000, 40);
        r.Next(T0.AddSeconds(2)).Should().Be(ReacquireAction.SearchLocal);
        var actions = Enumerable.Range(0, 6).Select(i => r.Next(T0.AddSeconds(12 + 2 * i))).ToList();
        actions.Should().Contain(ReacquireAction.SearchFullFrame).And.Contain(ReacquireAction.SearchLocal);
        r.Next(T0.AddSeconds(61)).Should().Be(ReacquireAction.GiveUp);

        r.IsPlausible(9000, 35).Should().BeTrue();
        r.IsPlausible(50000, 200).Should().BeFalse();
        r.IsPlausible(9000, 5).Should().BeFalse();
    }

    [Test]
    public void A_single_slow_frame_gets_a_note_at_most_every_5_minutes_and_no_warning()
    {
        // it raised the warning, which stayed on the guider page as the last error for the rest of the night
        var m = new SlowProcessingMonitor(settings);
        var reports = RecordSlowProcessing(m, Frames(0, 1200, 2, t => t is 100 or 110 or 500 ? 800 : 50));

        reports.Should().Equal((100, SlowProcessingVerdict.SlowFrame), (500, SlowProcessingVerdict.SlowFrame));
    }

    [Test]
    public void Slow_frames_for_a_minute_raise_the_warning_at_most_every_5_minutes()
    {
        var m = new SlowProcessingMonitor(settings);
        var reports = RecordSlowProcessing(m, Frames(0, 700, 2, _ => 700));

        // the first minute is not enough for a median: only the note
        reports.Should().Equal(
            (0, SlowProcessingVerdict.SlowFrame),
            (60, SlowProcessingVerdict.SlowMedian),
            (360, SlowProcessingVerdict.SlowMedian),
            (660, SlowProcessingVerdict.SlowMedian));
        m.Record(T0.AddSeconds(702), 700, out double median);
        median.Should().Be(700);

        // after more than a minute without frames (guiding stopped) the minute starts over
        RecordSlowProcessing(m, Frames(1000, 1100, 2, _ => 700)).Should().Equal(
            (1000, SlowProcessingVerdict.SlowFrame),
            (1060, SlowProcessingVerdict.SlowMedian));
    }

    [Test]
    public void The_warning_needs_the_median_above_the_limit()
    {
        // fewer than half of the frames slow: notes only
        var m = new SlowProcessingMonitor(settings);
        RecordSlowProcessing(m, Frames(0, 1200, 2, t => t % 6 == 0 ? 900 : 100))
            .Should().OnlyContain(r => r.Verdict == SlowProcessingVerdict.SlowFrame);

        // 40 s guide exposures: a minute holds two frames, and one slow frame of them is no median
        m = new SlowProcessingMonitor(settings);
        RecordSlowProcessing(m, Frames(0, 1200, 40, t => t % 80 == 40 ? 900 : 100))
            .Should().OnlyContain(r => r.Verdict == SlowProcessingVerdict.SlowFrame);
    }

    private static IEnumerable<(double T, double Ms)> Frames(double from, double to, double interval, Func<double, double> ms)
    {
        for (double t = from; t <= to; t += interval)
        {
            yield return (t, ms(t));
        }
    }

    private static List<(double T, SlowProcessingVerdict Verdict)> RecordSlowProcessing(SlowProcessingMonitor m, IEnumerable<(double T, double Ms)> frames) =>
        frames.Select(f => (f.T, Verdict: m.Record(T0.AddSeconds(f.T), f.Ms, out _)))
            .Where(r => r.Verdict != SlowProcessingVerdict.None)
            .ToList();
}
