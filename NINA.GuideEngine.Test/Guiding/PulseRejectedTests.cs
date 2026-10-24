// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>
/// A pulse the mount reports it did not carry out (<see cref="PulseRejectedException"/>) counts as not sent, raises a
/// rate-limited alert and does not stop guiding the way failed output does.
/// </summary>
[TestFixture]
public class PulseRejectedTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    [Test]
    public async Task Rejected_Dec_pulses_alert_without_stopping_guiding()
    {
        var clock = new VirtualClock();
        var sim = new Simulator(SimulatorScenario.GoodMount, clock);
        var output = new RejectingOutput(sim.Mount, clock);
        var settings = new GuiderSettings { FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm, ExposureMs = 2000 };
        var guider = new Guider(sim.Camera, output, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        output.Active = () => guider.State == GuiderState.Guiding;
        var events = new List<GuiderEvent>();
        guider.EventRaised += (_, e) =>
        {
            if (e is AlertEvent)
            {
                lock (events)
                {
                    events.Add(e);
                }
            }
        };

        _ = guider.StartGuidingAsync(Settle);
        var duration = TimeSpan.FromMinutes(12);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);

        List<GuiderEvent> seen;
        lock (events)
        {
            seen = [.. events];
        }

        output.Rejected.Should().BeGreaterThan(settings.MaxPulseFailures, "more rejections than the output failures that stop guiding");
        var alerts = seen.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.PulseRejected).ToList();
        alerts.Should().NotBeEmpty();
        alerts.Count.Should().BeLessThanOrEqualTo(3, "at most one alert per 5 minutes");
        seen.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.PulseOutputFailed || a.Code == GuideErrorCode.MountDisconnected);
        output.LastRejectedAt.Should().BeGreaterThan(duration - TimeSpan.FromMinutes(1), "guiding went on to the end");
    }

    [Test]
    public async Task A_rejected_RA_pulse_does_not_keep_the_Dec_pulse_of_the_frame_from_being_sent()
    {
        var clock = new VirtualClock();
        var sim = new Simulator(SimulatorScenario.GoodMount, clock);
        var output = new RejectingOutput(sim.Mount, clock, GuideAxis.Ra);
        var settings = new GuiderSettings { FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm, ExposureMs = 2000 };
        var guider = new Guider(sim.Camera, output, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        output.Active = () => guider.State == GuiderState.Guiding;

        _ = guider.StartGuidingAsync(Settle);
        var duration = TimeSpan.FromMinutes(6);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);

        // the RA pulse comes first in a frame: before, its rejection dropped the frame's Dec pulse too
        output.Rejected.Should().BeGreaterThan(0);
        output.SentAfterRejection.Should().BeGreaterThan(0, "the Dec pulse of a frame whose RA pulse was rejected went out");
    }

    /// <summary>While <see cref="Active"/>, every pulse on the axis is rejected without moving the mount.</summary>
    private sealed class RejectingOutput(SimulatedMount inner, VirtualClock clock, GuideAxis axis = GuideAxis.Dec) : IPulseOutput
    {
        public Func<bool> Active { get; set; } = () => false;

        public int Rejected { get; private set; }

        /// <summary>Pulses on the other axis sent right after a rejection, in the same frame.</summary>
        public int SentAfterRejection { get; private set; }

        public TimeSpan LastRejectedAt { get; private set; }

        public string Name => inner.Name;

        public bool IsConnected => inner.IsConnected;

        public bool SupportsSimultaneousPulses => false;

        public Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
        {
            if (direction.Axis() == axis && Active())
            {
                Rejected++;
                LastRejectedAt = clock.Elapsed;
                throw new PulseRejectedException($"the driver rejected the {durationMs} ms pulse");
            }

            if (Rejected > 0 && LastRejectedAt == clock.Elapsed && direction.Axis() != axis)
            {
                SentAfterRejection++;
            }

            return inner.PulseAsync(direction, durationMs, ct);
        }
    }
}
