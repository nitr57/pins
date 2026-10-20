// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

[TestFixture]
public class SafetyTests
{
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
}
