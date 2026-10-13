// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>The learned Dec backlash pulse: kept across settings changes and, through the host, sessions.</summary>
[TestFixture]
public class BacklashMemoryTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    private static (Guider Guider, VirtualClock Clock, List<GuiderEvent> Events) Create(SimulatorScenario scenario, BacklashSettings backlash)
    {
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, Backlash = backlash };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        var events = new List<GuiderEvent>();
        guider.EventRaised += (_, e) =>
        {
            if (e is BacklashPulseLearnedEvent)
            {
                lock (events)
                {
                    events.Add(e);
                }
            }
        };

        return (guider, clock, events);
    }

    private static async Task RunFor(Guider guider, VirtualClock clock, TimeSpan duration)
    {
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);
    }

    [Test]
    public async Task A_restored_pulse_is_used_while_the_setting_is_the_one_it_was_learned_from()
    {
        var on = new BacklashSettings { Enabled = true, PulseMs = 200 };
        var (guider, clock, _) = Create(SimulatorScenario.GoodMount, on);
        guider.DecBacklashPulseMs.Should().Be(200);

        guider.RestoreBacklash(new BacklashState(200, 260));
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().Be(260);

        // another setting rebuilds the corrector: the learned pulse stays
        guider.UpdateSettings(guider.Settings with { ExposureMs = 1500 });
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().Be(260);

        // a new pulse setting starts over from itself
        guider.UpdateSettings(guider.Settings with { Backlash = on with { PulseMs = 150 } });
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().Be(150);

        // back to the setting it was learned from
        guider.UpdateSettings(guider.Settings with { Backlash = on });
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().Be(260);

        guider.UpdateSettings(guider.Settings with { Backlash = on with { Enabled = false } });
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().BeNull("the compensation is off");
    }

    [Test]
    public async Task A_restored_pulse_stays_within_the_range_of_the_setting()
    {
        var (guider, clock, _) = Create(SimulatorScenario.GoodMount, new BacklashSettings { Enabled = true, PulseMs = 200 });
        guider.RestoreBacklash(new BacklashState(200, 900));
        await RunFor(guider, clock, TimeSpan.FromSeconds(4));
        guider.DecBacklashPulseMs.Should().Be(300, "1.5 × the setting is the ceiling");
    }

    [Test, Category("Slow")]
    public async Task The_adaptive_compensation_reports_the_pulse_it_learned()
    {
        // 12″ of Dec backlash: far more than a 300 ms pulse takes up
        var (guider, clock, events) = Create(SimulatorScenario.Backlash, new BacklashSettings { Enabled = true, PulseMs = 300 });
        _ = guider.StartGuidingAsync(Settle);
        await RunFor(guider, clock, TimeSpan.FromMinutes(40));

        List<BacklashPulseLearnedEvent> learned;
        lock (events)
        {
            learned = events.OfType<BacklashPulseLearnedEvent>().ToList();
        }

        learned.Should().NotBeEmpty();
        learned.Should().OnlyContain(e => e.State.SetPulseMs == 300 && e.State.PulseMs >= 20 && e.State.PulseMs <= 450);
        guider.DecBacklashPulseMs.Should().Be(learned[^1].State.PulseMs);
    }

    [Test]
    public void The_store_keeps_a_pulse_per_profile_and_mount()
    {
        var store = new BacklashStore();
        store.Set(new PulseModelKey("p1", "EQMod"), new BacklashState(200, 260));
        store.Set(new PulseModelKey("p1", "AM5"), new BacklashState(100, 80));

        var back = BacklashStore.FromJson(store.ToJson());
        back.Count.Should().Be(2);
        back.Get(new PulseModelKey("p1", "EQMod")).Should().Be(new BacklashState(200, 260));
        back.Get(new PulseModelKey("p2", "EQMod")).Should().BeNull();
    }
}
