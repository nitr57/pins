// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.Simulation;

public class SimulatorFaultTests
{
    private static Simulator Create() => new(SimTestHelpers.Clean());

    [Test]
    public void Clouds_ramp_to_zero_stay_and_ramp_back()
    {
        var sim = Create();
        sim.VirtualClock!.Advance(TimeSpan.FromSeconds(50));
        sim.InjectFault(SimulatorFault.Clouds);

        sim.Sky.TransparencyAt(49).Should().Be(1);
        sim.Sky.TransparencyAt(55).Should().BeApproximately(0.5, 1e-9);
        sim.Sky.TransparencyAt(60).Should().BeApproximately(0, 1e-9);
        sim.Sky.TransparencyAt(150).Should().BeApproximately(0, 1e-9);
        sim.Sky.TransparencyAt(155).Should().BeApproximately(0.5, 1e-9);
        sim.Sky.TransparencyAt(161).Should().Be(1);
    }

    [Test]
    public void Bump_moves_the_pointing_by_15_pixels_once_without_moving_the_encoders()
    {
        var sim = Create();
        sim.VirtualClock!.Advance(TimeSpan.FromSeconds(10));
        var before = sim.Mount.GetSnapshot();
        sim.InjectFault(SimulatorFault.Bump);

        sim.Mount.GetPointingError(9.99).Magnitude.Should().BeApproximately(0, 1e-9);
        sim.Mount.GetPointingError(10.01).Magnitude.Should().BeApproximately(15 * sim.Camera.PixelScale, 1e-6);
        sim.Mount.GetPointingErrorBreakdown(20).Bumps.Magnitude.Should().BeApproximately(15 * sim.Camera.PixelScale, 1e-6);
        var after = sim.Mount.GetSnapshot();
        after.DeclinationDeg.Should().Be(before.DeclinationDeg);
        after.IsSlewing.Should().BeFalse();
    }

    [Test]
    public async Task Mount_stops_responding_for_60_seconds_after_a_stall()
    {
        var sim = Create();
        sim.InjectFault(SimulatorFault.MountStopsResponding);
        sim.Mount.GetPointingError(0.01).Ra.Should().BeApproximately(7 * sim.Camera.PixelScale, 1e-6);

        await sim.Mount.PulseAsync(GuideDirection.West, 1000, default);
        sim.Mount.GetPointingErrorBreakdown(sim.Now).Guiding.Ra.Should().Be(0, "pulses have no effect");

        sim.VirtualClock!.Advance(TimeSpan.FromSeconds(60));
        await sim.Mount.PulseAsync(GuideDirection.West, 1000, default);
        sim.Mount.GetPointingErrorBreakdown(sim.Now).Guiding.Ra.Should().BeLessThan(-5, "responding again");
    }

    [Test]
    public async Task Runaway_inverts_dec_pulses_for_120_seconds()
    {
        var sim = Create();
        sim.InjectFault(SimulatorFault.Runaway);
        sim.Mount.GetPointingError(0.01).Dec.Should().BeApproximately(3 * sim.Camera.PixelScale, 1e-6);

        await sim.Mount.PulseAsync(GuideDirection.North, 1000, default);
        sim.Mount.GetPointingErrorBreakdown(sim.Now).Guiding.Dec.Should().BeLessThan(-5, "North moves South");

        sim.VirtualClock!.Advance(TimeSpan.FromSeconds(120));
        double dec = sim.Mount.GetPointingErrorBreakdown(sim.Now).Guiding.Dec;
        await sim.Mount.PulseAsync(GuideDirection.North, 1000, default);
        sim.Mount.GetPointingErrorBreakdown(sim.Now).Guiding.Dec.Should().BeGreaterThan(dec + 5, "normal again");
    }

    [Test]
    public async Task Camera_failure_fails_the_next_three_exposures()
    {
        var sim = Create();
        sim.InjectFault(SimulatorFault.CameraFailure);
        for (int i = 0; i < 3; i++)
        {
            await FluentActions.Awaiting(() => sim.CaptureAsync(100)).Should().ThrowAsync<GuideCameraException>();
        }

        (await sim.CaptureAsync(100)).Width.Should().Be(sim.Camera.SensorWidth);
    }
}
