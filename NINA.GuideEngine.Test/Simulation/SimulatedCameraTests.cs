// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;
using static NINA.GuideEngine.Test.Simulation.SimTestHelpers;

namespace NINA.GuideEngine.Test.Simulation;

public class SimulatedCameraTests
{
    [Test]
    public async Task Same_seeds_give_identical_frames_different_noise_seed_differs()
    {
        var a = new Simulator(SimulatorScenario.GoodMount);
        var b = new Simulator(SimulatorScenario.GoodMount);
        var c = new Simulator(SimulatorScenario.GoodMount with { Camera = SimulatorScenario.GoodMount.Camera with { NoiseSeed = 1 } });

        for (int i = 0; i < 3; i++)
        {
            var fa = await a.CaptureAsync(2000);
            var fb = await b.CaptureAsync(2000);
            var fc = await c.CaptureAsync(2000);
            fa.Pixels.Should().Equal(fb.Pixels);
            fa.Pixels.SequenceEqual(fc.Pixels).Should().BeFalse();
            a.LastFrameTruth!.Stars.Select(s => s.X).Should().Equal(c.LastFrameTruth!.Stars.Select(s => s.X));
        }
    }

    [Test]
    public async Task Capture_advances_clock_by_exposure_plus_download_and_sets_metadata()
    {
        var sim = new Simulator(Clean());
        var frame = await sim.CaptureAsync(1500);

        sim.VirtualClock!.ElapsedMs.Should().Be(1600);
        frame.ExposureMs.Should().Be(1500);
        frame.FrameNumber.Should().Be(1);
        frame.StartTime.Should().Be(sim.VirtualClock.Epoch);
        frame.Width.Should().Be(1936);
        frame.Height.Should().Be(1216);
        sim.LastFrameTruth!.StartSec.Should().Be(0);
    }

    [Test]
    public async Task Gain_amplifies_the_signal_and_is_reported_through_the_gain_range()
    {
        var sim = new Simulator(Clean() with { Sky = new SkySimConfig { Stars = [] } });
        IGainRange range = sim.Camera;
        range.GainMin.Should().Be(0);
        range.GainMax.Should().Be(100);
        range.CurrentGain.Should().Be(0);

        var low = await sim.Camera.CaptureAsync(new CaptureRequest(1000, Gain: 0), default);
        var high = await sim.Camera.CaptureAsync(new CaptureRequest(1000, Gain: 100), default);
        range.CurrentGain.Should().Be(100);
        await sim.Camera.CaptureAsync(new CaptureRequest(1000), default);
        range.CurrentGain.Should().Be(100, "a request without a gain keeps the current one");
        await sim.Camera.CaptureAsync(new CaptureRequest(1000, Gain: 500), default);
        range.CurrentGain.Should().Be(100, "requests are clamped to the range");

        var all = new IntRect(0, 0, low.Width, low.Height);
        double bias = sim.Camera.Config.BiasAdu;
        double lowSignal = FrameStats(low, all).Mean - bias;
        double highSignal = FrameStats(high, all).Mean - bias;
        (highSignal / lowSignal).Should().BeApproximately(10.0, 0.2, "gain 100 amplifies 10x with the default model");
    }

    [Test]
    public void Background_mean_and_noise_match_the_sensor_model()
    {
        var cfg = new CameraSimConfig { HotPixelCount = 0, ColdPixelCount = 0, SkyBackgroundElectronsPerSec = 40, GainElectronsPerAdu = 0.5, BiasAdu = 500 };
        var sim = new Simulator(Clean() with { Sky = new SkySimConfig { Stars = [] }, Camera = cfg });

        var frame = sim.Camera.Render(0, 1000, 1, IntRect.Empty, 1);
        var (mean, sigma) = FrameStats(frame, new IntRect(0, 0, frame.Width, frame.Height));

        double electrons = 40 + cfg.DarkCurrentElectronsPerSec;
        mean.Should().BeApproximately(500 + electrons / 0.5, 0.1);
        sigma.Should().BeApproximately(Math.Sqrt(electrons + 3.5 * 3.5) / 0.5, 0.05);
    }

    [Test]
    public void Zero_exposure_frame_shows_bias_and_read_noise_only()
    {
        var sim = new Simulator(Clean() with { Sky = new SkySimConfig { Stars = [] } });

        var frame = sim.Camera.Render(0, 0, 1, IntRect.Empty, 1);
        var (mean, sigma) = FrameStats(frame, new IntRect(0, 0, frame.Width, frame.Height));

        mean.Should().BeApproximately(200, 0.05);
        sigma.Should().BeApproximately(3.5, 0.05);
    }

    [Test]
    public void Low_background_uses_poisson_statistics()
    {
        var cfg = new CameraSimConfig { HotPixelCount = 0, ColdPixelCount = 0, SkyBackgroundElectronsPerSec = 2, DarkCurrentElectronsPerSec = 0, ReadNoiseElectrons = 0 };
        var sim = new Simulator(Clean() with { Sky = new SkySimConfig { Stars = [] }, Camera = cfg });

        var frame = sim.Camera.Render(0, 1000, 1, new IntRect(0, 0, 800, 800), 1);
        var (mean, sigma) = FrameStats(frame, frame.Subframe);

        mean.Should().BeApproximately(202, 0.02);
        sigma.Should().BeApproximately(Math.Sqrt(2), 0.02);
    }

    [Test]
    public void Star_centroid_and_flux_match_truth()
    {
        var sim = new Simulator(Clean(7.0) with
        {
            Sky = new SkySimConfig { Stars = [new SimStar(10.3, -7.9, 7.0)], SeeingJitterArcsec = 0 },
        });

        var frame = sim.Camera.Render(0, 1000, 1, IntRect.Empty, 1);
        var truth = sim.LastFrameTruth!.Stars[0];
        var (x, y, flux) = Centroid(frame, truth.X, truth.Y, 14);

        x.Should().BeApproximately(truth.X, 0.03);
        y.Should().BeApproximately(truth.Y, 0.03);
        truth.FluxElectrons.Should().BeApproximately(Math.Pow(10, -0.4 * (7.0 - 20.0)), 1.0);
        flux.Should().BeApproximately(truth.FluxElectrons, truth.FluxElectrons * 0.03);
        truth.InFrame.Should().BeTrue();

        var expected = sim.Camera.SkyToSensor(new SkyOffset(10.3, -7.9), PierSide.East);
        truth.X.Should().BeApproximately(expected.X, 1e-9);
    }

    [TestCase(false, 30.0)]
    [TestCase(true, 30.0)]
    [TestCase(false, -125.0)]
    public async Task Pulses_move_the_star_by_rate_duration_rotation_and_parity(bool mirrored, double angle)
    {
        var scenario = Clean() with
        {
            Mount = new MountSimConfig { DeclinationDeg = 40, GuideRateRa = 0.5, GuideRateDec = 0.5 },
            Camera = Clean().Camera with { Mirrored = mirrored, CameraAngleDeg = angle },
        };
        var sim = new Simulator(scenario);
        double scale = sim.Camera.PixelScale;
        double th = angle * Math.PI / 180;

        var f0 = await sim.CaptureAsync(1000);
        var t0 = sim.LastFrameTruth!.Stars[0];
        var c0 = Centroid(f0, t0.X, t0.Y);

        await sim.Mount.PulseAsync(GuideDirection.West, 2000, default);
        var f1 = await sim.CaptureAsync(1000);
        var t1 = sim.LastFrameTruth!.Stars[0];
        var c1 = Centroid(f1, t1.X, t1.Y);

        // West: pointing moves West, star moves towards +East on the sensor.
        double raPx = 0.5 * SimulatedMount.SiderealArcsecPerSec * Math.Cos(40 * Math.PI / 180) * 2.0 / scale;
        (t1.X - t0.X).Should().BeApproximately(raPx * Math.Cos(th), 1e-9);
        (t1.Y - t0.Y).Should().BeApproximately(raPx * Math.Sin(th), 1e-9);
        (c1.X - c0.X).Should().BeApproximately(raPx * Math.Cos(th), 0.05);
        (c1.Y - c0.Y).Should().BeApproximately(raPx * Math.Sin(th), 0.05);

        await sim.Mount.PulseAsync(GuideDirection.North, 3000, default);
        var f2 = await sim.CaptureAsync(1000);
        var t2 = sim.LastFrameTruth!.Stars[0];
        var c2 = Centroid(f2, t2.X, t2.Y);

        // North: pointing moves North, star moves towards -North on the sensor.
        double decPx = 0.5 * SimulatedMount.SiderealArcsecPerSec * 3.0 / scale;
        var north = mirrored ? (X: Math.Sin(th), Y: -Math.Cos(th)) : (X: -Math.Sin(th), Y: Math.Cos(th));
        (t2.X - t1.X).Should().BeApproximately(-decPx * north.X, 1e-9);
        (t2.Y - t1.Y).Should().BeApproximately(-decPx * north.Y, 1e-9);
        (c2.X - c1.X).Should().BeApproximately(-decPx * north.X, 0.05);
        (c2.Y - c1.Y).Should().BeApproximately(-decPx * north.Y, 0.05);
    }

    [Test]
    public async Task Meridian_flip_reverses_ra_on_sensor_but_not_dec_by_default()
    {
        async Task<(double Dx, double Dy)> Move(Simulator s, GuideDirection d)
        {
            var before = s.TrueStarPositions(s.Now)[0];
            await s.Mount.PulseAsync(d, 1000, default);
            var after = s.TrueStarPositions(s.Now)[0];
            return (after.X - before.X, after.Y - before.Y);
        }

        var sim = new Simulator(Clean());
        var raEast = await Move(sim, GuideDirection.West);
        var decEast = await Move(sim, GuideDirection.North);
        sim.Mount.MeridianFlip();
        var raWest = await Move(sim, GuideDirection.West);
        var decWest = await Move(sim, GuideDirection.North);

        raWest.Dx.Should().BeApproximately(-raEast.Dx, 1e-9);
        raWest.Dy.Should().BeApproximately(-raEast.Dy, 1e-9);
        decWest.Dx.Should().BeApproximately(decEast.Dx, 1e-9);
        decWest.Dy.Should().BeApproximately(decEast.Dy, 1e-9);

        var flipped = new Simulator(Clean() with { Mount = new MountSimConfig { DecGuideReversedOnWestPier = false, DeclinationDeg = 0 } });
        var d0 = await Move(flipped, GuideDirection.North);
        flipped.Mount.MeridianFlip();
        var d1 = await Move(flipped, GuideDirection.North);
        d1.Dx.Should().BeApproximately(-d0.Dx, 1e-9);
    }

    [Test]
    public void Binning_and_subframe()
    {
        var sim = new Simulator(Clean());
        var truth1 = sim.TrueStarPositions(0, 1)[0];

        var frame = sim.Camera.Render(0, 1000, 2, new IntRect(400, 250, 160, 100), 1);

        frame.Width.Should().Be(968);
        frame.Height.Should().Be(608);
        frame.Binning.Should().Be(2);
        frame.Subframe.Should().Be(new IntRect(400, 250, 160, 100));
        frame[0, 0].Should().Be(0);
        frame[399, 260].Should().Be(0);
        frame[450, 260].Should().BeGreaterThan(0);

        var t = sim.LastFrameTruth!.Stars[0];
        t.X.Should().BeApproximately((truth1.X - 0.5) / 2, 1e-9);
        var c = Centroid(frame, t.X, t.Y, 8);
        c.X.Should().BeApproximately(t.X, 0.05);
        c.Y.Should().BeApproximately(t.Y, 0.05);
    }

    [Test]
    public void Hot_and_cold_pixels_form_a_fixed_pattern()
    {
        var sim = new Simulator(SimulatorScenario.HotPixelHeavy);
        var f1 = sim.Camera.Render(0, 1000, 1, IntRect.Empty, 1);
        var f2 = sim.Camera.Render(10, 1000, 1, IntRect.Empty, 2);

        sim.Camera.HotPixels.Should().HaveCount(600);
        foreach (var (x, y, v) in sim.Camera.HotPixels)
        {
            f1[x, y].Should().BeGreaterThanOrEqualTo(v);
            f2[x, y].Should().BeGreaterThanOrEqualTo(v);
        }

        foreach (var (x, y) in sim.Camera.ColdPixels) f1[x, y].Should().Be(0);
        sim.Camera.HotPixels.Count(h => h.Value == sim.Camera.MaxAdu).Should().BeGreaterThan(200);
    }

    [Test]
    public void Bright_stars_saturate_at_max_adu_and_bit_depth_is_respected()
    {
        var sim = new Simulator(Clean(2.0) with { Camera = Clean().Camera with { BitsPerPixel = 12, MaxAdu = 65535 } });
        sim.Camera.MaxAdu.Should().Be(4095);

        var frame = sim.Camera.Render(0, 1000, 1, IntRect.Empty, 1);

        frame.BitsPerPixel.Should().Be(12);
        frame.Pixels.Max().Should().Be(4095);
    }

    [Test]
    public void Clouds_dim_and_occlusions_remove_stars()
    {
        var sim = new Simulator(Clean() with
        {
            Sky = Clean().Sky with
            {
                Transparency = [new TransparencyWindow(10, 20, 0.25)],
                Occlusions = [new StarOcclusion(30, 40, 0)],
            },
        });

        sim.Camera.Render(0, 1000, 1, IntRect.Empty, 1);
        double clear = sim.LastFrameTruth!.Stars[0].FluxElectrons;
        sim.Camera.Render(12, 1000, 1, IntRect.Empty, 2);
        sim.LastFrameTruth!.Stars[0].FluxElectrons.Should().BeApproximately(clear * 0.25, 1e-6 * clear);
        var lost = sim.Camera.Render(32, 1000, 1, IntRect.Empty, 3);
        sim.LastFrameTruth!.Stars[0].FluxElectrons.Should().Be(0);
        var t = sim.LastFrameTruth!.Stars[0];
        Centroid(lost, t.X, t.Y).Flux.Should().BeLessThan(clear * 0.02);
    }

    [Test]
    public async Task Injected_faults_throw_and_consume_time()
    {
        var sim = new Simulator(Clean());
        sim.Camera.InjectFaults(CaptureFault.Timeout);
        var act = () => sim.CaptureAsync(1000);
        await act.Should().ThrowAsync<GuideCameraException>();
        sim.VirtualClock!.ElapsedMs.Should().Be(6000);

        sim.Camera.InjectFaults(CaptureFault.Disconnect);
        await act.Should().ThrowAsync<GuideCameraException>();
        sim.Camera.IsConnected.Should().BeFalse();
        await act.Should().ThrowAsync<GuideCameraException>();
        await sim.Camera.ReconnectAsync(default);
        (await sim.CaptureAsync(1000)).Should().NotBeNull();
    }

    [Test]
    public async Task Random_failures_are_seeded()
    {
        async Task<string> Pattern()
        {
            var sim = new Simulator(Clean() with { Camera = Clean().Camera with { ExposureFailureProbability = 0.3, SensorWidth = 64, SensorHeight = 64 } });
            var s = "";
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    await sim.CaptureAsync(100);
                    s += ".";
                }
                catch (GuideCameraException)
                {
                    s += "x";
                }
            }

            return s;
        }

        var p = await Pattern();
        p.Should().Be(await Pattern());
        p.Count(ch => ch == 'x').Should().BeInRange(4, 24);
    }

    [Test]
    public void Seeing_motion_averages_down_with_exposure_time()
    {
        var sky = new SimulatedSky(new SkySimConfig { SeeingJitterArcsec = 0.8, SeeingCoherenceMs = 50 }, [new SimStar(0, 0, 8)]);
        double Sigma(double exposure)
        {
            double s2 = 0;
            int n = 4000;
            for (int i = 0; i < n; i++)
            {
                var o = sky.SeeingOffset(0, i * exposure, (i + 1) * exposure);
                s2 += o.Ra * o.Ra + o.Dec * o.Dec;
            }

            return Math.Sqrt(s2 / (2 * n));
        }

        Sigma(1.0).Should().BeApproximately(0.8, 0.03);
        Sigma(4.0).Should().BeApproximately(0.4, 0.015);
    }

    [Test]
    public void Seeing_is_partially_correlated_between_stars()
    {
        var sky = new SimulatedSky(new SkySimConfig { SeeingJitterArcsec = 1, SeeingCommonFraction = 0.5 }, [new SimStar(0, 0, 8), new SimStar(100, 0, 8)]);
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < 4000; i++)
        {
            double a = sky.SeeingOffset(0, i, i + 1).Ra, b = sky.SeeingOffset(1, i, i + 1).Ra;
            sab += a * b;
            saa += a * a;
            sbb += b * b;
        }

        (sab / Math.Sqrt(saa * sbb)).Should().BeApproximately(0.5, 0.05);
    }

    [Test]
    public void A_slow_seeing_layer_averages_down_only_beyond_its_cells_and_moves_all_stars_together()
    {
        SkySimConfig Config(params SeeingLayer[] layers) => new() { SeeingJitterArcsec = 0.8, SeeingLayers = layers };
        SimStar[] stars = [new SimStar(0, 0, 8), new SimStar(100, 0, 8)];
        var fast = new SimulatedSky(Config(), stars);
        var slow = new SimulatedSky(Config(new SeeingLayer(8.0, 0.3)), stars);

        // the layer, common to both stars, on top of the unchanged fast motion
        var layer = new List<double>();
        for (int i = 0; i < 4000; i++)
        {
            var a = slow.SeeingOffset(0, i, i + 1) - fast.SeeingOffset(0, i, i + 1);
            var b = slow.SeeingOffset(1, i, i + 1) - fast.SeeingOffset(1, i, i + 1);
            b.Ra.Should().BeApproximately(a.Ra, 1e-12);
            b.Dec.Should().BeApproximately(a.Dec, 1e-12);
            layer.Add(a.Ra);
        }

        double Sigma(double exposure)
        {
            var layerOnly = new SimulatedSky(new SkySimConfig { SeeingJitterArcsec = 0, SeeingLayers = [new SeeingLayer(8.0, 0.3)] }, stars);
            double s2 = 0;
            const int n = 16000;
            for (int i = 0; i < n; i++)
            {
                var o = layerOnly.SeeingOffset(0, i * exposure, (i + 1) * exposure);
                s2 += o.Ra * o.Ra + o.Dec * o.Dec;
            }

            return Math.Sqrt(s2 / (2 * n));
        }

        // a 1 s mean inside a cell is the cell's value; over four cells the scatter halves
        Sigma(1.0).Should().BeApproximately(0.3, 0.015);
        Sigma(32.0).Should().BeApproximately(0.15, 0.008);

        // successive 1 s frames share a cell 7 times in 8, where the fast motion leaves them independent
        double mean = layer.Average(), c0 = 0, c1 = 0;
        for (int i = 0; i < layer.Count; i++)
        {
            c0 += (layer[i] - mean) * (layer[i] - mean);
            if (i > 0) c1 += (layer[i] - mean) * (layer[i - 1] - mean);
        }

        (c1 / c0).Should().BeApproximately(1 - 1.0 / 8, 0.04);
    }

    [TestCaseSource(typeof(SimulatorScenario), nameof(SimulatorScenario.Presets))]
    public async Task Presets_render(SimulatorScenario scenario)
    {
        var sim = new Simulator(scenario);
        var frame = await sim.CaptureAsync(2000);
        sim.Sky.Stars.Should().HaveCount(25);
        sim.LastFrameTruth!.Stars.Count(s => s.InFrame).Should().Be(25);
        int bright = sim.BrightestStarIndex();
        bright.Should().BeGreaterThanOrEqualTo(0);
        var t = sim.LastFrameTruth.Stars[bright];
        var c = Centroid(frame, t.X, t.Y, 10);
        c.X.Should().BeApproximately(t.X, 0.3);
        c.Y.Should().BeApproximately(t.Y, 0.3);
    }

    [Test]
    [Category("Performance")]
    public void Full_frame_render_is_fast()
    {
        var sim = new Simulator(SimulatorScenario.GoodMount);
        sim.Camera.Render(0, 2000, 1, IntRect.Empty, 1); // warm-up
        var sw = Stopwatch.StartNew();
        const int n = 5;
        for (int i = 0; i < n; i++) sim.Camera.Render(i * 3, 2000, 1, IntRect.Empty, i + 2);
        double ms = sw.Elapsed.TotalMilliseconds / n;
        TestContext.Out.WriteLine($"1936x1216 render: {ms:F1} ms/frame");
        ms.Should().BeLessThan(400);
    }

    [Test]
    public async Task Closed_loop_proportional_guiding_beats_unguided()
    {
        // Minimal controller using the known sky→sensor mapping: proves the loop closes physically.
        var scenario = SimulatorScenario.PoorPeriodicError;
        var guided = new Simulator(scenario);
        var unguided = new Simulator(scenario);
        int star = guided.BrightestStarIndex();
        var cam = guided.Camera;
        double scale = cam.PixelScale;
        double th = scenario.Camera.CameraAngleDeg * Math.PI / 180;
        double cosDec = Math.Cos(scenario.Mount.DeclinationDeg * Math.PI / 180);
        double raSpeed = 0.5 * SimulatedMount.SiderealArcsecPerSec * cosDec, decSpeed = 0.5 * SimulatedMount.SiderealArcsecPerSec;

        var f0 = await guided.CaptureAsync(2000);
        var lock0 = Centroid(f0, guided.LastFrameTruth!.Stars[star].X, guided.LastFrameTruth.Stars[star].Y);
        var ref0 = guided.LastFrameTruth.MeanPointingError; // the lock position corresponds to this pointing
        double sumG = 0, sumU = 0;
        int n = 0;
        for (int i = 0; i < 150; i++)
        {
            var f = await guided.CaptureAsync(2000);
            var t = guided.LastFrameTruth!.Stars[star];
            var c = Centroid(f, t.X, t.Y);
            double dx = (c.X - lock0.X) * scale, dy = (c.Y - lock0.Y) * scale;

            // star displacement along East/North (arcsec); star moved East ⇒ pointing moved West ⇒ pulse East (same for North).
            double east = dx * Math.Cos(th) + dy * Math.Sin(th);
            double north = -dx * Math.Sin(th) + dy * Math.Cos(th);
            int raMs = (int)Math.Round(Math.Abs(0.7 * east) / raSpeed * 1000);
            int decMs = (int)Math.Round(Math.Abs(0.7 * north) / decSpeed * 1000);
            if (raMs > 20) await guided.Mount.PulseAsync(east > 0 ? GuideDirection.East : GuideDirection.West, Math.Min(raMs, 2500), default);
            if (decMs > 20) await guided.Mount.PulseAsync(north > 0 ? GuideDirection.North : GuideDirection.South, Math.Min(decMs, 2500), default);

            unguided.VirtualClock!.AdvanceTo(guided.VirtualClock!.Elapsed);
            if (i < 20) continue;
            var eg = guided.LastFrameTruth.MeanPointingError - ref0;
            var eu = unguided.TruePointingError() - ref0;
            sumG += eg.Ra * eg.Ra + eg.Dec * eg.Dec;
            sumU += eu.Ra * eu.Ra + eu.Dec * eu.Dec;
            n++;
        }

        double rmsGuided = Math.Sqrt(sumG / n), rmsUnguided = Math.Sqrt(sumU / n);
        TestContext.Out.WriteLine($"true RMS guided {rmsGuided:F2}\" unguided {rmsUnguided:F2}\"");
        rmsGuided.Should().BeLessThan(rmsUnguided / 4);
        rmsGuided.Should().BeLessThan(1.0);
    }
}
