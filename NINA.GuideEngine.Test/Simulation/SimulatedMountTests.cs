// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.Simulation;

public class SimulatedMountTests
{
    private const double Sidereal = SimulatedMount.SiderealArcsecPerSec;

    private static (SimulatedMount Mount, VirtualClock Clock) Create(MountSimConfig config)
    {
        var clock = new VirtualClock();
        return (new SimulatedMount(config, clock), clock);
    }

    [TestCase(0.0)]
    [TestCase(60.0)]
    public async Task Ra_pulse_moves_pointing_by_rate_times_cosdec_times_duration(double dec)
    {
        var (mount, clock) = Create(new MountSimConfig { DeclinationDeg = dec, GuideRateRa = 0.5 });

        await mount.PulseAsync(GuideDirection.West, 1000, default);

        double expected = 0.5 * Sidereal * Math.Cos(dec * Math.PI / 180);
        clock.ElapsedMs.Should().Be(1000);
        mount.GetPointingError().Ra.Should().BeApproximately(-expected, 1e-9);
        mount.GetPointingError().Dec.Should().Be(0);

        await mount.PulseAsync(GuideDirection.East, 2000, default);
        mount.GetPointingError().Ra.Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public async Task Dec_pulse_is_not_scaled_by_cosdec_and_is_linear_during_the_pulse()
    {
        var (mount, _) = Create(new MountSimConfig { DeclinationDeg = 70, GuideRateDec = 1.0 });

        await mount.PulseAsync(GuideDirection.North, 400, default);

        mount.GetPointingError(0.4).Dec.Should().BeApproximately(0.4 * Sidereal, 1e-9);
        mount.GetPointingError(0.1).Dec.Should().BeApproximately(0.1 * Sidereal, 1e-9);
    }

    [Test]
    public async Task Dec_backlash_takes_up_dead_band_on_reversal()
    {
        var (mount, _) = Create(new MountSimConfig { DecBacklashArcsec = 10.0, GuideRateDec = 0.5 });
        double step = 0.5 * Sidereal; // 7.52" per 1000 ms

        await mount.PulseAsync(GuideDirection.North, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(0, 1e-9, "the first 10\" only take up the gear play");

        await mount.PulseAsync(GuideDirection.North, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(2 * step - 10.0, 1e-9);

        await mount.PulseAsync(GuideDirection.North, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(3 * step - 10.0, 1e-9, "no backlash while continuing in the same direction");

        double before = mount.GetPointingError().Dec;
        await mount.PulseAsync(GuideDirection.South, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(before, 1e-9, "reversal eats the dead band again");
        mount.DecBacklashPlay.Should().BeApproximately(10.0 - step, 1e-9);
    }

    [Test]
    public async Task Ra_stiction_swallows_start_of_each_pulse()
    {
        var (mount, _) = Create(new MountSimConfig { RaStictionMs = 200, DeclinationDeg = 0 });

        await mount.PulseAsync(GuideDirection.East, 500, default);
        await mount.PulseAsync(GuideDirection.East, 150, default);

        mount.GetPointingError().Ra.Should().BeApproximately(0.5 * Sidereal * 0.3, 1e-9);
    }

    [Test]
    public async Task Not_responding_mount_ignores_pulses_but_time_passes()
    {
        var (mount, clock) = Create(new MountSimConfig());
        mount.NotResponding = true;

        await mount.PulseAsync(GuideDirection.North, 800, default);

        clock.ElapsedMs.Should().Be(800);
        mount.GetPointingError().Should().Be(SkyOffset.Zero);
        mount.PulseCount.Should().Be(1);
    }

    [Test]
    public async Task Dec_direction_reverses_on_west_pier_and_with_invert_fault()
    {
        var (mount, _) = Create(new MountSimConfig { PierSide = PierSide.West, GuideRateDec = 1.0 });
        await mount.PulseAsync(GuideDirection.North, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(-Sidereal, 1e-9);

        mount.InvertDecPulses = true;
        await mount.PulseAsync(GuideDirection.North, 1000, default);
        mount.GetPointingError().Dec.Should().BeApproximately(0, 1e-9);
    }

    [Test]
    public void Periodic_error_amplitude_is_recovered_from_truth()
    {
        var (mount, _) = Create(new MountSimConfig
        {
            DeclinationDeg = 30,
            PeriodicError = [new PeriodicErrorTerm(8.0, 480.0, 0.3), new PeriodicErrorTerm(2.0, 240.0, 1.1)],
        });

        // Least-squares fit of the fundamental over two worm periods.
        double sc = 0, ss = 0;
        int n = 0;
        for (double t = 0; t < 960; t += 0.5, n++)
        {
            double e = mount.GetPointingError(t).Ra;
            sc += e * Math.Cos(2 * Math.PI * t / 480);
            ss += e * Math.Sin(2 * Math.PI * t / 480);
        }

        double amplitude = 2 * Math.Sqrt(sc * sc + ss * ss) / n;
        amplitude.Should().BeApproximately(8.0 * Math.Cos(Math.PI / 6), 0.01);
    }

    [Test]
    public void A_periodic_error_term_without_change_is_the_plain_sine()
    {
        var (mount, _) = Create(new MountSimConfig { DeclinationDeg = 0, PeriodicError = [new PeriodicErrorTerm(8.0, 480.0, 0.3)] });
        for (double t = 0; t < 2000; t += 13.7) mount.PeriodicErrorAt(t).Should().Be(8.0 * Math.Sin(2 * Math.PI * t / 480.0 + 0.3));
    }

    [Test]
    public void The_amplitude_and_phase_of_a_term_change_slowly_within_their_bounds_and_repeat_with_the_seed()
    {
        var term = new PeriodicErrorTerm(10.0, 100.0)
        {
            AmplitudeChange = 0.3, AmplitudeChangeSec = 2000, PhaseChangeRad = 0.5, PhaseChangeSec = 3000,
        };
        var config = new MountSimConfig { DeclinationDeg = 0, PeriodicError = [term], Seed = 3 };
        var (mount, _) = Create(config);
        var (same, _) = Create(config);
        var (other, _) = Create(config with { Seed = 4 });

        // amplitude and phase of each 100 s cycle by a least-squares fit
        var amplitudes = new List<double>();
        var phases = new List<double>();
        const int cycles = 2000;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            double sc = 0, ss = 0;
            for (int i = 0; i < 200; i++)
            {
                double t = cycle * 100 + i * 0.5;
                double e = mount.PeriodicErrorAt(t);
                if (cycle < 100) e.Should().Be(same.PeriodicErrorAt(t));
                sc += e * Math.Cos(2 * Math.PI * t / 100);
                ss += e * Math.Sin(2 * Math.PI * t / 100);
            }

            amplitudes.Add(Math.Sqrt(sc * sc + ss * ss) / 100 / 10.0 - 1);
            phases.Add(Math.Atan2(sc, ss));
        }

        Enumerable.Range(0, 300).Count(c => other.PeriodicErrorAt(c * 100 + 25) != mount.PeriodicErrorAt(c * 100 + 25)).Should().BeGreaterThan(290);
        amplitudes.Should().OnlyContain(a => Math.Abs(a) <= 0.31);
        phases.Should().OnlyContain(p => Math.Abs(p) <= 0.52);

        // knots evenly within ± size joined by a smoothstep: a standard deviation of about 0.5 × size
        StandardDeviation(amplitudes).Should().BeInRange(0.35 * 0.3, 0.65 * 0.3);
        StandardDeviation(phases).Should().BeInRange(0.35 * 0.5, 0.65 * 0.5);

        // slow: neighbouring cycles differ by much less than the spread
        Enumerable.Range(1, cycles - 1).Average(c => Math.Abs(amplitudes[c] - amplitudes[c - 1])).Should().BeLessThan(0.1 * 0.3);
    }

    [Test]
    public void A_changing_curve_on_the_axis_depends_on_the_gear_angle_not_on_the_time()
    {
        var term = new PeriodicErrorTerm(10.0, 430.8) { AmplitudeChange = 0.3, AmplitudeChangeSec = 430.8, PhaseChangeRad = 0.5, PhaseChangeSec = 430.8 };
        var config = new MountSimConfig { DeclinationDeg = 0, PeriodicError = [term], PeriodicErrorFollowsAxis = true, RightAscensionHours = 6 };
        var (mount, _) = Create(config);

        // a mount that starts 1000 s of axis angle further on the same gear: same curve, 1000 s earlier
        var (later, _) = Create(config with { SiderealTimeAtStartHours = 6 + 1000 / SimulatedMount.SecondsPerSiderealHour });
        for (double t = 0; t < 3000; t += 7.1) later.PeriodicErrorAt(t).Should().BeApproximately(mount.PeriodicErrorAt(t + 1000), 1e-9);
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    [Test]
    public void Drift_accumulates_linearly()
    {
        var (mount, _) = Create(new MountSimConfig { DecDriftArcsecPerMin = 1.5, RaDriftArcsecPerMin = -0.5 });
        var e = mount.GetPointingError(600);
        e.Dec.Should().BeApproximately(15.0, 1e-9);
        e.Ra.Should().BeApproximately(-5.0, 1e-9);
    }

    [Test]
    public void Random_walk_and_wind_are_deterministic_and_scaled()
    {
        var cfg = new MountSimConfig { RandomWalkArcsecPerSqrtSec = 0.1, Wind = new WindGustConfig(2, 3.0, 4.0), Seed = 5 };
        var (a, _) = Create(cfg);
        var (b, _) = Create(cfg);

        // evaluate b out of order: results must not depend on query order
        b.GetPointingError(3000);
        for (double t = 0; t < 1000; t += 7.3) a.GetPointingError(t).Should().Be(b.GetPointingError(t));

        var (walkOnly, _) = Create(cfg with { Wind = null });
        double sum2 = 0;
        int n = 0;
        for (int t = 0; t < 3000; t++, n++)
        {
            double d = walkOnly.GetPointingError(t + 1).Dec - walkOnly.GetPointingError(t).Dec;
            sum2 += d * d;
        }

        Math.Sqrt(sum2 / n).Should().BeApproximately(0.1, 0.01);
    }

    [Test]
    public void Tracking_off_drifts_at_sidereal_rate()
    {
        var (mount, clock) = Create(new MountSimConfig { DeclinationDeg = 60 });
        mount.SetTracking(false);
        clock.Advance(TimeSpan.FromSeconds(2));
        mount.GetSnapshot().IsTracking.Should().BeFalse();
        mount.GetPointingError().Ra.Should().BeApproximately(2 * Sidereal * 0.5, 1e-9);

        mount.SetTracking(true);
        clock.Advance(TimeSpan.FromSeconds(5));
        mount.GetPointingError().Ra.Should().BeApproximately(2 * Sidereal * 0.5, 1e-9);
    }

    [Test]
    public async Task Slewing_ignores_pulses_and_moves_pointing()
    {
        var (mount, clock) = Create(new MountSimConfig());
        mount.StartSlew(new SkyOffset(100, -50), TimeSpan.FromSeconds(10));
        mount.GetSnapshot().IsSlewing.Should().BeTrue();

        await mount.PulseAsync(GuideDirection.North, 1000, default);
        clock.Advance(TimeSpan.FromSeconds(10));

        mount.GetSnapshot().IsSlewing.Should().BeFalse();
        mount.GetPointingError().Ra.Should().BeApproximately(100, 1e-9);
        mount.GetPointingError().Dec.Should().BeApproximately(-50, 1e-9);
    }

    [Test]
    public void Snapshot_reports_state_and_pier_side_override()
    {
        var (mount, _) = Create(new MountSimConfig { DeclinationDeg = 12, GuideRateRa = 0.75, GuideRateDec = 0.25 });
        var s = mount.GetSnapshot();
        s.DeclinationDeg.Should().Be(12);
        s.GuideRateRa.Should().Be(0.75);
        s.GuideRateDec.Should().Be(0.25);
        s.PierSide.Should().Be(PierSide.East);

        mount.MeridianFlip();
        mount.GetSnapshot().PierSide.Should().Be(PierSide.West);
        mount.ReportedPierSideOverride = PierSide.East;
        mount.GetSnapshot().PierSide.Should().Be(PierSide.East);
        mount.PierSide.Should().Be(PierSide.West);

        mount.Park();
        mount.GetSnapshot().IsParked.Should().BeTrue();
        mount.GetSnapshot().IsTracking.Should().BeFalse();
    }

    [Test]
    public async Task Cancelled_pulse_on_real_clock_is_truncated()
    {
        var mount = new SimulatedMount(new MountSimConfig { DeclinationDeg = 0, GuideRateRa = 1.0 }, SystemClock.Instance);
        using var cts = new CancellationTokenSource(50);
        var start = SystemClock.Instance.NowSeconds();

        var act = () => mount.PulseAsync(GuideDirection.East, 5000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var moved = mount.GetPointingError(start + 10).Ra;
        moved.Should().BeLessThan(Sidereal * 2.0);
        moved.Should().BeGreaterThan(0);
    }
}
