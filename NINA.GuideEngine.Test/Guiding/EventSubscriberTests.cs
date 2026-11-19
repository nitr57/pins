// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>A subscriber that throws (the guide log on a full disk) does not keep an event from the ones after it.</summary>
[TestFixture]
public class EventSubscriberTests
{
    [Test]
    public async Task A_throwing_subscriber_does_not_hide_events_from_the_next_one()
    {
        var clock = new VirtualClock();
        var sim = new Simulator(SimulatorScenario.GoodMount, clock);
        var settings = new GuiderSettings { FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm, ExposureMs = 2000 };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        int thrown = 0;
        int received = 0;
        guider.EventRaised += (_, e) =>
        {
            if (e is LoopingExposuresEvent)
            {
                Interlocked.Increment(ref thrown);
                throw new IOException("No space left on device");
            }
        };
        guider.EventRaised += (_, e) =>
        {
            if (e is LoopingExposuresEvent)
            {
                Interlocked.Increment(ref received);
            }
        };

        var duration = TimeSpan.FromMinutes(1);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);

        thrown.Should().BeGreaterThan(1);
        received.Should().Be(thrown, "the second subscriber got every event the first one failed on");
    }

    [Test]
    public async Task Two_failing_subscribers_are_reported_each()
    {
        var clock = new VirtualClock();
        var sim = new Simulator(SimulatorScenario.GoodMount, clock);
        var settings = new GuiderSettings { FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm, ExposureMs = 2000 };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        var faults = new List<EngineFaultEvent>();
        guider.EventRaised += new FailingSubscriber().OnEvent;
        guider.EventRaised += new OtherFailingSubscriber().OnEvent;
        guider.EventRaised += (_, e) =>
        {
            if (e is EngineFaultEvent f)
            {
                lock (faults)
                {
                    faults.Add(f);
                }
            }
        };

        // shorter than the minute a fault source is rate-limited for
        var duration = TimeSpan.FromSeconds(30);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);

        List<EngineFaultEvent> seen;
        lock (faults)
        {
            seen = [.. faults];
        }

        seen.Select(f => f.Source).Distinct().Should().HaveCount(2, "one subscriber's fault does not hide the other's");
    }

    private sealed class FailingSubscriber
    {
        public void OnEvent(object? sender, GuiderEvent e)
        {
            if (e is LoopingExposuresEvent)
            {
                throw new IOException("No space left on device");
            }
        }
    }

    private sealed class OtherFailingSubscriber
    {
        public void OnEvent(object? sender, GuiderEvent e)
        {
            if (e is LoopingExposuresEvent)
            {
                throw new InvalidOperationException("broken");
            }
        }
    }
}
