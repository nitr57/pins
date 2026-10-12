// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Guiding;

[TestFixture]
public class CycleTimerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 22, 0, 0, TimeSpan.Zero);

    // ticks, so fractions of a millisecond survive on every runtime
    private static DateTimeOffset At(double ms) => T0 + TimeSpan.FromTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));

    /// <summary>A 500 ms exposure, the frame 52 ms after it, 1 ms processing, a 300 ms pulse done after 347 ms.</summary>
    private static void PulsedCycle(CycleTimer timer, double start)
    {
        timer.CaptureStarting(At(start), 500);
        timer.FrameReady(At(start + 552));
        timer.Processed(At(start + 553));
        timer.PulsesStarting(At(start + 553.8));
        timer.PulsesDone(At(start + 900.8));
    }

    [Test]
    public void A_cycle_is_complete_when_the_next_capture_starts()
    {
        var timer = new CycleTimer();
        PulsedCycle(timer, 0);
        timer.Timing.Should().BeNull();

        timer.CaptureStarting(At(912.8), 500);

        var last = timer.Timing!.Last;
        last.CycleMs.Should().BeApproximately(912.8, 1e-6);
        last.ExposureMs.Should().Be(500);
        last.CameraMs.Should().BeApproximately(52, 1e-6);
        last.ProcessingMs.Should().BeApproximately(1, 1e-6);
        last.FrameToPulseMs!.Value.Should().BeApproximately(1.8, 1e-6);
        last.PulseMs.Should().BeApproximately(347, 1e-6);
        // 12 ms after the pulses and 0.8 ms between processing and the pulses
        last.OtherMs.Should().BeApproximately(12.8, 1e-6);
        (last.ExposureMs + last.CameraMs + last.ProcessingMs + last.PulseMs + last.OtherMs).Should().BeApproximately(last.CycleMs, 1e-6);
        timer.Timing.FramesPerSecond.Should().BeApproximately(1000 / 912.8, 1e-9);
        timer.Timing.Cycles.Should().Be(1);
    }

    [Test]
    public void A_cycle_without_pulses_has_no_frame_to_pulse_time()
    {
        var timer = new CycleTimer();
        timer.CaptureStarting(At(0), 500);
        timer.FrameReady(At(552));
        timer.Processed(At(553));
        timer.CaptureStarting(At(564), 500);

        var last = timer.Timing!.Last;
        last.FrameToPulseMs.Should().BeNull();
        last.PulseMs.Should().Be(0);
        last.OtherMs.Should().BeApproximately(11, 1e-6);
        timer.Timing.Median.FrameToPulseMs.Should().BeNull();
    }

    [Test]
    public void Medians_cover_the_last_twenty_cycles_and_pulse_times_only_cycles_with_pulses()
    {
        var timer = new CycleTimer();
        double t = 0;
        for (int i = 0; i < 30; i++)
        {
            if (i % 2 == 0)
            {
                PulsedCycle(timer, t);
                t += 912.8;
            }
            else
            {
                timer.CaptureStarting(At(t), 500);
                timer.FrameReady(At(t + 552));
                timer.Processed(At(t + 553));
                t += 564;
            }
        }
        timer.CaptureStarting(At(t), 500);

        var timing = timer.Timing!;
        timing.Cycles.Should().Be(CycleTimer.Window);
        timing.Median.CycleMs.Should().BeApproximately((564 + 912.8) / 2, 1e-6);
        timing.Median.PulseMs.Should().BeApproximately(347, 1e-6);
        timing.Median.FrameToPulseMs!.Value.Should().BeApproximately(1.8, 1e-6);
    }

    [Test]
    public void A_broken_off_cycle_is_dropped()
    {
        var timer = new CycleTimer();
        PulsedCycle(timer, 0);
        timer.CaptureStarting(At(912.8), 500);

        // the capture fails: no frame, so the next start must not complete a 5 s cycle
        timer.CaptureStarting(At(1000), 500);
        timer.Discard();
        timer.CaptureStarting(At(6000), 500);

        timer.Timing!.Cycles.Should().Be(1);
        timer.Timing.Last.CycleMs.Should().BeApproximately(912.8, 1e-6);
    }

    [Test]
    public void Marks_without_a_capture_in_progress_are_ignored()
    {
        var timer = new CycleTimer();
        timer.FrameReady(At(10));
        timer.PulsesStarting(At(20));
        timer.CaptureStarting(At(30), 500);

        timer.Timing.Should().BeNull();
    }
}
