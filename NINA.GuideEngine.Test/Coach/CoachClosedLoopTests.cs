// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.Guiding;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Coach;

/// <summary>
/// Guiding Coach sessions against the closed-loop simulator in virtual time: measurements are checked against the
/// simulator truth, and every exit path must leave the guider as it was (settings, guiding output, guiding state).
/// </summary>
[TestFixture]
[NonParallelizable]
public class CoachClosedLoopTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    /// <summary>A mount with only the effects under test and modest seeing.</summary>
    private static SimulatorScenario Quiet(MountSimConfig? mount = null, double seeing = 0.4) => SimulatorScenario.GoodMount with
    {
        Name = "Quiet",
        Mount = mount ?? new MountSimConfig { RandomWalkArcsecPerSqrtSec = 0.01 },
        Sky = SimulatorScenario.GoodMount.Sky with { SeeingJitterArcsec = seeing },
    };

    [Test]
    public async Task Drift_recovers_seeing_periodic_error_and_polar_alignment()
    {
        var mount = new MountSimConfig
        {
            PeriodicError = [new PeriodicErrorTerm(8.0, 120.0)],
            DecDriftArcsecPerMin = 2.0,
            RandomWalkArcsecPerSqrtSec = 0.01,
        };
        var h = new Harness(Quiet(mount, seeing: 0.4));
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift], DriftSeconds = 300 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var d = status.Drift!;
        double cosDec = Math.Cos(20 * Math.PI / 180);
        TestContext.Out.WriteLine($"period {d.PeriodicErrorPeriodSeconds} s, amplitude {d.PeriodicErrorAmplitudeArcsec}″ (truth {8 * cosDec:F2}), " +
            $"PAE {d.PolarAlignmentErrorArcmin}′ (truth {3.8197 * 2 / cosDec:F2}), seeing {d.SeeingTotalArcsec}″, max rate {d.RaMaxRateArcsecPerSec}");
        d.PeriodicErrorPeriodSeconds!.Value.Should().BeApproximately(120, 12);
        d.PeriodicErrorAmplitudeArcsec!.Value.Should().BeApproximately(8 * cosDec, 0.15 * 8 * cosDec, "PE is measured on the sky");
        d.RaMaxRateArcsecPerSec!.Value.Should().BeApproximately(8 * cosDec * 2 * Math.PI / 120, 0.2 * 8 * cosDec * 2 * Math.PI / 120);
        d.PolarAlignmentErrorArcmin!.Value.Should().BeApproximately(3.8197 * 2 / cosDec, 0.15 * 3.8197 * 2 / cosDec);
        d.DeclinationAssumed.Should().BeFalse();

        // seeing: 0.4″ per axis for 1 s, averaging as 1/√T over the 2 s exposure, both axes: 0.4″ for a single star; the
        // multi-star position averages the half of the variance that is not common to all stars (SeeingCommonFraction 0.5)
        double singleStar = Math.Sqrt(2) * 0.4 / Math.Sqrt(2.0);
        d.SeeingTotalArcsec!.Value.Should().BeInRange(0.85 * Math.Sqrt(0.5) * singleStar, 1.1 * singleStar);
        d.Samples.Should().HaveCountGreaterThan(100);

        // the PE model of the contract reproduces the published samples
        double Model(double t) => d.PeriodicErrorOffsetArcsec!.Value + d.RaDriftArcsecPerMin!.Value * t / 60
            + d.PeriodicErrorAmplitudeArcsec!.Value * Math.Sin(2 * Math.PI * t / d.PeriodicErrorPeriodSeconds!.Value + d.PeriodicErrorPhaseRad!.Value);
        double residual = Math.Sqrt(d.Samples.Average(x => Math.Pow(x.Ra - Model(x.T), 2)));
        TestContext.Out.WriteLine($"model residual {residual:F3}″ (RA seeing {d.SeeingRaArcsec}″)");
        residual.Should().BeLessThan(1.5 * d.SeeingRaArcsec!.Value);

        status.Findings.Should().Contain(f => f.Code == CoachCodes.DriftPolarAlignment && f.Severity == CoachSeverities.Warning);
        status.Findings.Should().Contain(f => f.Code == CoachCodes.DriftPeriodicError && (double?)f.Parameters["periodSeconds"] != null);
        status.Findings.Single(f => f.Code == CoachCodes.DriftMinMove).Changes.Select(c => c.Name)
            .Should().Equal(CoachSettingNames.RaMinMove, CoachSettingNames.DecMinMove);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    [Category("Slow")]
    public async Task Drift_reports_the_periodic_error_Predictive_guiding_learned()
    {
        // a drift measurement shorter than the 479 s worm turn cannot see the period; the learned curve can
        var model = (await PeriodicErrorHarness.Guide(PeriodicErrorHarness.Worm(), periodicError: true, TimeSpan.FromMinutes(30), teeth: 180)).Models[^1];
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive, new Dictionary<string, double> { ["wormTeeth"] = 180 });
        var h = new Harness(PeriodicErrorHarness.Worm(), s => s with { RaAlgorithm = predictive });
        h.Guider.RestorePeriodicError(model);
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift], DriftSeconds = 120 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var pe = status.Findings.Single(f => f.Code == CoachCodes.DriftPeriodicError);
        TestContext.Out.WriteLine(pe.Message);
        pe.Parameters["learned"].Should().Be(true);
        pe.Parameters["wormTeeth"].Should().Be(180);
        ((double)pe.Parameters["periodSeconds"]!).Should().BeApproximately(PeriodicErrorHarness.WormPeriod, 0.1);
        double onSky = 15 * Math.Cos(20 * Math.PI / 180);
        ((double)pe.Parameters["amplitudeArcsec"]!).Should().BeApproximately(onSky, 0.15 * onSky);
        pe.Severity.Should().Be(CoachSeverities.Warning);
        pe.Message.Should().Contain("learned by Predictive guiding");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Mount_response_measures_backlash_stiction_and_asymmetric_rates()
    {
        var mount = new MountSimConfig
        {
            DecBacklashArcsec = 12.0,
            RaStictionMs = 200,
            DecSouthEfficiency = 0.6,
            DecDriftArcsecPerMin = 0.3,
            RandomWalkArcsecPerSqrtSec = 0.01,
        };
        var h = new Harness(Quiet(mount, seeing: 0.3));
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse], DriftSeconds = 120 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var r = status.Response!;
        // the dead band takes 1/0.6 longer with the slower South motor; its angle is the configured 12″
        double truthMs = mount.DecBacklashMs / 0.6;
        TestContext.Out.WriteLine($"backlash {r.BacklashMs} ms / {r.BacklashArcsec}″ ({r.BacklashState}), truth {truthMs:F0} ms / 12″; " +
            $"min pulse RA {r.MinEffectivePulseRaMs} Dec {r.MinEffectivePulseDecMs}; asym RA {r.AsymmetryRa} Dec {r.AsymmetryDec}; rate RA {r.RateRatioRa} Dec {r.RateRatioDec}");
        foreach (var p in r.Pulses)
        {
            TestContext.Out.WriteLine($"  {p.Direction} {p.DurationMs} ms: {p.MovedArcsec}″ / {p.ExpectedArcsec}″ = {p.Ratio}");
        }

        r.BacklashState.Should().Be(CoachBacklashStates.Measured);
        r.LargeMoveBacklashMs!.Value.Should().BeApproximately(truthMs, 0.12 * truthMs);
        r.LargeMoveBacklashArcsec!.Value.Should().BeApproximately(12.0, 1.5);
        r.BacklashPoints.Should().HaveCountGreaterThan(4);

        // equal alternating pulses with the slower South motor creep North through the play (as guiding pulses would): the
        // reversal test finds a guiding value below the large-move one
        r.ReversalMoves.Should().NotBeEmpty();
        r.BacklashMs!.Value.Should().BeInRange(0, r.LargeMoveBacklashMs!.Value);
        r.MinEffectivePulseRaMs.Should().Be(500, "200 ms of stiction: 250 ms pulses move less than half");
        r.MinEffectivePulseDecMs.Should().Be(100);
        r.AsymmetryDec!.Value.Should().BeApproximately(1 / 0.6, 0.2);
        r.AsymmetryRa!.Value.Should().BeApproximately(1.0, 0.15);
        r.RateRatioRa!.Value.Should().BeGreaterThan(1.1, "stiction made the calibration underestimate the RA rate");

        status.Findings.Should().Contain(f => f.Id == "response.minPulse:Ra" && f.Changes.Any(c => c.Name == CoachSettingNames.RaMinMove));
        status.Findings.Should().Contain(f => f.Id == "response.asymmetry:Dec");
        var blc = status.Findings.Single(f => f.Code == CoachCodes.ResponseDecBacklash);
        blc.Changes.Should().Contain(c => c.Name == CoachSettingNames.BacklashCompensation && c.Value == "true" && c.CurrentValue == "false");
        blc.Changes.Should().Contain(c => c.Name == CoachSettingNames.BacklashPulseMs);
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.RunawayDetected || a.Code == GuideErrorCode.MountNotResponding,
            "measurements never trip the safety monitors");
        h.Events.OfType<GuideStepEvent>().Where(e => e.CoachMeasurement).Should().OnlyContain(e => e.RaDuration == 0 && e.DecDuration == 0);
        h.AssertRestoredAndGuiding();
    }

    [TestCase(DecGuideMode.Auto, false)]
    [TestCase(DecGuideMode.Auto, true)]
    [TestCase(DecGuideMode.Drift, false)]
    [TestCase(DecGuideMode.Auto, false, true, TestName = "Large_backlash_and_a_steady_dec_drift_suggest_the_drift_mode, not the play model")]
    public async Task Large_backlash_and_a_steady_dec_drift_suggest_the_drift_mode(DecGuideMode current, bool compensation, bool predictive = false)
    {
        var mount = new MountSimConfig { DecBacklashArcsec = 12.0, DecDriftArcsecPerMin = 1.5, RandomWalkArcsecPerSqrtSec = 0.01 };
        var h = new Harness(Quiet(mount, seeing: 0.3), s => s with
        {
            DecGuideMode = current,
            DecAlgorithm = predictive ? new AlgorithmSettings(GuideAlgorithmKind.Predictive) : s.DecAlgorithm,
            Backlash = s.Backlash with { Enabled = compensation, PulseMs = compensation ? 1500 : s.Backlash.PulseMs },
        });
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse], DriftSeconds = 120 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var finding = status.Findings.SingleOrDefault(f => f.Code == CoachCodes.DriftDecGuideMode);
        h.AssertRestoredAndGuiding();
        status.Findings.Single(f => f.Code == CoachCodes.ResponseDecBacklash).Changes.Should().BeEmpty(
            "backlash compensation works only while both directions are guided: not suggested with or next to Drift");
        if (current == DecGuideMode.Drift)
        {
            finding.Should().BeNull("Dec already guides along the drift");
            return;
        }

        finding.Should().NotBeNull();
        TestContext.Out.WriteLine(finding!.Message);
        finding.Parameters["mode"].Should().Be("Drift", "it follows the drift when that reverses (another target, a flip), a fixed North or South wouldn't");
        var mode = new CoachSettingChange(CoachSettingNames.DecGuideMode, "Drift", "Auto");
        if (compensation)
        {
            finding.Changes.Should().Equal(mode, new CoachSettingChange(CoachSettingNames.BacklashCompensation, "false", "true"));
        }
        else
        {
            finding.Changes.Should().ContainSingle().Which.Should().Be(mode);
        }

        finding.Message.Should().Contain("one direction only, following the drift");
    }

    [Test]
    public async Task Large_backlash_without_drift_and_Predictive_Dec_suggests_the_play_model()
    {
        var mount = new MountSimConfig { DecBacklashArcsec = 12.0, RandomWalkArcsecPerSqrtSec = 0.01 };
        var h = new Harness(Quiet(mount, seeing: 0.3), s => s with { DecAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive) });
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse], DriftSeconds = 120 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        h.AssertRestoredAndGuiding();
        var r = status.Response!;
        TestContext.Out.WriteLine($"backlash {r.BacklashMs} ms, after a long move {r.LargeMoveBacklashMs} ms (truth {mount.DecBacklashMs:F0} ms)");
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.DriftDecGuideMode, "no Dec drift to follow");
        var blc = status.Findings.Single(f => f.Code == CoachCodes.ResponseDecBacklash);
        TestContext.Out.WriteLine(blc.Message);
        blc.Parameters["playModel"].Should().Be(true);
        blc.Changes.Should().Contain(new CoachSettingChange(CoachSettingNames.BacklashModel, "PlayModel", "Fixed"));
        blc.Changes.Should().Contain(c => c.Name == CoachSettingNames.BacklashCompensation && c.Value == "true");
        blc.Changes.Single(c => c.Name == CoachSettingNames.BacklashPulseMs).Value
            .Should().Be(CoachSettingsMap.Format((int)(Math.Floor(Math.Min(r.BacklashMs!.Value, 8000) / 10) * 10)), "the guiding value seeds the model");
    }

    [Test]
    public async Task Full_session_from_guiding_recommends_proves_and_restores()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecBacklashArcsec = 8.0, DecDriftArcsecPerMin = 1.0 },
        };
        var h = new Harness(scenario);
        var options = new CoachOptions
        {
            ExposureSeconds = [1, 2],
            Gains = [0, 60],
            FramesPerCombination = 3,
            DriftSeconds = 120,
            TrialSeconds = 60,
        };
        var coachEvents = new List<CoachStatus>();
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent c)
            {
                lock (coachEvents)
                {
                    coachEvents.Add(c.Status);
                }
            }
        };
        var status = await h.RunFromGuiding(options, TimeSpan.FromMinutes(90));

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        status.Steps.Should().OnlyContain(s => s.State == CoachStepStates.Done);
        status.Camera!.Results.Should().HaveCountGreaterThanOrEqualTo(4);
        status.Camera.Recommended.Should().NotBeNull();
        status.Drift!.SeeingTotalArcsec.Should().BeGreaterThan(0);
        status.Response!.BacklashState.Should().Be(CoachBacklashStates.Measured);
        status.Response.BacklashMs!.Value.Should().BeApproximately(scenario.Mount.DecBacklashMs, 0.15 * scenario.Mount.DecBacklashMs);
        status.Trials.Select(t => t.Id).Should().StartWith(CoachTrialIds.A).And.EndWith(CoachTrialIds.A2);
        status.Trials.Should().Contain(t => t.Id == CoachTrialIds.B && t.Settings.Count > 0);
        status.Trials.Where(t => t.State == CoachTrialStates.Done).Should().HaveCountGreaterThanOrEqualTo(3);
        status.Trials.Should().ContainSingle(t => t.IsWinner);
        status.Findings.Should().Contain(f => f.Code == CoachCodes.TrialsWinner || f.Code == CoachCodes.TrialsNoImprovement);
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.ResponseMinPulse, "the good mount has no stiction");
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.CameraDefocused, "3 px / 4.5″ stars are in focus");
        foreach (var t in status.Trials)
        {
            TestContext.Out.WriteLine($"trial {t.Id} {t.State}: {t.RmsTotalArcsec}″ osc {t.OscillationIndex} frames {t.Frames} " +
                string.Join(", ", t.Settings.Select(c => $"{c.Name}={c.Value} (was {c.CurrentValue})")));
        }

        var report = status.Report!;
        report.GuidedSource.Should().Be(CoachGuidedSources.Trials);
        report.Grade.Should().NotBe(CoachGrades.Unknown);
        report.Steps.Should().Equal(CoachStepNames.All);
        report.ProfileName.Should().Be("Test");
        report.Actions.All(id => report.Findings.Any(f => f.Id == id && (f.Severity == CoachSeverities.Warning || f.Severity == CoachSeverities.Problem)))
            .Should().BeTrue("actions are the warning/problem findings");
        TestContext.Out.WriteLine($"grade {report.Grade} ({report.GradeRatio}), guided {report.GuidedRmsArcsec}″ = seeing {report.SeeingArcsec} + noise {report.CentroidNoiseArcsec} + mount {report.MountArcsec}");
        foreach (var f in report.Findings)
        {
            TestContext.Out.WriteLine($"  [{f.Severity}] {f.Id} impact {f.ImpactArcsec}: {f.Message}");
        }

        coachEvents.Should().Contain(s => s.Step == CoachStepNames.Drift && s.Drift != null && s.Drift.Samples.Count > 0, "live drift samples are published");
        coachEvents.Select(s => s.Progress).Should().BeInAscendingOrder();
        h.Coach.GetHistory(10).Should().ContainSingle(r => r.Id == report.Id).Which.Drift!.Samples.Should().BeEmpty("the history has no raw samples");
        h.AssertRestoredAndGuiding();

        // apply the suggestion: host settings change and the finding/trial is marked applied
        h.Coach.ApplyActions(["trial:B"], out var error).Should().BeTrue(error);
        var b = status.Trials.Single(t => t.Id == CoachTrialIds.B);
        foreach (var c in b.Settings)
        {
            CoachSettingsMap.GetValue(h.Guider.BaseSettings, c.Name).Should().Be(c.Value);
        }

        h.Coach.Status.Trials.Single(t => t.Id == CoachTrialIds.B).Applied.Should().BeTrue();
        h.Coach.GetHistory(1).Single().Trials.Single(t => t.Id == CoachTrialIds.B).Applied.Should().BeTrue();
        h.Coach.ApplyActions(["nope"], out _).Should().BeFalse();
    }

    [Test]
    public async Task Predictive_axes_get_no_min_move_or_aggression_advice()
    {
        // stiction and wind make the coach suggest min moves for PHD2 algorithms; the Predictive algorithm reads none
        var mount = new MountSimConfig
        {
            RaStictionMs = 200,
            DecDriftArcsecPerMin = 0.3,
            RandomWalkArcsecPerSqrtSec = 0.01,
            Wind = new WindGustConfig(3, 2.0, 3),
        };
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive);
        var h = new Harness(Quiet(mount, seeing: 0.3), s => s with { RaAlgorithm = predictive, DecAlgorithm = predictive });
        var status = await h.RunFromGuiding(new CoachOptions
        {
            Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse, CoachStepNames.Trials],
            DriftSeconds = 120,
            TrialSeconds = 60,
        });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        string[] phd2Parameters = [CoachSettingNames.RaMinMove, CoachSettingNames.DecMinMove, CoachSettingNames.RaAggression,
            CoachSettingNames.RaHysteresis, CoachSettingNames.DecAggression];
        foreach (var f in status.Findings)
        {
            TestContext.Out.WriteLine($"  [{f.Severity}] {f.Id}: {string.Join(", ", f.Changes.Select(c => $"{c.Name}={c.Value}"))}");
        }

        status.Findings.SelectMany(f => f.Changes).Select(c => c.Name).Should().NotIntersectWith(phd2Parameters);
        status.Trials.SelectMany(t => t.Settings).Select(c => c.Name).Should().NotIntersectWith(phd2Parameters);
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.DriftMinMove, "min move is all that finding says");
        status.Findings.Should().Contain(f => f.Id == "response.minPulse:Ra", "the stiction itself is still worth knowing");
        status.Trials.Should().OnlyContain(t => t.Kind == CoachTrialKinds.Current || t.Kind == CoachTrialKinds.CurrentRepeat, "no camera check: nothing else to try");
        status.Trials.Should().NotContain(t => t.IsWinner);
        status.Findings.Should().ContainSingle(f => f.Code == CoachCodes.TrialsNothingToTry).Which.Parameters["predictive"].Should().Be(true);
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.TrialsNoImprovement || f.Code == CoachCodes.TrialsWinner);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Cancel_restores_settings_output_and_guiding()
    {
        // 0.1 s exposures: the camera check recommends a longer, clearly steadier one (significant with 6 frames per
        // combination), applied temporarily for the drift step
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { ExposureMs = 100 });
        bool overlaySeen = false;
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 30 } })
            {
                overlaySeen |= h.Guider.Settings.ExposureMs != 100;
                h.Coach.Cancel();
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions
        {
            Steps = [CoachStepNames.CameraCheck, CoachStepNames.Drift],
            ExposureSeconds = [1, 2],
            Gains = [0],
            FramesPerCombination = 6,
        });

        status.Phase.Should().Be(CoachPhases.Cancelled);
        status.MessageCode.Should().BeNull();
        status.Steps.Single(s => s.Name == CoachStepNames.CameraCheck).State.Should().Be(CoachStepStates.Done);
        overlaySeen.Should().BeTrue("the recommended exposure was used during the drift step");
        h.AfterSession!.Settings.ExposureMs.Should().Be(100);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task External_start_guiding_interrupts_the_session()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        Task<SettleResult>? external = null;
        h.OnEvent = e =>
        {
            if (external is null && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                external = h.Guider.StartGuidingAsync(Settle);
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.Trials] });

        status.Phase.Should().Be(CoachPhases.Cancelled);
        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.MessageParameters["reason"].Should().Be(CoachInterruptReasons.Guiding);
        status.Findings.Should().ContainSingle(f => f.Code == CoachCodes.Interrupted).Which.Parameters["reason"].Should().Be(CoachInterruptReasons.Guiding);
        external.Should().NotBeNull();
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task External_dither_and_stop_interrupt_with_their_reason()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool dithered = false;
        h.OnEvent = e =>
        {
            if (!dithered && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                dithered = true;
                _ = h.Guider.DitherAsync(3, false, Settle);
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] });
        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.Findings.Single(f => f.Code == CoachCodes.Interrupted).Parameters["reason"].Should().Be(CoachInterruptReasons.Dither);
        h.Events.OfType<GuidingDitheredEvent>().Should().ContainSingle("the dither runs once the measurement has been removed");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Star_lost_fails_the_step_and_guiding_resumes()
    {
        // clouds while the drift step runs; the step gives up after 30 s without the star
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(700, 790, 0.0, 2.0)] },
        };
        var h = new Harness(scenario);
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift], DriftSeconds = 900 });

        status.Phase.Should().Be(CoachPhases.Failed, "the only step failed");
        status.MessageCode.Should().Be(CoachCodes.StarLost);
        status.Steps.Single().State.Should().Be(CoachStepStates.Failed);
        status.Steps.Single().MessageCode.Should().Be(CoachCodes.StarLost);
        status.Drift!.Samples.Should().NotBeEmpty("partial results stay visible");
        status.Findings.Should().ContainSingle(f => f.Id == "coach.starLost:Drift" && f.Severity == CoachSeverities.Problem);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Calibration_failure_fails_the_guided_steps_and_restores()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        h.Sim.Mount.NotResponding = true;
        var status = await h.RunFromLooping(new CoachOptions
        {
            Steps = [CoachStepNames.CameraCheck, CoachStepNames.Drift, CoachStepNames.MountResponse],
            ExposureSeconds = [1],
            Gains = [0],
            FramesPerCombination = 3,
        });

        status.Phase.Should().Be(CoachPhases.Complete, "the camera check succeeded");
        status.Steps.Single(s => s.Name == CoachStepNames.CameraCheck).State.Should().Be(CoachStepStates.Done);
        status.Steps.Single(s => s.Name == CoachStepNames.Drift).MessageCode.Should().Be(CoachCodes.CalibrationFailed);
        status.Steps.Single(s => s.Name == CoachStepNames.MountResponse).MessageCode.Should().Be(CoachCodes.CalibrationFailed);
        h.Events.OfType<CalibrationFailedEvent>().Should().ContainSingle("a failed calibration is not repeated for every step");
        status.Findings.Should().Contain(f => f.Id == "coach.calibrationFailed:Drift");
        status.Report.Should().NotBeNull();
        h.AssertRestoredAndGuiding(expectGuiding: false);
        h.AfterSession!.State.Should().BeOneOf(GuiderState.Selected, GuiderState.Looping);
    }

    [Test]
    public async Task No_calibration_allowed_fails_with_noCalibration_and_rejections_carry_codes()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var status = await h.RunFromLooping(new CoachOptions { Steps = [CoachStepNames.Drift], AllowCalibration = false });
        status.Phase.Should().Be(CoachPhases.Failed);
        status.MessageCode.Should().Be(CoachCodes.NoCalibration);
        h.Events.OfType<StartCalibrationEvent>().Should().BeEmpty();

        h.Sim.Camera.IsConnected = false;
        var rejected = h.Coach.Start();
        rejected.Accepted.Should().BeFalse();
        rejected.MessageCode.Should().Be(CoachCodes.NotConnected);
        rejected.Status.SessionId.Should().Be(status.SessionId, "a rejection leaves the last session's status untouched");
        h.Coach.Status.MessageCode.Should().Be(CoachCodes.NoCalibration);
        h.Coach.Status.GainMax.Should().Be(100, "the gain range is always reported");
    }

    [Test]
    public async Task Skip_keeps_partial_drift_results_and_continues()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool skipped = false;
        h.OnEvent = e =>
        {
            if (!skipped && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 60 } })
            {
                skipped = true;
                h.Coach.SkipStep().Should().BeTrue();
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse], DriftSeconds = 600 });

        status.Phase.Should().Be(CoachPhases.Complete);
        status.Steps.Single(s => s.Name == CoachStepNames.Drift).State.Should().Be(CoachStepStates.Skipped);
        status.Drift!.ElapsedSeconds.Should().BeLessThan(200, "the drift step ended early");
        status.Drift.SeeingTotalArcsec.Should().NotBeNull("partial results are kept");
        status.Findings.Should().Contain(f => f.Code == CoachCodes.DriftSeeing);
        status.Steps.Single(s => s.Name == CoachStepNames.MountResponse).State.Should().Be(CoachStepStates.Done);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task A_real_goto_interrupts_with_slew_within_a_frame()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        DateTimeOffset? slewAt = null;
        h.OnEvent = e =>
        {
            if (slewAt is null && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                slewAt = e.Timestamp;
                h.Sim.Mount.StartSlew(new SkyOffset(3600, 0), TimeSpan.FromSeconds(2));
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] }, expectGuiding: false);

        TestContext.Out.WriteLine($"phase {status.Phase} {status.MessageCode} {status.Message} " + string.Join(";", status.Steps.Select(x => x.Name + " " + x.State + " " + x.MessageCode)));
        status.Phase.Should().Be(CoachPhases.Cancelled);
        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.MessageParameters["reason"].Should().Be(CoachInterruptReasons.Slew);
        var cancelled = h.Events.OfType<CoachStatusEvent>().First(c => c.Status.Phase == CoachPhases.Cancelled).Timestamp;
        (cancelled - slewAt!.Value).TotalSeconds.Should().BeLessThan(5, "a goto moving more than 5′ interrupts at the next frame after the move");
        h.Events.OfType<PausedEvent>().Should().ContainSingle(p => p.Reason == "Slewing", "the guider's own auto-pause takes over");
        h.AssertRestoredAndGuiding(expectGuiding: false);
    }

    [Test]
    [Category("Slow")]
    public async Task Spurious_slewing_after_pulses_does_not_cancel_the_coach()
    {
        // an INDI-like mount: "slewing" for 0.5–3 s after every pulse and at random moments
        var h = new Harness(SimulatorScenario.GoodMount, flaky: true);
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent { Status.Phase: not (CoachPhases.Running or CoachPhases.Idle) })
            {
                // afterwards a quiet mount, so resumed guiding can be checked without auto-pauses
                h.Flaky!.BusyAfterPulse = null;
                h.Flaky.RandomBusyPerMinute = 0;
            }
        };

        // from the session start on (calibration itself fails on any slewing report, as it always did)
        var status = await h.RunFromGuiding(new CoachOptions
        {
            ExposureSeconds = [1, 2],
            Gains = [0],
            FramesPerCombination = 3,
            DriftSeconds = 120,
            TrialSeconds = 60,
        }, TimeSpan.FromMinutes(120), atStart: x =>
        {
            x.Flaky!.BusyAfterPulse = (0.5, 3.0);
            x.Flaky.RandomBusyPerMinute = 1.0;
        });

        TestContext.Out.WriteLine($"busy reports: {h.Flaky!.BusyReports}, trials {string.Join(", ", status.Trials.Select(t => $"{t.Id} {t.State} {t.ElapsedSeconds}s"))}");
        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        status.Steps.Should().OnlyContain(s => s.State == CoachStepStates.Done);
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.Interrupted);
        status.Response!.Pulses.Should().HaveCount(32, "every response pulse is measured (redone when it was not sent)");
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.ResponseMinPulse, "busy frames are skipped, not measured as no motion");
        status.Trials.Where(t => t.State == CoachTrialStates.Done).Should().OnlyContain(t => t.ElapsedSeconds >= 60);
        h.Flaky.BusyReports.Should().BeGreaterThan(20, "the mount really reported slewing many times");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task A_stuck_slewing_flag_interrupts_after_the_debounce_time()
    {
        var h = new Harness(SimulatorScenario.GoodMount, flaky: true);
        DateTimeOffset? stuckAt = null;
        h.OnEvent = e =>
        {
            if (stuckAt is null && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                stuckAt = e.Timestamp;
                h.Flaky!.Slewing(e.Timestamp, TimeSpan.FromSeconds(10));
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] });

        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.MessageParameters["reason"].Should().Be(CoachInterruptReasons.Slew);
        var cancelled = h.Events.OfType<CoachStatusEvent>().First(c => c.Status.Phase == CoachPhases.Cancelled).Timestamp;
        (cancelled - stuckAt!.Value).TotalSeconds.Should().BeInRange(8, 11.5, "interrupted once the report persisted 8 s");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Tracking_off_for_long_interrupts_with_trackingOff()
    {
        var h = new Harness(SimulatorScenario.GoodMount, flaky: true);
        bool done = false;
        h.OnEvent = e =>
        {
            if (!done && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                done = true;
                h.Flaky!.TrackingOff(e.Timestamp, TimeSpan.FromSeconds(10));
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] });

        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.MessageParameters["reason"].Should().Be(CoachInterruptReasons.TrackingOff);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Tracking_off_briefly_does_not_cancel_the_coach()
    {
        var h = new Harness(SimulatorScenario.GoodMount, flaky: true);
        bool done = false;
        h.OnEvent = e =>
        {
            if (!done && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                done = true;
                h.Flaky!.TrackingOff(e.Timestamp, TimeSpan.FromSeconds(2));
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        h.Flaky!.BusyReports.Should().BeGreaterThan(0, "the tracking-off report was seen");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task A_mount_failure_during_a_trial_fails_only_that_trial()
    {
        var scenario = SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { RaDriftArcsecPerMin = 20 } };
        var h = new Harness(scenario);
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent { Status.Trials: { Count: > 0 } trials })
            {
                // the mount ignores pulses while trial A guides, and responds again for A2
                h.Sim.Mount.NotResponding = trials[0].State == CoachTrialStates.Running;
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Trials], TrialSeconds = 120 });

        foreach (var t in status.Trials)
        {
            TestContext.Out.WriteLine($"trial {t.Id} {t.State} {t.RmsTotalArcsec}″ frames {t.Frames}");
        }

        TestContext.Out.WriteLine($"phase {status.Phase} {status.MessageCode} {string.Join(",", status.MessageParameters.Select(kv => kv.Key + "=" + kv.Value))}");
        foreach (var e in h.Events.Where(e => e is AlertEvent or AppStateEvent or CoachStatusEvent { Status.Phase: not "Running" } or StartGuidingEvent or GuidingStoppedEvent))
        {
            TestContext.Out.WriteLine($"  {h.Seconds(e.Timestamp):F1}s {e switch { AlertEvent a => "alert " + a.Code, AppStateEvent st => $"state {st.Previous}->{st.State}", CoachStatusEvent c => "coach " + c.Status.Phase, _ => e.Phd2Name }}");
        }

        status.Phase.Should().Be(CoachPhases.Complete);
        status.Trials.Single(t => t.Id == CoachTrialIds.A).State.Should().Be(CoachTrialStates.Failed);
        status.Trials.Single(t => t.Id == CoachTrialIds.A2).State.Should().Be(CoachTrialStates.Done);
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.MountNotResponding || a.Code == GuideErrorCode.RunawayDetected);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Live_hints_are_raised_while_guiding_normally()
    {
        // a strong one-sided Dec drift: every Dec correction goes the same way
        var scenario = SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 4 } };
        var h = new Harness(scenario);
        var hints = new List<CoachHintEvent>();
        h.OnEvent = e =>
        {
            if (e is CoachHintEvent hint)
            {
                lock (hints)
                {
                    hints.Add(hint);
                }
            }
        };
        _ = h.Guider.StartGuidingAsync(Settle);
        h.Guider.StartLooping(h.Clock.CancelAt(TimeSpan.FromMinutes(12)));
        await h.Guider.WaitForLoopAsync();

        var dec = hints.Select(x => x.Hint).Where(x => x.Code == CoachCodes.HintDecDrift).ToList();
        dec.Should().ContainSingle("the hint is rate limited to once per 10 minutes");
        dec[0].Step.Should().Be(CoachStepNames.Live);
        dec[0].Changes.Should().ContainSingle(c => c.Name == CoachSettingNames.DecGuideMode).Which.Value.Should().Be("Drift");
        h.Guider.DismissHint(dec[0].Id).Should().BeTrue();
        h.Guider.ActiveHints.Should().NotContain(x => x.Id == dec[0].Id);
    }

    [Test]
    public async Task Good_mount_with_short_exposures_shows_no_false_stiction_or_defocus()
    {
        // the full-image smoke test setup: GoodMount, 1 s exposures, 1.51″/px
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { ExposureMs = 1000 });
        var status = await h.RunFromGuiding(new CoachOptions
        {
            Steps = [CoachStepNames.CameraCheck, CoachStepNames.Drift, CoachStepNames.MountResponse],
            ExposureSeconds = [1],
            Gains = [0],
            FramesPerCombination = 3,
            DriftSeconds = 120,
        });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var r = status.Response!;
        TestContext.Out.WriteLine($"min pulse RA {r.MinEffectivePulseRaMs} Dec {r.MinEffectivePulseDecMs}");
        foreach (var p in r.Pulses)
        {
            TestContext.Out.WriteLine($"  {p.Direction} {p.DurationMs} ms: {p.MovedArcsec}″ / {p.ExpectedArcsec}″");
        }

        status.Findings.Should().NotContain(f => f.Code == CoachCodes.ResponseMinPulse);
        status.Findings.Should().NotContain(f => f.Code == CoachCodes.CameraDefocused);
        r.Pulses.Should().HaveCount(32);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Seeing_matches_the_guided_rms_floor_with_multi_star()
    {
        // a perfect mount: whatever guiding cannot remove is seeing, measured on the same multi-star offset
        var h = new Harness(Quiet(new MountSimConfig(), seeing: 0.8));
        var status = await h.RunFromGuiding(new CoachOptions
        {
            Steps = [CoachStepNames.Drift, CoachStepNames.Trials],
            DriftSeconds = 150,
            TrialSeconds = 120,
            RepeatBaseline = false,
        });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        double seeing = status.Drift!.SeeingTotalArcsec!.Value;
        var a = status.Trials.Single(t => t.Id == CoachTrialIds.A);
        var report = status.Report!;
        TestContext.Out.WriteLine($"seeing {seeing}″, trial A {a.RmsTotalArcsec}″ ({a.Frames} frames), budget: seeing {report.SeeingArcsec} + noise " +
            $"{report.CentroidNoiseArcsec} + mount {report.MountArcsec} = {report.GuidedRmsArcsec}");
        (a.RmsTotalArcsec!.Value / seeing).Should().BeInRange(0.8, 1.4, "the guided RMS floor is the (multi-star) seeing");
        report.MountArcsec!.Value.Should().BeLessThan(0.7 * report.GuidedRmsArcsec!.Value, "the budget adds up without a large mount term");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task A_cloud_over_a_trial_counts_as_its_cloud_noise()
    {
        // thin clouds over a third of trial A: its frames carry their centroid σ from the guider into the trial, whose
        // statistics count the excess of the noisier frames as cloud noise (the judge takes it out before naming a winner).
        // Predictive on both axes leaves nothing else to try: trial A only.
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive);
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { RaAlgorithm = predictive, DecAlgorithm = predictive });
        TrialObserver? observer = null;
        h.OnEvent = e =>
        {
            if (observer is null && e is CoachStatusEvent { Status.Trials: var trials }
                && trials.Any(t => t.Id == CoachTrialIds.A && t.State == CoachTrialStates.Running) && h.Guider.CoachHook is TrialObserver o)
            {
                observer = o;
                double now = h.Sim.Now;
                h.Sim.Sky.AddTransparencyWindow(new TransparencyWindow(now + 15, now + 35, 0.3, 2));
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Trials], TrialSeconds = 60, RepeatBaseline = false });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        observer.Should().NotBeNull();
        var observation = await observer!.Completion;
        var stats = TrialStatistics.Compute(observation.Samples, observation.PixelScale)!;
        var a = status.Trials.Single(t => t.Id == CoachTrialIds.A);
        TestContext.Out.WriteLine($"trial A: {a.Frames} frames, RMS {a.RmsTotalArcsec}″, cloud noise {stats.CloudNoiseArcsec:F3}″, σ " +
            string.Join(" ", observation.Samples.Select(x => x.SigmaPx is { } v ? v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "-")));
        observation.Samples.Should().NotBeEmpty().And.OnlyContain(x => x.SigmaPx > 0, "every guided trial frame carries its centroid σ");
        stats.Frames.Should().Be(a.Frames, "the trial's own statistics");
        stats.CloudNoiseArcsec.Should().BeGreaterThan(0);
        h.AssertRestoredAndGuiding();
    }

    /// <summary>Wires simulator, guider, coach and an event recorder; runs sessions in virtual time.</summary>
    private sealed class Harness
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];
        private readonly TaskCompletionSource<CoachStatus> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GuiderSettings initialSettings;
        private CancellationToken deadline;
        private int pulsedStepsAfter;
        private int framesAfter;
        private DateTimeOffset? finishedAt;

        public Harness(SimulatorScenario scenario, Func<GuiderSettings, GuiderSettings>? configure = null, bool flaky = false)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(scenario, Clock);
            var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000 };
            initialSettings = configure?.Invoke(settings) ?? settings;
            Flaky = flaky ? new FlakyMount(Sim.Mount, Clock) : null;
            Guider = new Guider(Sim.Camera, (IPulseOutput?)Flaky ?? Sim.Mount, (IMountState?)Flaky ?? Sim.Mount, Clock, initialSettings, ditherSeed: 5)
            {
                AutoStartLoop = false,
            };
            Host = new SimpleCoachHost(Guider) { ImagingScale = 1.0, ProfileName = "Test" };
            Coach = new GuidingCoach(Guider, Host);
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

                if (e is CoachStatusEvent { Status.Phase: not (CoachPhases.Running or CoachPhases.Idle) } done && Coach.Status.SessionId == done.Status.SessionId)
                {
                    finishedAt ??= e.Timestamp;
                    finished.TrySetResult(done.Status);
                }

                if (finishedAt is { } at && e is GuideStepEvent { CoachMeasurement: false } s && s.Timestamp > at && s.RaDuration + s.DecDuration > 0)
                {
                    Interlocked.Increment(ref pulsedStepsAfter);
                }

                if (finishedAt is { } at2 && e is FrameReadyEvent && e.Timestamp > at2)
                {
                    Interlocked.Increment(ref framesAfter);
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        /// <summary>Mount state double around the simulated mount (null unless requested).</summary>
        public FlakyMount? Flaky { get; }

        public Simulator Sim { get; }

        public Guider Guider { get; }

        public SimpleCoachHost Host { get; }

        public GuidingCoach Coach { get; }

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

        public bool WasGuidingBefore { get; private set; }

        /// <summary>Starts guiding, runs the coach once settled, waits for the session and a few guided frames afterwards.</summary>
        public async Task<CoachStatus> RunFromGuiding(CoachOptions options, TimeSpan? virtualBudget = null, Action<Harness>? atStart = null,
            bool expectGuiding = true, TimeSpan? guideFirst = null)
        {
            WasGuidingBefore = true;
            bool started = false;
            DateTimeOffset? settled = null;
            var previous = OnEvent;
            OnEvent = e =>
            {
                previous?.Invoke(e);
                if (e is SettleDoneEvent)
                {
                    settled ??= e.Timestamp;
                }

                if (!started && settled is { } at && e is GuideStepEvent && e.Timestamp - at >= (guideFirst ?? TimeSpan.Zero))
                {
                    started = true;
                    atStart?.Invoke(this);
                    Coach.Start(options).Accepted.Should().BeTrue();
                }
            };

            _ = Guider.StartGuidingAsync(Settle);
            return await Run(virtualBudget ?? TimeSpan.FromMinutes(60), expectGuiding);
        }

        /// <summary>Starts looping with a selected star (no guiding), then runs the coach.</summary>
        public async Task<CoachStatus> RunFromLooping(CoachOptions options, TimeSpan? virtualBudget = null)
        {
            bool started = false;
            var previous = OnEvent;
            OnEvent = e =>
            {
                previous?.Invoke(e);
                if (!started && e is StarSelectedEvent)
                {
                    started = true;
                    Coach.Start(options).Accepted.Should().BeTrue();
                }
            };

            Guider.AutoSelectStar();
            return await Run(virtualBudget ?? TimeSpan.FromMinutes(60), expectGuiding: false);
        }

        private async Task<CoachStatus> Run(TimeSpan budget, bool expectGuiding)
        {
            deadline = Clock.CancelAt(Clock.Elapsed + budget);
            Coach.LoopToken = deadline;
            Guider.StartLooping(deadline);
            var done = await Task.WhenAny(finished.Task, Task.Delay(WallClockGuard.For(budget)));
            if (done != finished.Task)
            {
                var st = Coach.Status;
                TestContext.Out.WriteLine($"TIMEOUT at {Clock.ElapsedSeconds:F0}s: step {st.Step}, guider {Guider.State}, loop {Guider.IsLoopRunning}; " +
                    string.Join("; ", st.Steps.Select(x => $"{x.Name} {x.State} {x.Detail} {x.Progress}")) + "; trials " +
                    string.Join(", ", st.Trials.Select(t => $"{t.Id} {t.State} {t.ElapsedSeconds}s {t.Frames}f")) + $"; pulses {st.Response?.Pulses.Count}");
            }

            done.Should().BeSameAs(finished.Task, "the session must finish in reasonable wall time");
            var status = await finished.Task;
            (await Task.WhenAny(Coach.Completion, Task.Delay(TimeSpan.FromMinutes(1)))).Should().BeSameAs(Coach.Completion);

            // let the guider run a little after the session (guiding resumed?), then stop
            var until = DateTime.UtcNow.AddSeconds(60);
            while (Guider.IsLoopRunning && DateTime.UtcNow < until &&
                   (expectGuiding ? Volatile.Read(ref pulsedStepsAfter) < 5 : Volatile.Read(ref framesAfter) < 10))
            {
                await Task.Delay(5);
            }

            AfterSession = new SessionEnd(Guider.State, Guider.HasCoachOverlay, Guider.HasCoachHook, Guider.GuidingOutputEnabled, Guider.Settings,
                Volatile.Read(ref pulsedStepsAfter));
            await Guider.StopCaptureAsync();
            return status;
        }

        public SessionEnd? AfterSession { get; private set; }

        public double Seconds(DateTimeOffset t) => (t - Clock.Epoch).TotalSeconds;

        /// <summary>Temporary settings removed, guiding output on, host settings unchanged and guiding resumed.</summary>
        public void AssertRestoredAndGuiding(bool expectGuiding = true)
        {
            var a = AfterSession!;
            a.Overlay.Should().BeFalse("temporary settings are removed");
            a.Hook.Should().BeFalse("no measurement hook remains");
            a.OutputEnabled.Should().BeTrue("guiding output is re-enabled");
            a.Settings.Should().Be(Guider.BaseSettings, "the effective settings are the host settings again");
            if (expectGuiding)
            {
                a.State.Should().BeOneOf(GuiderState.Guiding, GuiderState.LostLock, GuiderState.Reacquiring);
                a.PulsedStepsAfter.Should().BeGreaterThanOrEqualTo(5, "guiding with corrections continues after the session");
            }
        }
    }

    /// <summary>
    /// INDI-like mount state double around the simulated mount: reports "slewing" for a while after pulses, at random moments
    /// or in forced windows (without moving), and tracking off in forced windows.
    /// </summary>
    private sealed class FlakyMount(SimulatedMount inner, VirtualClock clock) : IPulseOutput, IMountState
    {
        private readonly object gate = new();
        private readonly Random rng = new(17);
        private readonly List<(double Start, double End, bool Tracking)> windows = [];
        private double busyUntil = double.NegativeInfinity;
        private double nextRandom = double.NaN;
        private bool wasBusy;

        public (double Min, double Max)? BusyAfterPulse { get; set; }

        public double RandomBusyPerMinute { get; set; }

        /// <summary>Number of times the double started to report a condition.</summary>
        public int BusyReports { get; private set; }

        public string Name => inner.Name;

        public bool IsConnected => inner.IsConnected;

        public bool SupportsSimultaneousPulses => inner.SupportsSimultaneousPulses;

        public void Slewing(DateTimeOffset from, TimeSpan duration) => Add(from, duration, tracking: false);

        public void TrackingOff(DateTimeOffset from, TimeSpan duration) => Add(from, duration, tracking: true);

        public async Task PulseAsync(Core.GuideDirection direction, int durationMs, CancellationToken ct)
        {
            await inner.PulseAsync(direction, durationMs, ct);
            lock (gate)
            {
                if (BusyAfterPulse is { } b)
                {
                    busyUntil = Math.Max(busyUntil, clock.ElapsedSeconds + b.Min + rng.NextDouble() * (b.Max - b.Min));
                }
            }
        }

        public MountSnapshot GetSnapshot()
        {
            var m = inner.GetSnapshot();
            lock (gate)
            {
                double now = clock.ElapsedSeconds;
                if (RandomBusyPerMinute > 0)
                {
                    if (double.IsNaN(nextRandom))
                    {
                        nextRandom = now + 60.0 / RandomBusyPerMinute * rng.NextDouble();
                    }

                    if (now >= nextRandom)
                    {
                        busyUntil = Math.Max(busyUntil, now + 1 + 2 * rng.NextDouble());
                        nextRandom = now + 60.0 / RandomBusyPerMinute * (0.5 + rng.NextDouble());
                    }
                }

                bool slewing = now < busyUntil || windows.Any(w => !w.Tracking && now >= w.Start && now < w.End);
                bool trackingOff = windows.Any(w => w.Tracking && now >= w.Start && now < w.End);
                bool busy = slewing || trackingOff;
                if (busy && !wasBusy)
                {
                    BusyReports++;
                }

                wasBusy = busy;
                return m with { IsSlewing = m.IsSlewing || slewing, IsTracking = m.IsTracking && !trackingOff };
            }
        }

        private void Add(DateTimeOffset from, TimeSpan duration, bool tracking)
        {
            lock (gate)
            {
                double start = (from - clock.Epoch).TotalSeconds;
                windows.Add((start, start + duration.TotalSeconds, tracking));
            }
        }
    }

    private sealed record SessionEnd(GuiderState State, bool Overlay, bool Hook, bool OutputEnabled, GuiderSettings Settings, int PulsedStepsAfter);
}
