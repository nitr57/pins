// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.MultiStar;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider.Internal;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class FitsReaderTests
{
    private static byte[] Fits(int bitpix, int w, int h, Func<int, int, double> value, params string[] extraCards)
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T / conforms",
            $"BITPIX  = {bitpix,20}",
            "NAXIS   =                    2",
            $"NAXIS1  = {w,20} / width",
            $"NAXIS2  = {h,20}",
        };
        if (bitpix == 16)
        {
            cards.Add("BZERO   =                32768");
            cards.Add("BSCALE  =                    1");
        }

        cards.AddRange(extraCards);
        cards.Add("END");
        var header = new StringBuilder();
        foreach (var c in cards)
        {
            header.Append(c.PadRight(80));
        }

        while (header.Length % 2880 != 0)
        {
            header.Append(' ');
        }

        int bytes = Math.Abs(bitpix) / 8;
        var data = new byte[((w * h * bytes) + 2879) / 2880 * 2880];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double v = value(x, y);
                if (bitpix == 8)
                {
                    data[i] = (byte)v;
                }
                else
                {
                    BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), (short)(v - 32768));
                }
            }
        }

        return Encoding.ASCII.GetBytes(header.ToString()).Concat(data).ToArray();
    }

    [Test]
    public void Reads_16_bit_with_bzero()
    {
        var bytes = Fits(16, 7, 5, (x, y) => x * 1000 + y * 7 + 3, "OBJECT  = 'guide / star'        / a comment");
        var f = FitsReader.Read(bytes, ".fits");
        f.Width.Should().Be(7);
        f.Height.Should().Be(5);
        f[0, 0].Should().Be(3);
        f[6, 4].Should().Be(6 * 1000 + 4 * 7 + 3);
        f.BitsPerPixel.Should().Be(16);
    }

    [Test]
    public void Reads_8_bit()
    {
        var f = FitsReader.Read(Fits(8, 4, 3, (x, y) => x + 10 * y), ".fits");
        f[3, 2].Should().Be(23);
        f.BitsPerPixel.Should().Be(8);
    }

    [Test]
    public void Reads_zlib_compressed_blob()
    {
        var raw = Fits(16, 16, 16, (x, y) => 40000 + x - y);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        var f = FitsReader.Read(ms.ToArray(), ".fits.z");
        f[15, 0].Should().Be(40015);
        f[0, 15].Should().Be(39985);
    }

    [Test]
    public void Multi_hdu_roundtrip_keeps_frames_and_keywords()
    {
        var a = new GuideFrame(5, 4);
        var b = new GuideFrame(5, 4);
        for (int i = 0; i < 20; i++)
        {
            a.Pixels[i] = (ushort)(i * 100);
            b.Pixels[i] = (ushort)(65535 - i);
        }

        using var ms = new MemoryStream();
        GuideEngine.Imaging.FitsWriter.Write(ms, [(a, new Dictionary<string, string> { ["EXPOSURE"] = "0.5" }), (b, new Dictionary<string, string> { ["EXPOSURE"] = "2" })]);
        var hdus = FitsReader.ReadAll(ms.ToArray());
        hdus.Should().HaveCount(2);
        hdus[0].Frame.Pixels.Should().Equal(a.Pixels);
        hdus[1].Frame.Pixels.Should().Equal(b.Pixels);
        hdus[1].Header["EXPOSURE"].Should().Be("2");
        hdus[1].Header["XTENSION"].Should().Be("IMAGE");
    }

    [Test]
    public void Reads_the_real_phd2_dark_library_if_present()
    {
        const string path = "/src/../phd2-dark-sample/PHD2_dark_lib_2.fit";
        var candidates = new[] { path, Environment.GetEnvironmentVariable("PHD2_DARK_LIB") ?? string.Empty }.Where(File.Exists).ToList();
        if (candidates.Count == 0)
        {
            Assert.Ignore("no PHD2 dark library sample available");
        }

        var hdus = FitsReader.ReadAll(File.ReadAllBytes(candidates[0]));
        hdus.Should().HaveCount(8);
        hdus.Should().OnlyContain(h => h.Frame.Width == 1280 && h.Frame.Height == 960);
        hdus.Select(h => h.Header["EXPOSURE"]).Should().Contain("2.");
    }

    [Test]
    public void Rejects_truncated_data()
    {
        var bytes = Fits(16, 100, 100, (_, _) => 1);
        FluentActions.Invoking(() => FitsReader.Read(bytes.Take(4000).ToArray(), ".fits")).Should().Throw<InvalidDataException>();
    }
}

[TestFixture]
public class InternalGuiderOptionsTests
{
    private delegate bool TryGetString(Guid id, string key, out string value);

    private static (InternalGuiderOptions Options, Mock<IGuiderSettings> Guider) Create(Dictionary<string, string>? stored = null)
    {
        var guider = new Mock<IGuiderSettings>();
        var profile = new Mock<IProfile>();
        var store = stored ?? new Dictionary<string, string>();
        var plugin = new Mock<IPluginSettings>();
        plugin.Setup(p => p.SetValue(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<Guid, string, string>((_, k, v) => store[k] = v);
        plugin.Setup(p => p.TryGetValue(It.IsAny<Guid>(), It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Returns(new TryGetString((Guid _, string k, out string v) => store.TryGetValue(k, out v!)));
        profile.SetupGet(p => p.PluginSettings).Returns(plugin.Object);
        profile.SetupGet(p => p.GuiderSettings).Returns(guider.Object);
        profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        var service = new Mock<IProfileService>();
        service.SetupGet(s => s.ActiveProfile).Returns(profile.Object);
        return (new InternalGuiderOptions(service.Object, InternalGuiderOptions.SettingsId), guider);
    }

    [Test]
    public void The_plugins_simulator_setting_selects_the_simulator()
    {
        // pins-guider chose the simulator as its guide camera driver
        var (o, _) = Create(new Dictionary<string, string> { ["GuideCameraDriver"] = "simulator" });

        o.GetString("GuideSource").Should().Be(InternalGuiderOptions.SimulatorSource);
        o.IsSimulator.Should().BeTrue();
    }

    [Test]
    public void The_plugins_camera_driver_setting_keeps_the_guide_camera()
    {
        var (o, _) = Create(new Dictionary<string, string> { ["GuideCameraDriver"] = "indi_asi_ccd" });

        o.GetString("GuideSource").Should().Be(InternalGuiderOptions.GuideCameraSource);
        o.IsSimulator.Should().BeFalse();
    }

    [Test]
    public void Defaults_without_phd2_settings()
    {
        var (o, _) = Create();
        o.GetString("GuideSource").Should().Be(InternalGuiderOptions.GuideCameraSource);
        o.IsSimulator.Should().BeFalse();
        o.GetDouble("ExposureSeconds").Should().Be(2);
        var s = o.ToEngineSettings();
        s.RaAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Hysteresis);
        s.DecAlgorithm.Kind.Should().Be(GuideAlgorithmKind.ResistSwitch);
        s.AutoCalibrationStep.Should().BeTrue();
        s.MaxRaDurationMs.Should().Be(2500);
        s.MinPulseMs.Should().Be(20);
        s.Gain.Should().BeNull();
    }

    [Test]
    public void Falls_back_to_the_profiles_phd2_settings()
    {
        var (o, g) = Create();
        g.SetupGet(x => x.PHD2FocalLength).Returns(248);
        g.SetupGet(x => x.PHD2ExposureMs).Returns(1500);
        g.SetupGet(x => x.PHD2CameraGain).Returns(48);
        g.SetupGet(x => x.PHD2GuideAlgorithmRA).Returns("Lowpass2");
        g.SetupGet(x => x.PHD2RALowpass2Aggressiveness).Returns(65);
        g.SetupGet(x => x.PHD2DecGuideMode).Returns("North");
        g.SetupGet(x => x.PHD2MaxRADuration).Returns(3000);

        var s = o.ToEngineSettings();
        s.FocalLengthMm.Should().Be(248);
        s.ExposureMs.Should().Be(1500);
        s.Gain.Should().BeNull("PHD2 gain is a percentage and is not carried over");
        s.RaAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Lowpass2);
        s.RaAlgorithm.Parameters!["aggressiveness"].Should().Be(65);
        s.DecGuideMode.Should().Be(DecGuideMode.North);
        s.MaxRaDurationMs.Should().Be(3000);
    }

    [Test]
    public void Explicit_setting_wins_over_phd2_fallback_and_is_validated()
    {
        var (o, g) = Create();
        g.SetupGet(x => x.PHD2FocalLength).Returns(248);
        o.TrySet("FocalLengthMm", "180", out _).Should().BeTrue();
        o.FocalLengthMm.Should().Be(180);
        o.TrySet("FocalLengthMm", "-5", out var err).Should().BeFalse();
        err.Should().Contain("between");
        o.TrySet("DecGuideMode", "south", out _).Should().BeTrue();
        o.GetString("DecGuideMode").Should().Be("South");
        o.TrySet("DecGuideMode", "sideways", out _).Should().BeFalse();
        o.TrySet("ExposureSeconds", "2,5", out _).Should().BeTrue();
        o.GetDouble("ExposureSeconds").Should().Be(2.5);
        o.TrySet("Nope", "1", out _).Should().BeFalse();
    }

    [Test]
    public void Predictive_algorithm_gets_no_phd2_min_move()
    {
        var (o, _) = Create();
        o.TrySet("RaAlgorithm", "Predictive", out _).Should().BeTrue();
        o.TrySet("DecAlgorithm", "predictive", out _).Should().BeTrue();
        o.TrySet("RaMinMove", "0.3", out _).Should().BeTrue();

        var s = o.ToEngineSettings();
        s.RaAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Predictive);
        s.DecAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Predictive);
        (s.RaAlgorithm.Parameters ?? new Dictionary<string, double>()).Should().NotContainKey("minMove");
    }

    [TestCase("Ra")]
    [TestCase("Dec")]
    public void Algorithm_parameters_are_shown_only_for_the_algorithms_that_read_them(string axis)
    {
        // setting -> engine parameter it becomes (see InternalGuiderOptions.Algorithm)
        var parameters = new Dictionary<string, string>
        {
            [axis + "Aggression"] = "aggression",
            [axis + "Hysteresis"] = "hysteresis",
            [axis + "Lowpass2Aggressiveness"] = "aggressiveness",
            [axis + "MinMove"] = "minMove",
            [axis + "CorrectionPace"] = "pace",
        };
        if (axis == "Dec")
        {
            parameters["DecFastSwitch"] = "fastSwitch";
        }
        else
        {
            parameters["PeriodicErrorPrediction"] = "periodicError";
            parameters["WormTeeth"] = "wormTeeth";
        }

        var described = Create().Options.Describe();
        var algorithm = described.Single(x => x.Name == axis + "Algorithm");
        algorithm.Basic.Should().BeTrue("the algorithm explains why its parameters come and go");
        foreach (var d in described.Where(x => !string.IsNullOrEmpty(x.DependsOn)))
        {
            described.Should().Contain(x => x.Name == d.DependsOn && x.Type == "enum");
            d.AppliesTo.Should().NotBeEmpty().And.BeSubsetOf(described.Single(x => x.Name == d.DependsOn).Options);
        }

        foreach (string name in algorithm.Options!)
        {
            var (o, _) = Create();
            o.TrySet(axis + "Algorithm", name, out _).Should().BeTrue();
            o.TrySet(axis + "MinMove", "0.3", out _).Should().BeTrue();
            var settings = o.ToEngineSettings();
            var engine = (axis == "Ra" ? settings.RaAlgorithm : settings.DecAlgorithm).Parameters ?? new Dictionary<string, double>();
            var shown = parameters.Keys.Where(p => described.Single(x => x.Name == p).AppliesTo!.Contains(name)).Select(p => parameters[p]);
            engine.Keys.Should().BeEquivalentTo(shown, $"{axis} {name} shows exactly the parameters it reads");
        }
    }

    [Test]
    public void Predictive_state_is_reported_for_learning_algorithms_only()
    {
        InternalGuider.ToDto(new HysteresisAlgorithm(), 1.5).Should().BeNull();
        var predictive = new PredictiveAlgorithm();
        var fresh = InternalGuider.ToDto(predictive, 1.5)!;
        fresh.Name.Should().Be("Predictive");
        fresh.Phase.Should().Be("Learning");
        fresh.Progress.Should().Be(0);

        var rng = new Random(3);
        for (int k = 0; k < 150; k++)
        {
            double c = predictive.Result(0.3 * (rng.NextDouble() - 0.5), DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2 * k));
            predictive.CorrectionApplied(c);
        }

        var learned = InternalGuider.ToDto(predictive, 1.5)!;
        learned.PeriodicError.Should().BeNull("Dec has no worm");
        learned.Phase.Should().Be("Adapted");
        learned.Progress.Should().Be(1);
        learned.FramesLearned.Should().Be(149);
        learned.SeeingPx.Should().BeGreaterThan(0);
        learned.SeeingArcsec.Should().BeApproximately(learned.SeeingPx * 1.5, 1e-9);
        learned.DriftArcsecPerMin.Should().BeApproximately(learned.DriftPxPerMin * 1.5, 1e-9);
        learned.Gain.Should().BeInRange(0, 1);
    }

    [Test]
    public void Predictive_RA_reports_its_periodic_error_and_its_settings_reach_the_engine()
    {
        var ra = InternalGuider.ToDto(new PredictiveAlgorithm(periodicError: true), 1.5)!;
        ra.PeriodicError.Should().NotBeNull();
        ra.PeriodicError!.Phase.Should().Be("Learning");
        ra.PeriodicError.AmplitudeArcsec.Should().BeNull("nothing fitted yet");

        var (o, _) = Create();
        o.TrySet("RaAlgorithm", "Predictive", out _).Should().BeTrue();
        o.TrySet("WormTeeth", "180", out _).Should().BeTrue();
        o.TrySet("PeriodicErrorPrediction", "false", out _).Should().BeTrue();
        var p = o.ToEngineSettings().RaAlgorithm.Parameters!;
        p["wormTeeth"].Should().Be(180);
        p["periodicError"].Should().Be(0);

        var forget = o.Describe().Single(x => x.Name == "ForgetPeriodicError");
        forget.Type.Should().Be("action");
        o.TrySet("ForgetPeriodicError", "true", out _).Should().BeTrue();
        o.GetString("ForgetPeriodicError").Should().BeEmpty("an action stores nothing");
    }

    [TestCase("Ra")]
    [TestCase("Dec")]
    public void Predictive_correction_pace_reaches_the_engine_per_axis(string axis)
    {
        var (o, _) = Create();
        o.TrySet(axis + "Algorithm", "Predictive", out _).Should().BeTrue();
        Pace().Should().Be(0.5, "the plugin default takes an offset out over a few frames");

        o.TrySet(axis + "CorrectionPace", "0.7", out _).Should().BeTrue();
        Pace().Should().Be(0.7);
        o.Describe().Single(x => x.Name == axis + "CorrectionPace").Basic.Should().BeFalse("a fixed value that works everywhere; changed only for tests");

        double Pace()
        {
            var s = o.ToEngineSettings();
            return (axis == "Ra" ? s.RaAlgorithm : s.DecAlgorithm).Parameters!["pace"];
        }
    }

    [Test]
    public void Pulse_model_is_on_by_default_and_reaches_the_engine()
    {
        var (o, _) = Create();
        o.ToEngineSettings().PulseModel.Should().BeTrue("it starts as calibrated and changes only what the dithers clearly show");
        o.Describe().Single(x => x.Name == "PulseModel").Basic.Should().BeFalse();

        o.TrySet("PulseModel", "false", out _).Should().BeTrue();
        o.ToEngineSettings().PulseModel.Should().BeFalse();
    }

    [Test]
    public void Describe_lists_all_settings_with_basic_flags()
    {
        var (o, _) = Create();
        var d = o.Describe();
        d.Should().HaveCount(InternalGuiderOptions.Definitions.Count);
        d.Should().Contain(x => x.Name == "GuideSource" && x.Basic && x.RequiresReconnect);
        d.Should().NotContain(x => x.Name == "GuideCameraDriver" || x.Name == "GuideCameraDevice", "the guide camera slot chooses the camera");
        d.Single(x => x.Name == "DecGuideMode").Options.Should().Equal("Auto", "North", "South", "Off", "Drift");
    }

    [Test]
    public void Dec_guide_mode_drift_is_an_option_and_reaches_the_engine()
    {
        var (o, g) = Create();
        o.ToEngineSettings().DecGuideMode.Should().Be(DecGuideMode.Auto, "the default stays PHD2's Auto");
        g.SetupGet(x => x.PHD2DecGuideMode).Returns("South");
        o.TrySet("DecGuideMode", "drift", out _).Should().BeTrue();
        o.GetString("DecGuideMode").Should().Be("Drift");
        o.ToEngineSettings().DecGuideMode.Should().Be(DecGuideMode.Drift);
        o.Describe().Single(x => x.Name == "DecGuideMode").Description.Should().Contain("Drift follows the drift");
    }

    [Test]
    public void Status_shows_the_dec_drift_of_the_drift_mode_only()
    {
        InternalGuider.ToDecDriftDto(null, 1.5).Should().BeNull("the other Dec guide modes have no drift state");

        var both = InternalGuider.ToDecDriftDto(new DecDirectionState { Direction = DecGuideDirection.Both }, 1.5)!;
        both.Direction.Should().Be(AdvancedDecDriftDirections.Both);
        both.DriftArcsecPerMin.Should().BeNull("not enough data yet");
        both.SafetyValveOpen.Should().BeFalse();

        var south = InternalGuider.ToDecDriftDto(new DecDirectionState { Direction = DecGuideDirection.South, DriftPxPerSec = 0.01, ValveOpen = true }, 1.5)!;
        south.Direction.Should().Be(AdvancedDecDriftDirections.South);
        south.DriftArcsecPerMin.Should().BeApproximately(0.9, 1e-9);
        south.SafetyValveOpen.Should().BeTrue();

        // the shape agreed with the UI: decDrift { direction, driftArcsecPerMin, safetyValveOpen }
        var json = JsonSerializer.SerializeToNode(new AdvancedGuiderStatus { DecDrift = south }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        json["decDrift"]!.AsObject().Select(p => p.Key).Should().Equal("direction", "driftArcsecPerMin", "safetyValveOpen");
    }

    [Test]
    public void Nina_guide_step_carries_the_star_metrics_and_signed_durations()
    {
        var step = InternalGuider.ToNinaGuideStep(new GuideStepEvent(DateTimeOffset.UnixEpoch)
        {
            Frame = 7,
            RaDistanceRaw = 0.4,
            DecDistanceRaw = -0.2,
            RaDuration = 120,
            RaDirection = GuideDirection.East,
            DecDuration = 80,
            DecDirection = GuideDirection.South,
            StarMass = 5400,
            Snr = 32.5,
            Hfd = 2.1,
        });

        // NINA's guide graph and the ninaAPI step history read these only from a PHD2 step
        step.SNR.Should().Be(32.5);
        step.StarMass.Should().Be(5400);
        step.HFD.Should().Be(2.1);
        step.Frame.Should().Be(7);
        step.RADistanceRaw.Should().Be(0.4);
        step.DECDistanceRaw.Should().Be(-0.2);
        step.RADuration.Should().Be(-120, "East is negative");
        step.DECDuration.Should().Be(-80, "South is negative");

        var westNorth = InternalGuider.ToNinaGuideStep(new GuideStepEvent(DateTimeOffset.UnixEpoch)
        {
            RaDuration = 50,
            RaDirection = GuideDirection.West,
            DecDuration = 30,
            DecDirection = GuideDirection.North,
        });
        westNorth.RADuration.Should().Be(50);
        westNorth.DECDuration.Should().Be(30);
    }
}

[TestFixture]
public class InternalGuiderLifecycleTests
{
    [Test]
    public async Task When_the_guide_camera_disconnects_the_guider_disconnects_without_waiting_for_itself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pins-internal-guider-" + Guid.NewGuid().ToString("N"));
        var (_, service, _) = IncidentSettingsTests.Create();
        var info = new CameraInfo { Connected = true, Name = "Guide camera", DeviceId = "guide", XSize = 640, YSize = 480, BitDepth = 12, PixelSize = 3.75 };
        var slot = new Mock<IGuideCameraMediator>();
        slot.Setup(m => m.GetInfo()).Returns(() => info);
        slot.Setup(m => m.TryRegisterCaptureBlock(It.IsAny<object>())).Returns(true);
        Func<object, EventArgs, Task>? cameraDisconnected = null;
        slot.SetupAdd(m => m.Disconnected += It.IsAny<Func<object, EventArgs, Task>>())
            .Callback<Func<object, EventArgs, Task>>(h => cameraDisconnected = h);
        try
        {
            var guider = new InternalGuider(service.Object, new Mock<ITelescopeMediator>().Object, slot.Object, dir,
                Path.Combine(dir, "periodic-error.json"), Path.Combine(dir, "pulse-model.json"));
            foreach (var (name, value) in new[] { ("SaveGuideLog", "false"), ("ReuseCalibration", "false"), ("UseDarkLibrary", "false") })
            {
                guider.TrySetSetting(name, value, out _).Should().BeTrue();
            }

            (await guider.Connect(CancellationToken.None)).Should().BeTrue();

            // GuiderVM answers Connected = false by calling Disconnect, synchronously from the property change
            var answered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            guider.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IGuider.Connected) && !guider.Connected)
                {
                    var started = DateTime.UtcNow;
                    guider.Disconnect();
                    answered.TrySetResult(DateTime.UtcNow - started);
                }
            };

            info.Connected = false;
            cameraDisconnected.Should().NotBeNull("the guider watches its guide camera");
            await cameraDisconnected!(slot.Object, EventArgs.Empty);

            var waited = await answered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            waited.Should().BeLessThan(TimeSpan.FromSeconds(5), "the listener's Disconnect must not wait for the disconnect that notified it");
            guider.Connected.Should().BeFalse();
            slot.Verify(m => m.ReleaseCaptureBlock(It.IsAny<object>()), Times.AtLeastOnce(), "the guide camera is given back");
            slot.Verify(m => m.Disconnect(), Times.Never, "the guider never disconnects the guide camera itself");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task While_a_dark_library_is_built_the_guide_loop_waits_and_a_disconnect_ends_the_build_first()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pins-internal-guider-" + Guid.NewGuid().ToString("N"));
        var (_, service, _) = IncidentSettingsTests.Create();
        var info = new CameraInfo { Connected = true, Name = "Guide camera", DeviceId = "guide", XSize = 640, YSize = 480, BitDepth = 12, PixelSize = 3.75 };
        var slot = new Mock<IGuideCameraMediator>();
        slot.Setup(m => m.GetInfo()).Returns(() => info);
        slot.Setup(m => m.TryRegisterCaptureBlock(It.IsAny<object>())).Returns(true);
        var darkExposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        // a dark exposure that lasts until it is cancelled
        slot.Setup(m => m.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>>((_, ct, _) =>
            {
                ct.Register(() => { lock (calls) calls.Add("dark cancelled"); });
                darkExposing.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct);
            });
        slot.Setup(m => m.ReleaseCaptureBlock(It.IsAny<object>())).Callback(() => { lock (calls) calls.Add("release"); });
        try
        {
            var guider = new InternalGuider(service.Object, new Mock<ITelescopeMediator>().Object, slot.Object, dir,
                Path.Combine(dir, "periodic-error.json"), Path.Combine(dir, "pulse-model.json"));
            foreach (var (name, value) in new[] { ("SaveGuideLog", "false"), ("ReuseCalibration", "false"), ("UseDarkLibrary", "false") })
            {
                guider.TrySetSetting(name, value, out _).Should().BeTrue();
            }

            (await guider.Connect(CancellationToken.None)).Should().BeTrue();
            var build = guider.BuildDarkLibrary(1, 1, 1, CancellationToken.None);
            await darkExposing.Task.WaitAsync(TimeSpan.FromSeconds(10));

            (await guider.StartLooping(CancellationToken.None)).Should().BeFalse("the dark exposure has the guide camera");
            (await guider.AutoSelectGuideStar()).Should().BeFalse();
            (await guider.StartGuiding(false, null, CancellationToken.None)).Should().BeFalse();
            slot.Verify(m => m.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Once,
                "only the dark exposure");

            await Task.Run(guider.Disconnect);

            (await build.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse("the disconnect cancelled the build");
            lock (calls)
            {
                calls.Should().Equal(["dark cancelled", "release"], "the dark exposure ends before the guide camera is given back");
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Test]
    public async Task Disconnect_and_connect_share_the_connect_lock()
    {
        using var t = new GuiderFixture();
        t.Guider.TrySetSetting("GuideSource", InternalGuiderOptions.SimulatorSource, out _).Should().BeTrue();
        t.Guider.TrySetSetting("SaveGuideLog", "false", out _).Should().BeTrue();
        t.Guider.TrySetSetting("ReuseCalibration", "false", out _).Should().BeTrue();

        (await t.Guider.Connect(CancellationToken.None)).Should().BeTrue();
        await Task.Run(t.Guider.Disconnect);
        t.Guider.Connected.Should().BeFalse();

        // the disconnect released the lock: a new connect neither hangs nor finds the old session
        (await t.Guider.Connect(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
        t.Guider.Connected.Should().BeTrue();
        t.Guider.GetStatus().State.Should().Be(AdvancedGuiderStates.Stopped);
        await Task.Run(t.Guider.Disconnect);
        t.Guider.Connected.Should().BeFalse();
    }

    [Test]
    public async Task Selects_a_guide_star_by_position()
    {
        using var t = new GuiderFixture();
        t.Guider.TrySetSetting("GuideSource", InternalGuiderOptions.SimulatorSource, out _).Should().BeTrue();
        t.Guider.TrySetSetting("SaveGuideLog", "false", out _).Should().BeTrue();
        t.Guider.TrySetSetting("ReuseCalibration", "false", out _).Should().BeTrue();
        t.Guider.TrySetSetting("ExposureSeconds", "0.2", out _).Should().BeTrue();
        (await t.Guider.Connect(CancellationToken.None)).Should().BeTrue();
        try
        {
            (await t.Guider.SelectGuideStar(100, 100, CancellationToken.None)).Error.Should().Be(AdvancedStarSelectionErrors.NotLooping);

            (await t.Guider.StartLooping(CancellationToken.None)).Should().BeTrue();
            (await t.Guider.AutoSelectGuideStar().WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
            AdvancedGuideStar? secondary = null;
            for (int i = 0; i < 100 && secondary is null; i++)
            {
                await Task.Delay(100);
                secondary = t.Guider.GetLatestFrame()?.Stars.FirstOrDefault(s => !s.IsPrimary);
            }

            secondary.Should().NotBeNull("the auto-selection found secondary stars");

            // tapped a little off the star
            var r = await t.Guider.SelectGuideStar(secondary!.X + 2, secondary.Y - 1, CancellationToken.None);

            r.Success.Should().BeTrue(r.Message);
            r.Error.Should().BeNull();
            // the unguided field drifts a little between the frame read here and the one the selection is made on
            r.Star!.X.Should().BeApproximately(secondary.X, 5);
            r.Star.Y.Should().BeApproximately(secondary.Y, 5);
            r.SecondaryStars.Should().BeGreaterThan(0);
            t.Guider.GetStatus().State.Should().Be(AdvancedGuiderStates.Selected);
            (await t.Guider.SelectGuideStar(-50, -50, CancellationToken.None)).Error.Should().Be(AdvancedStarSelectionErrors.NoStar);
        }
        finally
        {
            await Task.Run(t.Guider.Disconnect);
        }
    }
}

[TestFixture]
public class ContractTests
{
    private static IEnumerable<string> Constants(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    [Test]
    public void Contract_vocabularies_match_the_engine_values()
    {
        Constants(typeof(AdvancedGuiderStates)).Should().BeEquivalentTo(Enum.GetNames<GuiderState>());
        Constants(typeof(AdvancedGuiderAlertSeverities)).Should().BeEquivalentTo(Enum.GetNames<GuideErrorSeverity>());
        Constants(typeof(AdvancedDecDriftDirections)).Should().BeEquivalentTo(Enum.GetNames<DecGuideDirection>());
        Constants(typeof(AdvancedIncidentKinds)).Should().BeEquivalentTo(Enum.GetNames<IncidentKind>());
        Constants(typeof(AdvancedCoachPhases)).Should().BeEquivalentTo(Constants(typeof(CoachPhases)));
        Constants(typeof(AdvancedCoachSteps)).Should().BeEquivalentTo(Constants(typeof(CoachStepNames)));
        Constants(typeof(AdvancedCoachSeverities)).Should().BeEquivalentTo(Constants(typeof(CoachSeverities)));
        Constants(typeof(AdvancedCoachGrades)).Should().BeEquivalentTo(Constants(typeof(CoachGrades)));
        AdvancedCoachSteps.Selectable.Should().Equal(CoachStepNames.All);
        Constants(typeof(AdvancedStarSelectionErrors)).Should()
            .BeEquivalentTo(Enum.GetNames<StarSelectionError>().Where(n => n != nameof(StarSelectionError.None)).Append(AdvancedStarSelectionErrors.TimedOut));
    }

    [Test]
    public void Native_guider_implements_the_optional_interfaces()
    {
        typeof(InternalGuider).Should().Implement<IGuidingCoach>().And.Implement<IGuideIncidentRecorder>();
    }
}

[TestFixture]
public class MountAdapterTests
{
    [Test]
    public async Task Mount_pulse_maps_directions_and_waits_for_completion()
    {
        var info = new TelescopeInfo { Connected = true };
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(info);
        var calls = new List<(GuideDirections, int)>();
        mediator.Setup(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>())).Callback<GuideDirections, int>((d, ms) => calls.Add((d, ms)));
        var output = new MountPulseOutput(mediator.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await output.PulseAsync(GuideDirection.West, 300, CancellationToken.None);
        await output.PulseAsync(GuideDirection.North, 100, CancellationToken.None);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(390);
        calls.Should().Equal((GuideDirections.guideWest, 300), (GuideDirections.guideNorth, 100));
    }

    [Test]
    public async Task Mount_pulse_refuses_when_parked_or_disconnected()
    {
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, AtPark = true });
        var output = new MountPulseOutput(mediator.Object);
        await FluentActions.Awaiting(() => output.PulseAsync(GuideDirection.East, 100, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = false });
        await FluentActions.Awaiting(() => output.PulseAsync(GuideDirection.East, 100, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        mediator.Verify(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public void Mount_state_maps_telescope_info()
    {
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo
        {
            Connected = true,
            Declination = 41.2,
            RightAscension = 5.5,
            SiderealTime = 7.25,
            SideOfPier = NINA.Core.Enum.PierSide.pierWest,
            Slewing = true,
            TrackingEnabled = true,
            GuideRateRightAscensionArcsecPerSec = 7.5205,
            GuideRateDeclinationArcsecPerSec = double.NaN,
        });
        var s = new NinaMountState(mediator.Object).GetSnapshot();
        s.IsConnected.Should().BeTrue();
        s.DeclinationDeg.Should().Be(41.2);
        s.PierSide.Should().Be(GuideEngine.Core.PierSide.West);
        s.SiderealTimeHours.Should().Be(7.25);
        s.IsSlewing.Should().BeTrue();
        s.GuideRateRa.Should().BeApproximately(0.5, 1e-3);
        s.GuideRateDec.Should().BeNull();
    }

    [Test]
    public void Mount_state_falls_back_to_the_computers_sidereal_time()
    {
        var now = new DateTimeOffset(2026, 9, 26, 21, 30, 0, TimeSpan.Zero);
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, RightAscension = 5.5, SiderealTime = -1, SiteLongitude = 11.5 });
        var s = new NinaMountState(mediator.Object, clock: clock.Object).GetSnapshot();
        s.SiderealTimeHours.Should().Be(NinaMountState.LocalMeanSiderealTime(now.UtcDateTime, 11.5));
    }

    // reference: ERFA eraGmst06 (IAU 2006 GMST) with UT1 = UTC, TT = UT1 + 69.2 s; + 11.5° E = 0.766667 h
    [TestCase(2026, 1, 1, 0, 0, 0.0, 6.710723)]
    [TestCase(2026, 9, 26, 21, 30, 0.0, 21.879821)]
    [TestCase(2026, 1, 1, 0, 0, 11.5, 7.477390)]
    [TestCase(2026, 9, 26, 21, 30, -180.0, 9.879821)]
    public void Local_mean_sidereal_time_matches_the_IAU_2006_GMST(int y, int mo, int d, int h, int mi, double longitude, double expectedHours)
    {
        double lst = NinaMountState.LocalMeanSiderealTime(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), longitude);
        (lst - expectedHours).Should().BeApproximately(0, 0.1 / 3600, "within 0.1 s");
    }
}
