// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.MultiStar;
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
    public async Task The_polar_alignment_estimate_uses_the_declination_guided_at()
    {
        // calibrated at the equator, guided at 60°: Barrett's estimate divides the Dec drift by cos δ of the latter
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DeclinationDeg = 0 } });
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(3));
        (await guide).Success.Should().BeTrue();
        h.Guider.StopGuiding();

        h.Sim.Mount.SetDeclination(60);
        guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(1));

        (await guide).Success.Should().BeTrue();
        h.Guider.Statistics!.DeclinationDeg.Should().BeApproximately(60, 0.01);
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

    /// <summary>
    /// The play model's measurement after the flip finds North pulses moving the star South (a wrong Dec-flip setting):
    /// it inverts Dec, saves the setting and measures again before guiding (pins log 2026-10-19: guiding started anyway and
    /// Dec ran away by 270 px).
    /// </summary>
    [Test]
    public async Task Play_probe_after_the_flip_inverts_a_wrong_dec_flip_setting()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecGuideReversedOnWestPier = false, DecDriftArcsecPerMin = 3, DecBacklashArcsec = 3 },
        };
        var h = new Harness(scenario, PlayModel);
        Task<SettleResult>? second = null;
        DateTimeOffset? flippedAt = null;
        h.OnEvent = e =>
        {
            if (flippedAt is null && e is SettleDoneEvent)
            {
                flippedAt = e.Timestamp;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        foreach (var p in h.Events.OfType<DecPlayProbeEvent>())
        {
            TestContext.Out.WriteLine($"{h.Seconds(p.Timestamp):F0}s {p.Message}");
        }

        (await second!).Success.Should().BeTrue();
        var probes = h.Events.OfType<DecPlayProbeEvent>().ToList();
        probes.Should().HaveCount(2);
        probes[0].Message.Should().Contain("North pulses moved the star South").And.Contain("Dec inverted");
        probes[1].Measured.Should().BeTrue("the second measurement finds the play with the inverted Dec");
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle("the flipped calibration is kept");
        h.Guider.Settings.DecFlipRequired.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Sim.TruePointingError(h.Clock.Elapsed.TotalSeconds).Dec.Should().BeInRange(-5, 5, "Dec is guided after the flip");
    }

    /// <summary>
    /// Without the post-flip self-check the play model's measurement does not invert Dec on its own: it calibrates again
    /// instead of guiding with a calibration that pushes Dec away.
    /// </summary>
    [Test]
    public async Task Play_probe_calibrates_again_when_dec_moves_the_wrong_way_and_may_not_be_inverted()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecGuideReversedOnWestPier = false, DecDriftArcsecPerMin = 3, DecBacklashArcsec = 3 },
        };
        var h = new Harness(scenario, s => PlayModel(s) with { VerifyDecAfterFlip = false });
        Task<SettleResult>? second = null;
        DateTimeOffset? flippedAt = null;
        h.OnEvent = e =>
        {
            if (flippedAt is null && e is SettleDoneEvent)
            {
                flippedAt = e.Timestamp;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<DecPlayProbeEvent>().Should().ContainSingle(p => p.Message.Contains("calibrating again"));
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.CalibrationInvalidated);
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Events.OfType<CalibrationCompleteEvent>().Should().HaveCount(2);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    /// <summary>
    /// A runaway on the pulse duty alone shortly after a flip, with the right Dec-flip setting and a small error, stops
    /// guiding but does not invert Dec (pins log 2026-10-18: a play taken up on every reversal swung Dec between 2.5 s North
    /// and South pulses at ±0.3 px, the duty tripped, Dec was inverted and the wrong setting saved).
    /// </summary>
    [Test]
    public async Task Pulse_duty_runaway_after_a_flip_does_not_invert_dec()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 30 } });
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                h.Guider.UpdateSettings(h.Guider.Settings with { Safety = h.Guider.Settings.Safety with { MaxPulseDuty = 0.03 } });
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.RunawayDetected);
        h.Events.OfType<AlertEvent>().Single(a => a.Code == GuideErrorCode.RunawayDetected).Detail.Should().Be("Dec axis");
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Guider.Settings.DecFlipRequired.Should().BeFalse("the setting was right");
    }

    /// <summary>
    /// The play differs by pier side (OnStepX 2026-10-19: none on West, 4.6 s on East). When the measurement after the flip
    /// cannot find the play, what was learned on the other side is forgotten rather than kept.
    /// </summary>
    [Test]
    public async Task Play_learned_on_the_other_pier_side_is_forgotten_when_the_probe_fails()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 3, DecBacklashArcsec = 3 },
        };
        var h = new Harness(scenario, PlayModel);
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                // longer than the probe's North pulses up to its largest play: it gives up
                h.Sim.Mount.StopRespondingFor(TimeSpan.FromMinutes(3));
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        foreach (var p in h.Events.OfType<DecPlayProbeEvent>())
        {
            TestContext.Out.WriteLine($"{h.Seconds(p.Timestamp):F0}s {p.Message}");
        }

        second.Should().NotBeNull();
        var probe = h.Events.OfType<DecPlayProbeEvent>().Should().ContainSingle().Subject;
        probe.Measured.Should().BeFalse();
        probe.Message.Should().Contain("did not follow").And.Contain("forgotten");
    }

    /// <summary>
    /// An off-axis guider behind the rotator: the rotator turns the guide camera between two starts, and the kept calibration
    /// is turned by the rotator's move without a new calibration. With Yes the camera turns against the rotator's move (as
    /// PHD2 assumes), with Reversed the other way.
    /// </summary>
    [TestCase(RotatorGuideCamera.Yes, -1)]
    [TestCase(RotatorGuideCamera.Reversed, 1)]
    public async Task Rotator_turns_the_calibration_with_the_guide_camera(RotatorGuideCamera turns, int cameraSense)
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = turns });
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(cameraSense * 40);
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle("the turned calibration is kept");
        string turn = turns == RotatorGuideCamera.Yes ? "-40.0" : "+40.0";
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera, "one rotation, one message")
            .Which.Detail.Should().Be($"Rotator moved from 10.0° to 50.0°: new guide star selected, calibration turned by {turn}°");
        h.Events.OfType<StarSelectedEvent>().Should().HaveCount(2, "a new star after the rotation");
        h.Guider.Calibration!.RotatorAngleDeg.Should().Be(50);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2, "the turned calibration fits the camera");
        h.Events.OfType<CalibrationUpdatedEvent>().Last().Calibration.Should().Be(h.Guider.Calibration, "the host stores the turned calibration");
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        var error = h.Sim.TruePointingError(h.Clock.Elapsed.TotalSeconds);
        error.Ra.Should().BeInRange(-5, 5, "RA is guided with the turned calibration");
        error.Dec.Should().BeInRange(-5, 5, "Dec is guided with the turned calibration");
    }

    /// <summary>
    /// A calibration made without a rotator angle (before the guider recorded one, or without a rotator) takes the current
    /// angle. With Yes the user is warned once, since a turn of the guide camera since that calibration is not known.
    /// </summary>
    [Test]
    public async Task Calibration_without_rotator_angle_warns_once_when_the_rotator_turns_the_guide_camera()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = null;
        int starts = 0;
        Task<SettleResult>? last = null;
        h.OnEvent = e =>
        {
            if (starts < 3 && e is SettleDoneEvent)
            {
                starts++;
                h.Guider.StopGuiding();
                h.Sim.Mount.RotatorAngleDeg = 30;
                last = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        (await last!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle("the calibration is kept");
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.CalibrationRotatorUnknown)
            .Which.Detail.Should().Contain("Calibrate again if the guide camera was turned");
        h.Guider.Calibration!.RotatorAngleDeg.Should().Be(30);
    }

    /// <summary>
    /// A rotator between the off-axis guider and the imaging camera turns only the imaging camera: with the default setting
    /// the calibration keeps its angles and only follows the rotator's angle.
    /// </summary>
    [Test]
    public async Task Rotator_that_does_not_turn_the_guide_camera_leaves_the_calibration_as_it_is()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.RotatorAngleDeg = 50;
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        h.Guider.Settings.RotatorTurnsGuideCamera.Should().Be(RotatorGuideCamera.No, "the default");
        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera);
        h.Guider.Calibration!.RotatorAngleDeg.Should().Be(50);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    /// <summary>The rotator turns the guide camera during a pause that is resumed at once: resuming starts again with a new star.</summary>
    [Test]
    public async Task Rotator_move_while_paused_turns_the_calibration_on_resume()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        bool moved = false;
        h.OnEvent = e =>
        {
            if (!moved && e is SettleDoneEvent)
            {
                moved = true;
                h.Guider.Pause();
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
                h.Guider.Resume();
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        moved.Should().BeTrue();
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera)
            .Which.Detail.Should().Be("Rotator moved from 10.0° to 50.0°: new guide star selected, calibration turned by -40.0°");
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2, "resuming starts again with a new star");
        h.Events.OfType<StarSelectedEvent>().Should().HaveCount(2);
        GuideStepsAfter(h, starts[1].Timestamp).Should().BeGreaterThan(20, "guiding goes on with the new star");
        h.Guider.Calibration!.RotatorAngleDeg.Should().Be(50);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    /// <summary>
    /// The rotator turns the guide camera while guiding runs: guiding stops while it turns (the frames are smeared, the star
    /// leaves), then starts again with a new star and the turned calibration once the rotator has stopped.
    /// </summary>
    [Test]
    public async Task Rotator_turning_while_guiding_restarts_guiding_with_a_new_star_once_it_stopped()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (turnedAt is null && e is SettleDoneEvent)
            {
                turnedAt = t;
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 20)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        stoppedAt.Should().NotBeNull();
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2);
        h.Seconds(starts[1].Timestamp).Should().BeGreaterThan(stoppedAt!.Value, "guiding waits until the rotator has stopped");
        h.Events.OfType<GuideStepEvent>().Where(g => h.Seconds(g.Timestamp) > turnedAt!.Value + 3 && h.Seconds(g.Timestamp) < stoppedAt.Value)
            .Should().BeEmpty("no corrections while the rotator turns");
        h.Events.OfType<StarSelectedEvent>().Should().Contain(e => h.Seconds(e.Timestamp) > stoppedAt.Value, "a new star after the rotation");
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera).Select(a => a.Detail).Should().Equal(
            "Guiding stopped while the rotator turns; it starts again once the rotator has stopped",
            "Rotator moved from 10.0° to 50.0°: new guide star selected, calibration turned by -40.0°");
        GuideStepsAfter(h, starts[1].Timestamp).Should().BeGreaterThan(20, "guiding goes on with the new star");
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        var error = h.Sim.TruePointingError(h.Clock.Elapsed.TotalSeconds);
        error.Ra.Should().BeInRange(-5, 5);
        error.Dec.Should().BeInRange(-5, 5);
    }

    /// <summary>
    /// A rotator readout that jumps by more than the tolerance for one frame and comes back does not stop guiding: a turn
    /// of the angle stops a running guide only on <see cref="Guider.RotatorTurnFramesToStop"/> frames in a row.
    /// </summary>
    [Test]
    public async Task Rotator_readout_jumping_for_one_frame_does_not_stop_guiding()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        DateTimeOffset? settledAt = null;
        int steps = 0;
        h.OnEvent = e =>
        {
            if (settledAt is null && e is SettleDoneEvent)
            {
                settledAt = e.Timestamp;
            }
            else if (settledAt is not null && e is GuideStepEvent)
            {
                // the next frame reads the jump, the one after it the old angle again; twice
                steps++;
                h.Sim.Mount.RotatorAngleDeg = steps is 1 or 10 ? 10.1 : 10;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        steps.Should().BeGreaterThan(20);
        h.Events.OfType<StartGuidingEvent>().Should().ContainSingle("the jumps did not restart guiding");
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera);
        GuideStepsAfter(h, settledAt!.Value).Should().Be(steps);
    }

    /// <summary>
    /// A rotator whose angle keeps changing (a slow settle, a derotator) without its driver reporting moving holds the start
    /// back for two minutes. Then guiding runs while the angle creeps, without a restart at every frame, and once the angle
    /// holds it starts once more with a new star and the turned calibration. (The creep starts after the calibration: a
    /// camera that turns while it calibrates spoils the calibration.)
    /// </summary>
    [Test]
    public async Task Rotator_that_keeps_creeping_restarts_guiding_once_after_it_holds()
    {
        const double creepDegPerSec = 0.005;
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        h.Sim.Mount.RotatorTurning = true;
        double? last = null;
        double? heldAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (last is null && e is CalibrationCompleteEvent)
            {
                last = t;
            }

            if (heldAt is not null || last is null || t <= last)
            {
                return;
            }

            double turn = creepDegPerSec * (t - last.Value);
            last = t;
            h.Sim.Mount.RotatorAngleDeg += turn;
            h.Sim.Camera.RotateCamera(-turn);
            if (t > 360)
            {
                heldAt = t;
                h.Sim.Mount.RotatorTurning = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        heldAt.Should().NotBeNull();
        h.Seconds(h.Events.OfType<StartCalibrationEvent>().Single().Timestamp).Should()
            .BeInRange(Guider.RotatorWaitLimit.TotalSeconds, Guider.RotatorWaitLimit.TotalSeconds + 15);
        var selected = h.Events.OfType<StarSelectedEvent>().Select(e => h.Seconds(e.Timestamp)).ToList();
        selected.Should().ContainSingle(t => t < heldAt!.Value, "no restart while it creeps");
        selected.Should().ContainSingle(t => t > heldAt!.Value, "one restart once it holds");
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera).Select(a => a.Detail).Should()
            .HaveCount(3).And.Contain(d => d!.Contains("starts anyway"))
            .And.Contain(d => d!.Contains("new guide star selected, calibration turned by"));
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    /// <summary>A start while the rotator still turns waits for it, then selects a new star.</summary>
    [Test]
    public async Task Start_while_the_rotator_turns_waits_until_it_stopped()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? second = null;
        double? requestedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
                requestedAt = t;
                second = h.Guider.StartGuidingAsync(Settle);
            }
            else if (requestedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        (await second!).Success.Should().BeTrue();
        h.Seconds(h.Events.OfType<StartGuidingEvent>().Last().Timestamp).Should().BeGreaterThan(stoppedAt!.Value);
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera).Select(a => a.Detail)
            .Should().Equal("Waiting for the rotator to stop before guiding starts",
                "Rotator moved from 10.0° to 50.0°: new guide star selected, calibration turned by -40.0°");
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle();
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>A rotator that never reports having stopped holds guiding back only for <see cref="Guider.RotatorWaitLimit"/>.</summary>
    [Test]
    public async Task Rotator_that_keeps_reporting_moving_holds_the_start_back_for_two_minutes_only()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        h.Sim.Mount.RotatorMoving = true;

        var start = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        (await start).Success.Should().BeTrue();
        h.Seconds(h.Events.OfType<StartCalibrationEvent>().Single().Timestamp).Should()
            .BeInRange(Guider.RotatorWaitLimit.TotalSeconds, Guider.RotatorWaitLimit.TotalSeconds + 15);
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera && a.Detail!.Contains("starts anyway"));
    }

    /// <summary>
    /// A driver stuck at moving holds back only the first start: later starts (after autofocus, centering, a flip) don't
    /// wait the two minutes again while it still reports moving.
    /// </summary>
    [Test]
    public async Task Rotator_stuck_at_moving_holds_back_only_the_first_start()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        h.Sim.Mount.RotatorMoving = true;
        Task<SettleResult>? second = null;
        double? secondAt = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                secondAt = h.Seconds(e.Timestamp);
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        (await second!).Success.Should().BeTrue();
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2);
        h.Seconds(starts[1].Timestamp).Should().BeLessThan(secondAt!.Value + 15, "the first wait gave up on the driver's moving state");
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera).Should().HaveCount(2)
            .And.Contain(a => a.Detail!.Contains("starts anyway"), "one wait, which gave up");
    }

    /// <summary>With the default setting a rotator turns only the imaging camera: guiding goes on while it moves.</summary>
    [Test]
    public async Task Rotator_that_does_not_turn_the_guide_camera_does_not_interrupt_guiding()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        h.Sim.Mount.RotatorAngleDeg = 10;
        double? turnedAt = null;
        h.OnEvent = e =>
        {
            if (turnedAt is null && e is SettleDoneEvent)
            {
                turnedAt = h.Seconds(e.Timestamp);
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(5));

        turnedAt.Should().NotBeNull();
        h.Events.OfType<StartGuidingEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera);
        GuideStepsAfter(h, h.Clock.Epoch.AddSeconds(turnedAt!.Value)).Should().BeGreaterThan(20, "guiding goes on while the rotator moves");
    }

    /// <summary>
    /// A user pause with frames coming in: a rotation during it changes nothing until Resume, which then starts again with a
    /// new star.
    /// </summary>
    [Test]
    public async Task Rotator_move_during_a_user_pause_waits_for_resume()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        double? pausedAt = null;
        double? resumedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (pausedAt is null && e is SettleDoneEvent)
            {
                pausedAt = t;
                h.Guider.Pause();
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (pausedAt is { } at && resumedAt is null && t > at + 30)
            {
                resumedAt = t;
                h.Guider.Resume();
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        resumedAt.Should().NotBeNull();
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2);
        h.Seconds(starts[1].Timestamp).Should().BeGreaterThan(resumedAt!.Value, "nothing happens while paused");
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>
    /// NINA's slew and rotate: the rotator turns while guiding is paused for a short slew; when the slew ends, guiding starts
    /// again with a new star instead of resuming on the old one.
    /// </summary>
    [Test]
    public async Task Rotator_move_during_a_slew_pause_starts_again_when_the_slew_ends()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        bool slewed = false;
        h.OnEvent = e =>
        {
            if (!slewed && e is SettleDoneEvent)
            {
                slewed = true;
                h.Sim.Mount.StartSlew(new SkyOffset(30, 20), TimeSpan.FromSeconds(10));
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        h.Events.OfType<PausedEvent>().Should().NotBeEmpty();
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2);
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera)
            .Which.Detail.Should().Contain("new guide star selected, calibration turned by -40.0°");
        GuideStepsAfter(h, starts[1].Timestamp).Should().BeGreaterThan(20);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>A dither whose settle the rotator interrupts completes once guiding has settled on the new star.</summary>
    [Test]
    public async Task Dither_interrupted_by_the_rotator_settles_on_the_new_star()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? dither = null;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (dither is null && e is SettleDoneEvent)
            {
                dither = h.Guider.DitherAsync(3, false, Settle);
                turnedAt = t;
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        dither.Should().NotBeNull();
        dither!.IsCompleted.Should().BeTrue("the dither waits for the settle on the new star, not forever");
        (await dither).Success.Should().BeTrue();
        h.Seconds(h.Events.OfType<SettleDoneEvent>().Last().Timestamp).Should().BeGreaterThan(stoppedAt!.Value);
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>
    /// The rotator turns during a calibration that was asked for: guiding starts again with a new star and makes that
    /// calibration, instead of guiding with the old one turned.
    /// </summary>
    [Test]
    public async Task Rotator_move_during_a_forced_calibration_calibrates_again()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? forced = null;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (forced is null && e is SettleDoneEvent)
            {
                forced = h.Guider.StartGuidingAsync(Settle, forceCalibration: true);
            }
            else if (forced is not null && turnedAt is null && e is CalibratingEvent { Step: >= 3 })
            {
                turnedAt = t;
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(12));

        (await forced!).Success.Should().BeTrue();
        var completed = h.Events.OfType<CalibrationCompleteEvent>().ToList();
        completed.Should().HaveCount(2, "the first calibration and the one that was asked for");
        h.Seconds(completed[1].Timestamp).Should().BeGreaterThan(stoppedAt!.Value);
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera).Select(a => a.Detail).Should().Equal(
            "Guiding stopped while the rotator turns; it starts again once the rotator has stopped",
            "Rotator moved from 10.0° to 50.0°: new guide star selected");
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>Without a kept calibration a rotation selects a new star, and the message says only that.</summary>
    [Test]
    public async Task Rotation_without_a_kept_calibration_reports_the_new_star_only()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Guider.ClearCalibration();
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().HaveCount(2);
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera)
            .Which.Detail.Should().Be("Rotator moved from 10.0° to 50.0°: new guide star selected");
    }

    /// <summary>
    /// The star was lost before the rotator reported moving, and the rotator's angle was not known at the selection (it was
    /// connected later): guiding still starts again after the rotator stopped, with a new star, instead of waiting for good.
    /// </summary>
    [Test]
    public async Task Restart_for_the_rotator_selects_a_star_when_the_old_one_is_lost()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        double? lostAt = null;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (lostAt is null && e is SettleDoneEvent)
            {
                lostAt = t;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (lostAt is { } lost && turnedAt is null && e is StarLostEvent && t > lost + 2)
            {
                turnedAt = t;
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2);
        h.Seconds(starts[1].Timestamp).Should().BeGreaterThan(stoppedAt!.Value);
        h.Events.OfType<StarSelectedEvent>().Should().Contain(e => h.Seconds(e.Timestamp) > stoppedAt.Value);
    }

    /// <summary>
    /// After the wait gave up on a driver that keeps reporting moving, a real turn of the angle is still waited for: one
    /// restart, no corrections while it turns.
    /// </summary>
    [Test]
    public async Task Rotator_stuck_at_moving_still_waits_for_a_real_turn()
    {
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 10;
        h.Sim.Mount.RotatorMoving = true;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (turnedAt is null && e is SettleDoneEvent)
            {
                turnedAt = t;
                h.Sim.Mount.RotatorTurning = true;
                h.Sim.Mount.RotatorAngleDeg = 50;
                h.Sim.Camera.RotateCamera(-40);
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorTurning = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(2, "one restart for the turn");
        h.Seconds(starts[1].Timestamp).Should().BeGreaterThan(stoppedAt!.Value);
        h.Events.OfType<GuideStepEvent>().Where(g => h.Seconds(g.Timestamp) > turnedAt!.Value + 3 && h.Seconds(g.Timestamp) < stoppedAt.Value)
            .Should().BeEmpty("no corrections while it turns");
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.RotatorTurnedGuideCamera && a.Detail!.Contains("turned by -40.0°"));
        CalibrationErrorDeg(h).Should().BeInRange(-2, 2);
    }

    /// <summary>
    /// A rotation right after a meridian flip (NINA's "rotate image after flip") restarts guiding before the post-flip Dec
    /// self-check has seen a frame. The restart resets the self-check, but the flip time survives it, so the first Dec
    /// runaway still inverts a wrong Dec-flip setting instead of stopping guiding.
    /// </summary>
    [Test]
    public async Task Rotation_after_a_flip_keeps_the_flip_time_for_the_dec_runaway_check()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecGuideReversedOnWestPier = false, DecDriftArcsecPerMin = 3 },
        };
        var h = new Harness(scenario, s => s with { RotatorTurnsGuideCamera = RotatorGuideCamera.Yes });
        h.Sim.Mount.RotatorAngleDeg = 0;
        Task<SettleResult>? second = null;
        bool rotated = false;
        double? turnedAt = null;
        double? stoppedAt = null;
        h.OnEvent = e =>
        {
            double t = h.Seconds(e.Timestamp);
            if (second is null && e is SettleDoneEvent)
            {
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
            else if (second is not null && !rotated && e is StartGuidingEvent)
            {
                rotated = true;
                turnedAt = t;
                h.Sim.Mount.RotatorMoving = true;
                h.Sim.Mount.RotatorAngleDeg = 180;
                h.Sim.Camera.RotateCamera(-180);
            }
            else if (turnedAt is { } at && stoppedAt is null && t > at + 15)
            {
                stoppedAt = t;
                h.Sim.Mount.RotatorMoving = false;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        (await second!).Success.Should().BeTrue();
        var starts = h.Events.OfType<StartGuidingEvent>().ToList();
        starts.Should().HaveCount(3, "the first start, the start after the flip, the restart");
        var corrected = h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.DecFlipCorrected).Subject;
        h.Seconds(corrected.Timestamp).Should().BeGreaterThan(h.Seconds(starts[2].Timestamp), "corrected in the restarted run");
        h.Guider.Settings.DecFlipRequired.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Sim.TruePointingError(h.Clock.Elapsed.TotalSeconds).Dec.Should().BeInRange(-5, 5);
    }

    // guide frames that sent corrections after t
    private static int GuideStepsAfter(Harness h, DateTimeOffset t) =>
        h.Events.OfType<GuideStepEvent>().Count(g => g.Timestamp > t && !g.IsSettling);

    // how far the calibration's RA angle is from the simulated camera's (degrees): East pulses move the star against the
    // camera's +RA direction, which turns by 180° on pier side West (SimulatedCamera)
    private static double CalibrationErrorDeg(Harness h)
    {
        bool west = h.Sim.Mount.GetSnapshot().PierSide == PierSide.West;
        double truth = MountTransform.Radians(h.Sim.Camera.CameraAngleDeg + (west ? 0 : 180));
        return MountTransform.Degrees(MountTransform.NormAngle(h.Guider.Calibration!.XAngle - truth));
    }

    // the Dec backlash play model, measured again after a flip (it needs Predictive in Dec)
    private static GuiderSettings PlayModel(GuiderSettings s) => s with
    {
        DecAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive),
        Backlash = new BacklashSettings { Enabled = true, PulseMs = 900, Model = BacklashModel.PlayModel },
    };

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

    [Test]
    public async Task Guides_on_a_star_selected_by_hand()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        StarTruth? chosen = null;
        Task<StarSelectionResult>? select = null;
        Task<StarSelectionResult>? whileGuiding = null;
        Task<SettleResult>? guide = null;
        IReadOnlyList<StarInfo>? selectionFrameStars = null;
        h.OnEvent = e =>
        {
            if (e is LoopingExposuresEvent && select is null && h.Sim.Camera.LastFrameTruth is { } truth)
            {
                // not the star auto-selection would take: the third brightest, away from the edges; tapped 3 px off
                chosen = truth.Stars.Where(s => s.InFrame && s.X is > 200 and < 1736 && s.Y is > 200 and < 1016)
                    .OrderBy(s => s.Magnitude).Skip(2).First();
                select = h.Guider.SelectStarAsync(new GuidePoint(chosen.X + 3, chosen.Y - 2));
            }
            else if (e is StarSelectedEvent && guide is null)
            {
                guide = h.Guider.StartGuidingAsync(Settle);
            }
            else if (e is FrameReadyEvent f && guide is not null && selectionFrameStars is null)
            {
                selectionFrameStars = f.Stars;
            }
            else if (e is GuideStepEvent && whileGuiding is null)
            {
                whileGuiding = h.Guider.SelectStarAsync(new GuidePoint(chosen!.X, chosen.Y));
            }
        };

        await h.RunFor(TimeSpan.FromMinutes(8));

        var selected = await select!;
        selected.Success.Should().BeTrue();
        selected.Primary.Position.X.Should().BeApproximately(chosen!.X, 1.5);
        selected.Primary.Position.Y.Should().BeApproximately(chosen.Y, 1.5);
        selected.SecondaryStars.Should().BeGreaterThan(2);
        selectionFrameStars.Should().HaveCount(selected.SecondaryStars + 1, "the frame the star was chosen on shows the new stars");
        selectionFrameStars!.Single(s => s.IsPrimary).X.Should().BeApproximately(selected.Primary.Position.X, 0.01);
        (await guide!).Success.Should().BeTrue();
        (await whileGuiding!).Error.Should().Be(StarSelectionError.Busy);
        h.Events.OfType<StarSelectedEvent>().Should().ContainSingle("no automatic selection replaced it");
        var firstLock = h.Events.OfType<LockPositionSetEvent>().First();
        firstLock.X.Should().BeApproximately(chosen.X, 1.5);
        firstLock.Y.Should().BeApproximately(chosen.Y, 1.5);
        h.Events.OfType<GuideStepEvent>().Where(s => s.Time > 120).Should().Contain(s => s.StarsUsed > 1, "multi-star guiding on the chosen star");
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.SecondaryStarsRefreshed);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Selecting_a_star_needs_looping_exposures()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        (await h.Guider.SelectStarAsync(new GuidePoint(500, 500))).Error.Should().Be(StarSelectionError.NotLooping);
    }

    [Test]
    public async Task Finds_the_secondary_stars_again_when_they_stay_lost()
    {
        // what a guide star changing to a neighbour does to the secondaries: the guide star stays, the stars around it
        // change (set A disappears, set B appears) and the secondaries are never found again at their old offsets
        const double swapSec = 600;
        var stars = new List<SimStar> { new(0, 0, 9.5) };
        SimStar[] setA = [new(-300, 200, 10.0), new(250, -150, 10.2), new(400, 250, 10.4), new(-450, -200, 10.6), new(100, 300, 10.8)];
        SimStar[] setB = [new(-200, -300, 10.0), new(350, 100, 10.2), new(-500, 100, 10.4), new(200, 350, 10.6), new(-150, 380, 10.8)];
        stars.AddRange(setA);
        stars.AddRange(setB);
        var occlusions = Enumerable.Range(1, setA.Length).Select(i => new StarOcclusion(swapSec, 1e6, i))
            .Concat(Enumerable.Range(1 + setA.Length, setB.Length).Select(i => new StarOcclusion(0, swapSec, i)))
            .ToList();
        var scenario = SimulatorScenario.GoodMount with { Sky = SimulatorScenario.GoodMount.Sky with { Stars = stars, Occlusions = occlusions } };
        var h = new Harness(scenario);
        Task<SettleResult>? guide = null;
        h.OnEvent = e =>
        {
            // the automatic selection takes the brightest star, the one that stays
            if (e is LoopingExposuresEvent && guide is null)
            {
                guide = h.Guider.StartGuidingAsync(Settle);
            }
        };

        await h.RunFor(TimeSpan.FromMinutes(20));

        (await guide!).Success.Should().BeTrue();
        var refreshed = h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.SecondaryStarsRefreshed).ToList();
        refreshed.Should().ContainSingle();
        double refreshSec = h.Seconds(refreshed[0].Timestamp);
        refreshSec.Should().BeInRange(swapSec + 50, swapSec + 120, "after 30 frames of 2 s with every secondary lost");
        refreshed[0].Detail.Should().Be("5 secondary stars (before: 5)");

        var steps = h.Events.OfType<GuideStepEvent>().Where(s => !s.IsSettling).ToList();
        double MultiStarShare(double from, double to)
        {
            var window = steps.Where(s => h.Seconds(s.Timestamp) > from && h.Seconds(s.Timestamp) < to).ToList();
            window.Should().NotBeEmpty();
            return window.Count(s => s.StarsUsed > 1) / (double)window.Count;
        }

        double before = MultiStarShare(swapSec - 200, swapSec);
        double lost = MultiStarShare(swapSec + 10, swapSec + 50);
        double after = MultiStarShare(refreshSec + 60, double.MaxValue);
        TestContext.Out.WriteLine($"multi-star frames: {before:P0} before the swap, {lost:P0} with set A lost, {after:P0} after the refresh");
        before.Should().BeGreaterThan(0.9);
        lost.Should().Be(0, "every secondary is lost");
        after.Should().BeGreaterThan(0.9, "guiding on set B");
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
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
        (last.ExposureMs + last.CameraMs + last.ProcessingMs + last.PulseMs + last.OtherMs)
            .Should().BeApproximately(last.CycleMs, 1e-6, "the parts of one cycle add up to it");
    }

    /// <summary>Wires a simulator, a guider and an event recorder together.</summary>
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
