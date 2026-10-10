// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>
/// End-to-end tests of <see cref="Guider"/> against the closed-loop simulator in virtual time.
/// Guiding quality is judged against the simulator's ground truth, not the guider's own measurements.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ClosedLoopTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    /// <summary>Replaces the guide algorithms of every harness (the Predictive run of this suite).</summary>
    internal static Func<GuiderSettings, GuiderSettings>? AlgorithmOverride { get; set; }

    [Test]
    public async Task Calibrates_and_guides_a_good_mount()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        (await guide).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle();
        var cal = h.Events.OfType<CalibrationCompleteEvent>().Single().Calibration;

        // expected RA rate: 0.5 × 15.041″/s × cos(20°) / scale, in px/ms
        double scale = h.Guider.PixelScale;
        double expectedX = 0.5 * 15.041 * Math.Cos(20 * Math.PI / 180) / scale / 1000.0;
        double expectedY = 0.5 * 15.041 / scale / 1000.0;
        cal.XRate.Should().BeApproximately(expectedX, expectedX * 0.1);
        cal.YRate.Should().BeApproximately(expectedY, expectedY * 0.1);
        GuideEngine.Calibration.MountTransform.OrthogonalityErrorDegrees(cal.XAngle, cal.YAngle).Should().BeLessThan(5);

        var (guided, unguided) = h.TrueRms(fromSec: 300);
        TestContext.Out.WriteLine($"GoodMount true RMS guided {guided:F2}″ unguided {unguided:F2}″; guider RMS {h.Guider.Statistics!.Session.RmsTotalArcsec:F2}″");
        guided.Should().BeLessThan(0.8);
        guided.Should().BeLessThan(unguided * 0.5);
        h.Guider.State.Should().Be(GuiderState.Stopped, "the loop ended with the virtual-time cancellation");
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    [Category("Slow")]
    public async Task Tames_large_periodic_error()
    {
        var h = new Harness(SimulatorScenario.PoorPeriodicError);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(25));

        (await guide).Success.Should().BeTrue();
        var (guided, unguided) = h.TrueRms(fromSec: 300);
        TestContext.Out.WriteLine($"PoorPE true RMS guided {guided:F2}″ unguided {unguided:F2}″");
        guided.Should().BeLessThan(unguided * 0.25);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Dithers_and_settles()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var results = new List<Task<SettleResult>>();
        int dithers = 0;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && dithers < 3)
            {
                dithers++;
                results.Add(h.Guider.DitherAsync(5, false, new SettleParams(1.5, 10, 120)));
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        results.Should().HaveCount(3);
        foreach (var r in results)
        {
            (await r).Success.Should().BeTrue();
        }

        h.Events.OfType<GuidingDitheredEvent>().Should().HaveCount(3);
        h.Events.OfType<SettleDoneEvent>().Should().HaveCount(4).And.OnlyContain(s => s.Status == 0);
        h.Events.OfType<GuideStepEvent>().Should().Contain(s => s.IsRecenterMove);
    }

    [Test]
    public async Task Survives_passing_clouds()
    {
        var h = new Harness(SimulatorScenario.Clouds);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        (await guide).Success.Should().BeTrue();
        h.Events.OfType<StarLostEvent>().Should().NotBeEmpty();
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.StarReacquired);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Events.OfType<GuideStepEvent>().Last().Time.Should().BeGreaterThan(400);
    }

    [Test]
    public async Task Fails_after_reacquire_timeout()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(300, 500, 0.0, 2.0)] },
        };
        var h = new Harness(scenario, s => s with { Safety = s.Safety with { StopOnLostStarTimeout = true } });
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.StarReacquireTimeout);
        var lost = h.Events.OfType<StarLostEvent>().First().Timestamp;
        var failed = h.Events.OfType<AlertEvent>().Single(a => a.Code == GuideErrorCode.StarReacquireTimeout).Timestamp;
        (failed - lost).TotalSeconds.Should().BeInRange(60, 66);
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > lost).Should().BeEmpty("no corrections while the star is lost");
    }

    [Test]
    public async Task Stops_on_runaway_when_corrections_have_the_wrong_sign()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 3 } });
        DateTimeOffset? faultAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && faultAt is null)
            {
                faultAt = e.Timestamp;
                h.Sim.Mount.InvertDecPulses = true;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.RunawayDetected);
        var stop = h.Events.OfType<AlertEvent>().Single(a => a.Code == GuideErrorCode.RunawayDetected).Timestamp;
        TestContext.Out.WriteLine($"runaway detected {(stop - faultAt!.Value).TotalSeconds:F0} s after the fault; true error then {h.Sim.TruePointingError(h.Seconds(stop)).Dec:F1}″");
        (stop - faultAt!.Value).TotalMinutes.Should().BeLessThan(5);
    }

    [Test]
    public async Task Stops_when_the_mount_ignores_pulses()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { RaDriftArcsecPerMin = 20 } });
        DateTimeOffset? faultAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && faultAt is null)
            {
                faultAt = e.Timestamp;
                h.Sim.Mount.NotResponding = true;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().BeOneOf(GuideErrorCode.MountNotResponding, GuideErrorCode.RunawayDetected);
        var stop = h.Events.OfType<AlertEvent>().First(a => a.Severity == GuideErrorSeverity.Critical).Timestamp;
        (stop - faultAt!.Value).TotalSeconds.Should().BeLessThan(90);
    }

    [Test]
    public async Task Retries_transient_camera_failures_and_fails_on_persistent_ones()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        int steps = 0;
        h.OnEvent = e =>
        {
            if (e is GuideStepEvent && ++steps == 50)
            {
                h.Sim.Camera.InjectFaults(CaptureFault.Failure, 2);
            }

            if (e is GuideStepEvent && steps == 150)
            {
                h.Sim.Camera.InjectFaults(CaptureFault.Failure, 100);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        h.Events.OfType<AlertEvent>().Count(a => a.Code == GuideErrorCode.CameraCaptureFailed).Should().BeGreaterThanOrEqualTo(2);
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.CameraReconnecting);
        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.CameraFailed);
        steps.Should().BeGreaterThanOrEqualTo(150, "guiding continued after the transient failures");
    }

    [Test]
    public async Task Pauses_during_a_short_slew_and_resumes()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool slewed = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !slewed)
            {
                slewed = true;
                h.Sim.Mount.StartSlew(new SkyOffset(0, 0), TimeSpan.FromSeconds(10));
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        h.Events.OfType<PausedEvent>().Should().ContainSingle(p => p.Reason == "Slewing");
        h.Events.OfType<ResumedEvent>().Should().ContainSingle();
        var resumed = h.Events.OfType<ResumedEvent>().Single().Timestamp;
        h.Events.OfType<GuideStepEvent>().Should().Contain(s => s.Timestamp > resumed && s.RaDuration + s.DecDuration > 0);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Stays_paused_after_a_large_slew_until_resumed()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool slewed = false;
        DateTimeOffset? resumeRequestedAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !slewed)
            {
                slewed = true;
                h.Sim.Mount.StartSlew(new SkyOffset(600, 300), TimeSpan.FromSeconds(10));
            }

            if (slewed && resumeRequestedAt is null && e is LoopingExposuresEvent or GuideStepEvent or StarLostEvent &&
                h.Seconds(e.Timestamp) > h.Seconds(h.Events.OfType<PausedEvent>().FirstOrDefault()?.Timestamp ?? DateTimeOffset.MaxValue) + 60)
            {
                resumeRequestedAt = e.Timestamp;
                h.Guider.Resume();
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        var paused = h.Events.OfType<PausedEvent>().Single().Timestamp;
        resumeRequestedAt.Should().NotBeNull();
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > paused && s.Timestamp < resumeRequestedAt!.Value && s.RaDuration + s.DecDuration > 0)
            .Should().BeEmpty("no corrections while paused after a large move");
        h.Events.OfType<StarSelectedEvent>().Should().Contain(s => s.Timestamp > resumeRequestedAt!.Value, "a new star is selected after resuming");
        h.Events.OfType<StartGuidingEvent>().Should().HaveCount(2);
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle("the calibration is reused");
    }

    [Test]
    public async Task Post_flip_dec_self_check_fixes_wrong_dec_flip_setting()
    {
        // the mount really requires a Dec flip after the meridian flip, but the setting says it doesn't
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecGuideReversedOnWestPier = false, DecDriftArcsecPerMin = 3 },
        };
        var h = new Harness(scenario);
        int phase = 0;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (phase == 0 && e is SettleDoneEvent)
            {
                phase = 1;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        second.Should().NotBeNull();
        foreach (var a in h.Events.OfType<AlertEvent>())
        {
            TestContext.Out.WriteLine($"{h.Seconds(a.Timestamp):F0}s {a.Code} {a.Detail}");
        }

        foreach (var st in h.Events.OfType<AppStateEvent>())
        {
            TestContext.Out.WriteLine($"{h.Seconds(st.Timestamp):F0}s state {st.Previous} -> {st.State}");
        }

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationDataFlippedEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Events.OfType<CalibrationUpdatedEvent>().Last().DecFlipRequired.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Guider.Settings.DecFlipRequired.Should().BeTrue();
    }

    [Test]
    public async Task Correct_dec_flip_setting_is_left_alone()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 3 } });
        int phase = 0;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (phase == 0 && e is SettleDoneEvent)
            {
                phase = 1;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationDataFlippedEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Emits_phd2_event_sequence_for_guide_start()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var names = h.Events.Select(e => e.Phd2Name).Where(n => n is "StarSelected" or "StartCalibration" or "CalibrationComplete" or "StartGuiding"
            or "SettleBegin" or "SettleDone").Distinct().ToList();
        names.Should().Equal("StarSelected", "StartCalibration", "CalibrationComplete", "StartGuiding", "SettleBegin", "SettleDone");
        h.Events.OfType<CalibratingEvent>().Should().NotBeEmpty();
        var step = h.Events.OfType<GuideStepEvent>().Last();
        step.Snr.Should().BeGreaterThan(10);
        step.StarsUsed.Should().BeGreaterThan(1, "multi-star guiding is active on a rich field");
        step.Stars.Should().Contain(s => s.IsPrimary);
    }

    [Test]
    public async Task Writes_a_phd2_compatible_guide_log()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var sw = new StringWriter();
        var log = new GuideEngine.Logging.GuidingLog(sw, h.Clock);
        log.EnableLogging();
        using var bridge = new GuideEngine.Logging.GuideLogBridge(h.Guider, log, h.Sim.Mount, () => new GuideEngine.Logging.GuideLogContext
        {
            CameraName = "Simulator", MountName = "Simulated mount", SensorWidth = 1936, SensorHeight = 1216, PixelSizeUm = 5.86,
        });
        bool dithered = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !dithered)
            {
                dithered = true;
                _ = h.Guider.DitherAsync(4, false, Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));
        log.Close();

        string text = sw.ToString();
        text.Should().Contain("Calibration Begins at").And.Contain("Calibration complete").And.Contain("Guiding Begins at");
        text.Should().Contain("Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance");
        text.Should().Contain("INFO: DITHER").And.Contain("INFO: SETTLING STATE CHANGE, Settling complete");
        text.Should().Contain("Guiding Ends at");
        int rows = text.Split('\n').Count(l => l.Length > 0 && char.IsDigit(l[0]) && l.Contains(",\"Mount\","));
        rows.Should().BeGreaterThan(100);
        if (h.Guider.RaAlgorithm is PredictiveAlgorithm)
        {
            // takeovers go to the guide log; the periodic summaries and the restarts at every dither to the debug log only
            text.Should().Contain("INFO: Predictive Ra: model ").And.Contain(" -> ");
            text.Should().NotContain("position restarted").And.NotContain("Predictive Ra: model wander 0.1/drift 0.001, gain");
        }
    }

    [Test]
    public async Task Keeps_searching_after_the_lost_star_timeout_and_recovers()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(300, 420, 0.0, 2.0)] },
        };
        var h = new Harness(scenario);
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(12));

        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.StarReacquireTimeout, "the timeout is reported once");
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.StarReacquired);
        var reacquired = h.Events.OfType<AlertEvent>().Last(a => a.Code == GuideErrorCode.StarReacquired).Timestamp;
        h.Events.OfType<GuideStepEvent>().Should().Contain(s => s.Timestamp > reacquired && s.RaDuration + s.DecDuration > 0, "guiding resumed by itself");
        h.Guider.State.Should().Be(GuiderState.Stopped, "the run ended with the virtual-time cancellation, not with a failure");
        var (guided, _) = h.TrueRms(fromSec: 500);
        guided.Should().BeLessThan(1.0);
    }

    [Test]
    public async Task Forced_recalibration_while_guiding_restarts_and_completes()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && second is null)
            {
                second = h.Guider.StartGuidingAsync(Settle, forceCalibration: true);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        second.Should().NotBeNull();
        second!.IsCompleted.Should().BeTrue("a forced restart must not hang");
        (await second).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().HaveCount(2);
    }

    [Test]
    public async Task Cancelled_start_never_calibrates_later_when_looping()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        using var cts = new CancellationTokenSource();
        bool looped = false;
        h.OnEvent = e =>
        {
            if (e is StarSelectedEvent && !cts.IsCancellationRequested)
            {
                // host cancels the start and stops capture (as the plugin does), later the user just loops
                cts.Cancel();
                h.Guider.StopGuiding();
            }
        };

        var start = h.Guider.StartGuidingAsync(Settle, ct: cts.Token);
        await h.RunFor(TimeSpan.FromMinutes(2));
        await FluentActions.Awaiting(() => start).Should().ThrowAsync<OperationCanceledException>();

        // new loop without any guide request
        h.OnEvent = _ => looped = true;
        await h.RunFor(TimeSpan.FromMinutes(2));
        looped.Should().BeTrue();
        h.Events.OfType<StartCalibrationEvent>().Should().BeEmpty("nobody asked for guiding after the cancellation");
    }

    [Test]
    public async Task Lost_star_while_user_paused_starts_no_timeout()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(300, 500, 0.0, 2.0)] },
        };
        var h = new Harness(scenario);
        bool paused = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !paused)
            {
                paused = true;
                h.Guider.Pause();
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(9));

        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.StarReacquireTimeout);
        h.Guider.State.Should().Be(GuiderState.Stopped);
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > h.Events.OfType<PausedEvent>().First().Timestamp)
            .Should().OnlyContain(s => s.RaDuration + s.DecDuration == 0, "no corrections while paused");
    }

    [Test]
    public async Task User_pause_survives_a_short_slew()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        int phase = 0;
        h.OnEvent = e =>
        {
            if (phase == 0 && e is SettleDoneEvent)
            {
                phase = 1;
                h.Guider.Pause();
            }
            else if (phase == 1 && e is PausedEvent)
            {
                phase = 2;
                h.Sim.Mount.StartSlew(new SkyOffset(0, 0), TimeSpan.FromSeconds(8));
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        h.Events.OfType<ResumedEvent>().Should().BeEmpty("the user's pause must not be lifted by the mount watcher");
        var paused = h.Events.OfType<PausedEvent>().First().Timestamp;
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > paused).Should().OnlyContain(s => s.RaDuration + s.DecDuration == 0);
    }

    [Test]
    public async Task Logs_a_recenter_pulse_as_sent()
    {
        // a recenter pulse is clamped to the max pulse after the corrector computed it: the step reports what went out
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { MaxRaDurationMs = 300, MaxDecDurationMs = 300 });
        bool dithered = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !dithered)
            {
                dithered = true;
                _ = h.Guider.DitherAsync(12, false, Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var recenter = h.Events.OfType<GuideStepEvent>().Where(s => s.IsRecenterMove).ToList();
        recenter.Should().NotBeEmpty();
        recenter.Should().OnlyContain(s => s.RaDuration <= 300 && s.DecDuration <= 300);
        recenter.Should().Contain(s => s.RaDuration == 300 || s.DecDuration == 300, "the 12 px dither needs more than one max pulse");
    }

    /// <summary>
    /// The pulse model (docs/notes/DEC-PULSE-MODEL.md) with both axes on Predictive and a dither every 2 minutes, on a mount
    /// with a little Dec backlash and drift. With a calibration that is off like the EQMod rig's (RA pulses act 1.14×, Dec
    /// 0.78× the calibrated amount) it learns both effects; with a right calibration it stays about neutral. Either way it
    /// guides no worse (2026-09-29: 0.212″ → 0.205″ with the calibration off, 0.225″ → 0.205″ with it right; Predictive's
    /// filters absorb much of a clean calibration error by themselves).
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    [Category("Slow")]
    public async Task Pulse_model_learns_what_the_pulses_move(bool calibrationOff)
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Name = "RigLike",
            Mount = SimulatorScenario.GoodMount.Mount with { DecBacklashArcsec = 2.0, DecDriftArcsecPerMin = 1.0 },
        };
        double raFactor = calibrationOff ? 1 / 1.14 : 1.0;
        double decFactor = calibrationOff ? 1 / 0.78 : 1.0;
        var off = await DitheredRun(scenario, pulseModel: false, raFactor, decFactor);
        var on = await DitheredRun(scenario, pulseModel: true, raFactor, decFactor);
        TestContext.Out.WriteLine(FormattableString.Invariant(
            $"calibration {(calibrationOff ? "off" : "right")}: true RMS without RA {off.Ra:F3}″ Dec {off.Dec:F3}″, with RA {on.Ra:F3}″ Dec {on.Dec:F3}″; learned RA {on.Learned.Ra?.Effect:F3} ± {on.Learned.Ra?.EffectSigma:F3}, Dec {on.Learned.Dec?.Effect:F3} ± {on.Learned.Dec?.EffectSigma:F3}, in use RA {on.Learned.RaEffect:F3}, Dec {on.Learned.DecEffect:F3}; {on.Updates} updates"));

        var learned = on.Learned;
        learned.Ra!.Effect.Should().BeApproximately(calibrationOff ? 1.14 : 1.0, Math.Max(0.07, 2.5 * learned.Ra.EffectSigma));
        learned.Dec!.Effect.Should().BeApproximately(calibrationOff ? 0.78 : 1.0, Math.Max(0.07, 2.5 * learned.Dec.EffectSigma));
        on.Updates.Should().BeGreaterThan(0, "the values in use were logged");
        double combinedOff = Math.Sqrt(off.Ra * off.Ra + off.Dec * off.Dec);
        double combinedOn = Math.Sqrt(on.Ra * on.Ra + on.Dec * on.Dec);
        combinedOn.Should().BeLessThan(combinedOff * 1.03);
    }

    /// <summary>
    /// Calibrates, then guides for an hour with the calibrated rates multiplied by the factors (a calibration that is off),
    /// dithering 5 px about every 2 minutes. True RMS per axis (″) over the last 30 minutes, each stretch between dithers
    /// about its own mean and without settling; what the pulse model learned (also with it off); and how many updates of
    /// its values in use went out.
    /// </summary>
    private static async Task<(double Ra, double Dec, PulseModelValues Learned, int Updates)> DitheredRun(
        SimulatorScenario scenario, bool pulseModel, double raFactor, double decFactor)
    {
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive);
        var h = new Harness(scenario, s => s with { RaAlgorithm = predictive, DecAlgorithm = predictive, PulseModel = pulseModel });
        var calibrate = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(3));
        (await calibrate).Success.Should().BeTrue();
        var cal = h.Guider.Calibration!;
        h.Guider.LoadCalibration(cal with { XRate = cal.XRate * raFactor, YRate = cal.YRate * decFactor });

        int framesSinceSettle = -1;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent)
            {
                framesSinceSettle = 0;
            }
            else if (e is GuideStepEvent && framesSinceSettle >= 0 && ++framesSinceSettle == 45)
            {
                framesSinceSettle = -1;
                _ = h.Guider.DitherAsync(5, false, Settle);
            }
        };

        double start = h.Clock.Elapsed.TotalSeconds;
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(60));
        (await guide).Success.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();

        // each stretch between dithers about its own mean: the dithers move the pointing on purpose (in event order: the step
        // that asks for a dither has the dither's timestamp)
        double from = start + 30 * 60;
        int stretch = 0;
        var steps = new List<(int Stretch, GuideStepEvent Step)>();
        foreach (var e in h.Events)
        {
            if (e is GuidingDitheredEvent)
            {
                stretch++;
            }
            else if (e is GuideStepEvent { IsSettling: false, IsRecenterMove: false } s && h.Seconds(s.Timestamp) >= from)
            {
                steps.Add((stretch, s));
            }
        }

        var stretches = steps.GroupBy(x => x.Stretch, x => x.Step);
        double sumRa = 0, sumDec = 0;
        int n = 0;
        foreach (var group in stretches)
        {
            var errors = group.Select(s => h.Sim.Mount.GetPointingErrorBreakdown(h.Seconds(s.Timestamp)).Total).ToList();
            double meanRa = errors.Average(x => x.Ra), meanDec = errors.Average(x => x.Dec);
            sumRa += errors.Sum(x => (x.Ra - meanRa) * (x.Ra - meanRa));
            sumDec += errors.Sum(x => (x.Dec - meanDec) * (x.Dec - meanDec));
            n += errors.Count;
        }

        n.Should().BeGreaterThan(300);
        return (Math.Sqrt(sumRa / n), Math.Sqrt(sumDec / n), h.Guider.PulseModelLearned, h.Events.OfType<PulseModelUpdatedEvent>().Count());
    }

    /// <summary>
    /// A 25-minute guided run (calibration included) with the given algorithms (and further <paramref name="configure"/>d
    /// settings, over <paramref name="duration"/>): true RMS (″) combined and per axis after the first 5 minutes, and what
    /// the Predictive filters learned.
    /// </summary>
    internal static async Task<(double Rms, double RaRms, double DecRms, string Diagnostics)> GuidedRun(
        SimulatorScenario scenario, AlgorithmSettings ra, AlgorithmSettings dec, Action<GuiderEvent>? onEvent = null,
        Func<GuiderSettings, GuiderSettings>? configure = null, TimeSpan? duration = null)
    {
        var h = new Harness(scenario, s =>
        {
            var algorithms = s with { RaAlgorithm = ra, DecAlgorithm = dec };
            return configure?.Invoke(algorithms) ?? algorithms;
        });
        h.OnEvent = onEvent;
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(duration ?? TimeSpan.FromMinutes(25));
        (await guide).Success.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();

        static string Describe(string axis, IGuideAlgorithm a) => a is PredictiveAlgorithm f
            ? $"{axis}: seeing {f.SeeingPx:F3} px, wander {f.WanderPx:F3} px, gain {f.LastGain:F2}, drift {f.DriftPxPerSec * 60:F3} px/min, min move {f.MinMove:F3}, {f.State.Phase} after {f.State.FramesLearned} frames, {f.State.Takeovers} takeovers"
            : $"{axis}: {a.Name}";
        var (raRms, decRms) = h.TrueRmsAxes(fromSec: 300);
        return (h.TrueRms(fromSec: 300).Guided, raRms, decRms,
            $"scale {h.Guider.PixelScale:F2}″/px; {Describe("RA", h.Guider.RaAlgorithm)}; {Describe("Dec", h.Guider.DecAlgorithm)}");
    }

    /// <summary>Wires a simulator, a guider and an event recorder together.</summary>
    [Test]
    public async Task Measures_the_guide_cycle()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));
        (await guide).Success.Should().BeTrue();

        var timing = h.Guider.Timing!;
        TestContext.Out.WriteLine($"cycle {timing.Median.CycleMs:F1} ms ({timing.FramesPerSecond:F3} fps): camera {timing.Median.CameraMs:F1}, " +
            $"processing {timing.Median.ProcessingMs:F1}, frame to pulse {timing.Median.FrameToPulseMs:F1}, pulses {timing.Median.PulseMs:F1}, other {timing.Median.OtherMs:F1}");
        timing.Cycles.Should().Be(CycleTimer.Window);
        timing.Median.ExposureMs.Should().Be(2000);
        timing.Median.CycleMs.Should().BeGreaterThanOrEqualTo(2000);
        timing.FramesPerSecond.Should().BeApproximately(1000 / timing.Median.CycleMs, 1e-9);
        // the Predictive run of this suite may leave a good mount without a pulse for 20 frames
        if (timing.Median.FrameToPulseMs is null)
        {
            timing.Median.PulseMs.Should().Be(0);
        }
        else
        {
            timing.Median.PulseMs.Should().BeGreaterThan(0);
        }
        var last = timing.Last;
        (last.ExposureMs + last.CameraMs + last.ProcessingMs + (last.FrameToPulseMs is null ? 0 : last.FrameToPulseMs.Value - last.ProcessingMs) + last.PulseMs + last.OtherMs)
            .Should().BeApproximately(last.CycleMs, 1e-6, "the parts of one cycle add up to it");
    }

    private sealed class Harness
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];

        public Harness(SimulatorScenario scenario, Func<GuiderSettings, GuiderSettings>? configure = null)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(scenario, Clock);
            var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000 };
            settings = configure?.Invoke(settings) ?? settings;
            settings = AlgorithmOverride?.Invoke(settings) ?? settings;
            Guider = new Guider(Sim.Camera, Sim.Mount, Sim.Mount, Clock, settings, ditherSeed: 5)
            {
                AutoStartLoop = false,
            };
            Guider.EventRaised += (_, e) =>
            {
                // a frame event carries the whole image: keeping every one would hold gigabytes in a long run
                if (e is not FrameReadyEvent)
                {
                    lock (gate)
                    {
                        events.Add(e);
                    }
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        public Simulator Sim { get; }

        public Guider Guider { get; }

        public Action<GuiderEvent>? OnEvent { get; set; }

        public IReadOnlyList<GuiderEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return events.ToList();
                }
            }
        }

        public IEnumerable<AlertEvent> Alerts(GuideErrorSeverity severity) => Events.OfType<AlertEvent>().Where(a => a.Severity == severity);

        public double Seconds(DateTimeOffset t) => (t - Clock.Epoch).TotalSeconds;

        public async Task RunFor(TimeSpan duration)
        {
            Guider.StartLooping(Clock.CancelAt(Clock.Elapsed + duration));
            await Guider.WaitForLoopAsync().Within(duration);
        }

        /// <summary>
        /// True RMS (″, population σ per axis, combined) of the pointing error over guide frames after
        /// <paramref name="fromSec"/> of guiding, excluding settling; and the same for the unguided error.
        /// </summary>
        public (double Guided, double Unguided) TrueRms(double fromSec)
        {
            var steps = Events.OfType<GuideStepEvent>().Where(s => !s.IsSettling && !s.IsRecenterMove && s.Time >= fromSec).ToList();
            steps.Should().NotBeEmpty();
            var g = steps.Select(s => Sim.Mount.GetPointingErrorBreakdown(Seconds(s.Timestamp))).ToList();
            double Rms(IEnumerable<double> v)
            {
                var a = v.ToArray();
                double m = a.Average();
                return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Length);
            }

            double guided = Math.Sqrt(Math.Pow(Rms(g.Select(b => b.Total.Ra)), 2) + Math.Pow(Rms(g.Select(b => b.Total.Dec)), 2));
            double unguided = Math.Sqrt(Math.Pow(Rms(g.Select(b => b.Total.Ra - b.Guiding.Ra)), 2) + Math.Pow(Rms(g.Select(b => b.Total.Dec - b.Guiding.Dec)), 2));
            return (guided, unguided);
        }

        /// <summary>The guided part of <see cref="TrueRms"/> per axis (″).</summary>
        public (double Ra, double Dec) TrueRmsAxes(double fromSec)
        {
            var steps = Events.OfType<GuideStepEvent>().Where(s => !s.IsSettling && !s.IsRecenterMove && s.Time >= fromSec).ToList();
            var g = steps.Select(s => Sim.Mount.GetPointingErrorBreakdown(Seconds(s.Timestamp))).ToList();
            static double Rms(double[] a)
            {
                double m = a.Average();
                return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Length);
            }

            return (Rms([.. g.Select(b => b.Total.Ra)]), Rms([.. g.Select(b => b.Total.Dec)]));
        }
    }
}
