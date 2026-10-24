// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;
using static NINA.GuideEngine.Test.TestSupport.PeriodicErrorHarness;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>
/// A new mode must match or beat Classic (PHD2 defaults) in the closed-loop simulator before it goes to the sky. Judged
/// on the simulator's true pointing error.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PredictiveComparisonTests
{
    private static IEnumerable<TestCaseData> Scenarios() =>
        new[] { SimulatorScenario.GoodMount, SimulatorScenario.HighSeeing, SimulatorScenario.PoorPeriodicError, SimulatorScenario.Backlash, SimulatorScenario.Clouds }
            .Select(s => new TestCaseData(s).SetArgDisplayNames(s.Name));

    [TestCaseSource(nameof(Scenarios))]
    [Category("Slow")]
    public async Task Predictive_beats_Classic(SimulatorScenario scenario)
    {
        async Task<double> TrueRms(GuideAlgorithmKind ra, GuideAlgorithmKind dec) =>
            (await ClosedLoopTests.GuidedRun(scenario, new AlgorithmSettings(ra), new AlgorithmSettings(dec))).Rms;

        double classic = await TrueRms(GuideAlgorithmKind.Hysteresis, GuideAlgorithmKind.ResistSwitch);
        double predictiveRa = await TrueRms(GuideAlgorithmKind.Predictive, GuideAlgorithmKind.ResistSwitch);
        double predictive = await TrueRms(GuideAlgorithmKind.Predictive, GuideAlgorithmKind.Predictive);
        TestContext.Out.WriteLine(
            $"{scenario.Name}: true RMS classic {classic:F3}″, predictive RA {predictiveRa:F3}″ ({predictiveRa / classic - 1:P0}), predictive both {predictive:F3}″ ({predictive / classic - 1:P0})");
        predictiveRa.Should().BeLessThan(classic);
        predictive.Should().BeLessThan(classic * 0.9);
    }
}

/// <summary>The Predictive algorithm's notes reach the guider's event stream for the host's debug log.</summary>
[TestFixture]
[NonParallelizable]
public class PredictiveNoteTests
{
    [Test]
    [Category("Slow")]
    public async Task Guider_emits_the_notes_of_both_axes()
    {
        var notes = new List<AlgorithmNoteEvent>();
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive);
        await ClosedLoopTests.GuidedRun(SimulatorScenario.GoodMount, predictive, predictive, e =>
        {
            if (e is AlgorithmNoteEvent n)
            {
                lock (notes)
                {
                    notes.Add(n);
                }
            }
        });

        foreach (var axis in new[] { GuideAxis.Ra, GuideAxis.Dec })
        {
            var kinds = notes.Where(n => n.Axis == axis).Select(n => n.Kind).ToList();
            kinds[0].Should().Be(PredictiveNoteKind.Restart);
            kinds.Should().Contain([PredictiveNoteKind.Takeover, PredictiveNoteKind.Learned, PredictiveNoteKind.Summary]);
        }

        notes.First(n => n.Kind == PredictiveNoteKind.Restart).Message.Should().Contain("guiding started");
    }

    [Test]
    [Category("Slow")]
    public async Task Classic_algorithms_emit_no_notes()
    {
        int notes = 0;
        await ClosedLoopTests.GuidedRun(SimulatorScenario.GoodMount, new AlgorithmSettings(GuideAlgorithmKind.Hysteresis),
            new AlgorithmSettings(GuideAlgorithmKind.ResistSwitch), e => notes += e is AlgorithmNoteEvent ? 1 : 0);
        notes.Should().Be(0);
    }
}

/// <summary>
/// The guider's minimum pulse (<see cref="GuiderSettings.MinPulseMs"/>, 20 ms by default) with Predictive, which has no
/// min move: on a good mount with a small Dec drift many of its corrections are a few ms long.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PredictiveMinimumPulseTests
{
    [Test]
    public async Task No_guide_pulse_is_shorter_than_the_minimum()
    {
        var predictive = new AlgorithmSettings(GuideAlgorithmKind.Predictive);
        async Task<List<int>> Pulses(Func<GuiderSettings, GuiderSettings>? configure)
        {
            var durations = new List<int>();
            await ClosedLoopTests.GuidedRun(SimulatorScenario.GoodMount, predictive, predictive, e =>
            {
                // recovery moves (fast recenter) are not algorithm pulses
                if (e is GuideStepEvent { IsRecenterMove: false } s)
                {
                    lock (durations)
                    {
                        durations.AddRange(new[] { s.RaDuration, s.DecDuration }.Where(d => d > 0));
                    }
                }
            }, configure, TimeSpan.FromMinutes(8));
            return durations;
        }

        var withMinimum = await Pulses(null);
        var anyLength = await Pulses(s => s with { MinPulseMs = 0 });
        TestContext.Out.WriteLine($"default minimum: {withMinimum.Count} pulses, shortest {withMinimum.Min()} ms; " +
            $"none (PHD2): {anyLength.Count} pulses, {anyLength.Count(d => d < AxisCorrector.DefaultMinPulseMs)} under {AxisCorrector.DefaultMinPulseMs} ms");
        withMinimum.Should().NotBeEmpty().And.OnlyContain(d => d >= AxisCorrector.DefaultMinPulseMs);
        anyLength.Count(d => d < AxisCorrector.DefaultMinPulseMs).Should().BeGreaterThan(10);
    }
}

/// <summary>Settings changes while guiding: what the Predictive algorithm learned survives the ones that don't touch its model.</summary>
[TestFixture]
[NonParallelizable]
public class PredictiveSettingsChangeTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    private static AlgorithmSettings Predictive(string? name = null, double value = 0) =>
        new(GuideAlgorithmKind.Predictive, name is null ? null : new Dictionary<string, double> { [name] = value });

    [Test]
    public async Task Parameter_changes_keep_what_it_learned_per_axis()
    {
        var scenario = SimulatorScenario.GoodMount;
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings
        {
            FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, RaAlgorithm = Predictive(), DecAlgorithm = Predictive(),
        };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        var seen = new Dictionary<int, (IGuideAlgorithm Ra, IGuideAlgorithm Dec)>();
        var retuned = settings with { RaAlgorithm = Predictive("pace", 0.8), DecAlgorithm = Predictive("minMove", 0.05) };
        int steps = 0;
        int changes = 0;
        guider.EventRaised += (_, e) =>
        {
            if (e is SettingsChangedEvent)
            {
                changes++;
            }

            if (e is not GuideStepEvent { IsSettling: false })
            {
                return;
            }

            // runs on the guide loop; settings apply between frames
            seen[++steps] = (guider.RaAlgorithm, guider.DecAlgorithm);
            if (steps == 150)
            {
                guider.UpdateSettings(retuned);
            }
            else if (steps == 160)
            {
                guider.UpdateSettings(retuned with { RaAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Hysteresis) });
            }
        };

        _ = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + TimeSpan.FromMinutes(12)));
        await guider.WaitForLoopAsync().Within(TimeSpan.FromMinutes(12));

        seen.Should().ContainKey(170);
        var before = seen[150];
        var ra = before.Ra.Should().BeOfType<PredictiveAlgorithm>().Subject;
        var dec = before.Dec.Should().BeOfType<PredictiveAlgorithm>().Subject;
        int learnedBefore = ra.State.FramesLearned;

        seen[155].Ra.Should().BeSameAs(ra, "a new pace doesn't touch the model");
        seen[155].Dec.Should().BeSameAs(dec, "neither does a min move");
        ra.Pace.Should().Be(0.8);
        changes.Should().Be(2, "each settings update while running is announced (for the guide log)");
        dec.MinMove.Should().Be(0.05);

        seen[165].Ra.Should().BeOfType<HysteresisAlgorithm>();
        seen[165].Dec.Should().BeSameAs(dec, "a change of the other axis leaves it alone");
        dec.State.FramesLearned.Should().BeGreaterThan(learnedBefore);
        dec.State.Phase.Should().NotBe(PredictivePhase.Learning);
        ra.State.FramesLearned.Should().BeGreaterThanOrEqualTo(PredictiveAlgorithm.ScoreWindowFrames);
    }

    [Test]
    public async Task Unchanged_settings_applied_again_keep_what_it_learned()
    {
        // the Guiding Coach re-applies the host's settings unchanged before a drift or mount-response measurement
        var scenario = SimulatorScenario.GoodMount;
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings
        {
            FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, RaAlgorithm = Predictive(), DecAlgorithm = Predictive(),
        };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        var seen = new Dictionary<int, (IGuideAlgorithm Ra, IGuideAlgorithm Dec, int Frames)>();
        int steps = 0;
        guider.EventRaised += (_, e) =>
        {
            if (e is not GuideStepEvent { IsSettling: false })
            {
                return;
            }

            seen[++steps] = (guider.RaAlgorithm, guider.DecAlgorithm, guider.Statistics?.Session.IncludedFrames ?? 0);
            if (steps == 150)
            {
                guider.UpdateSettings(settings);
            }
        };

        _ = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + TimeSpan.FromMinutes(12)));
        await guider.WaitForLoopAsync().Within(TimeSpan.FromMinutes(12));

        seen.Should().ContainKey(160);
        seen[160].Ra.Should().BeSameAs(seen[150].Ra);
        seen[160].Dec.Should().BeSameAs(seen[150].Dec);
        seen[160].Frames.Should().BeGreaterThan(seen[150].Frames, "the statistics go on");
    }

    [Test]
    public async Task Settling_after_a_dither_teaches_nothing()
    {
        var scenario = SimulatorScenario.GoodMount;
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings
        {
            FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, RaAlgorithm = Predictive(), DecAlgorithm = Predictive(),
        };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        int steps = 0;
        bool dithered = false;
        int? learnedAtDither = null;
        int? learnedAtSettled = null;
        int settleFrames = 0;
        guider.EventRaised += (_, e) =>
        {
            var ra = (PredictiveAlgorithm)guider.RaAlgorithm;
            switch (e)
            {
                case GuideStepEvent { IsSettling: false } when ++steps == 200 && !dithered:
                    dithered = true;
                    learnedAtDither = ra.State.FramesLearned;
                    _ = guider.DitherAsync(4, false, Settle);
                    break;
                case GuideStepEvent { IsSettling: true } when dithered && learnedAtSettled is null:
                    settleFrames++;
                    break;
                case SettleDoneEvent when dithered && learnedAtSettled is null:
                    learnedAtSettled = ra.State.FramesLearned;
                    break;
            }
        };

        _ = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + TimeSpan.FromMinutes(14)));
        await guider.WaitForLoopAsync().Within(TimeSpan.FromMinutes(14));

        settleFrames.Should().BeGreaterThan(2, "the dither needs a few frames to settle");
        learnedAtSettled.Should().NotBeNull();
        learnedAtSettled!.Value.Should().BeInRange(learnedAtDither!.Value, learnedAtDither.Value + 1,
            "only the frame that ends the settling may count, not the settling frames");
        ((PredictiveAlgorithm)guider.RaAlgorithm).State.FramesLearned.Should().BeGreaterThan(learnedAtSettled.Value + 50);
    }

    [Test]
    public void Only_the_parameters_changing_keeps_it_other_changes_start_over()
    {
        var learned = new PredictiveAlgorithm();
        for (int k = 0; k < 150; k++)
        {
            learned.CorrectionApplied(learned.Result(0.1 * Math.Sin(k), DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2 * k)));
        }

        learned.TakeNotes();
        var tuned = Predictive("pace", 0.7);
        IGuideAlgorithm Next(AlgorithmSettings was, AlgorithmSettings next, bool sameMinMove = true, bool samePixels = true) =>
            Guider.NextAlgorithm(learned, was, next, GuideAxis.Ra, 0.2, sameMinMove, samePixels);

        Next(Predictive(), Predictive()).Should().BeSameAs(learned);
        Next(Predictive(), Predictive(), sameMinMove: false).Should().BeSameAs(learned, "the smart min move is not Predictive's");
        learned.TakeNotes().Should().BeEmpty("nothing changed for it");
        Next(Predictive(), tuned).Should().BeSameAs(learned);
        learned.Pace.Should().Be(0.7);
        learned.State.FramesLearned.Should().Be(149);
        learned.TakeNotes().Should().ContainSingle(n => n.Kind == PredictiveNoteKind.Parameters).Which.Message.Should().Contain("149");

        Next(tuned, Predictive()).Should().BeSameAs(learned);
        learned.Pace.Should().Be(PredictiveAlgorithm.DefaultPace, "parameters not given go back to their defaults");

        Next(Predictive(), Predictive(), samePixels: false).Should().NotBeSameAs(learned, "its estimates are in pixels: a new binning starts over");
        Next(Predictive(), new AlgorithmSettings(GuideAlgorithmKind.Hysteresis)).Should().BeOfType<HysteresisAlgorithm>();

        var hysteresis = new HysteresisAlgorithm();
        Guider.NextAlgorithm(hysteresis, new AlgorithmSettings(GuideAlgorithmKind.Hysteresis), new AlgorithmSettings(GuideAlgorithmKind.Hysteresis),
            GuideAxis.Ra, 0.2, true, true).Should().BeSameAs(hysteresis);
        Guider.NextAlgorithm(hysteresis, new AlgorithmSettings(GuideAlgorithmKind.Hysteresis),
            new AlgorithmSettings(GuideAlgorithmKind.Hysteresis, new Dictionary<string, double> { ["aggression"] = 0.5 }), GuideAxis.Ra, 0.2, true, true)
            .Should().NotBeSameAs(hysteresis, "the PHD2 algorithms start over with new parameters, as before");
    }
}

/// <summary>
/// The periodic-error prediction in the closed loop, on a worm that behaves like a real one: 180 teeth, the
/// curve locked to the RA axis angle (it jumps with slews), as large as on a cheap mount.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PeriodicErrorGuidingTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    [Test]
    public void A_corrupted_stored_curve_is_discarded_and_never_restored()
    {
        var clock = new VirtualClock();
        var sim = new Simulator(Worm(), clock);
        var settings = new GuiderSettings { FocalLengthMm = sim.Scenario.Camera.FocalLengthMm, RaAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive) };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings) { AutoStartLoop = false };
        var events = new List<GuiderEvent>();
        guider.EventRaised += (_, e) => events.Add(e);

        var bad = new PeriodicErrorModel { Teeth = 180, PeriodSeconds = WormPeriod, Sin = [double.NaN, 0, 0], Cos = [0, 0, 0], Cycles = 5, LearnedAt = clock.UtcNow };
        guider.RestorePeriodicError(bad);

        events.OfType<PeriodicErrorModelDiscardedEvent>().Should().ContainSingle(e => ReferenceEquals(e.Model, bad), "the host deletes its stored copy");
        events.OfType<AlgorithmNoteEvent>().Should().ContainSingle(n => n.Message == "stored periodic error model discarded: not a number");
        ((PredictiveAlgorithm)guider.RaAlgorithm).PeriodicError!.IsSignificant.Should().BeFalse();
    }

    [Test]
    [Category("Slow")]
    public async Task Prediction_cuts_the_periodic_error_of_a_poor_worm()
    {
        // the period is detected after five worm turns (40 min)
        var off = await Guide(Worm(), periodicError: false, TimeSpan.FromMinutes(75));
        var on = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(75));
        double rmsOff = off.RaRms(45, 75), rmsOn = on.RaRms(45, 75);
        TestContext.Out.WriteLine($"RA true RMS 45–75 min: {rmsOff:F3}″ without, {rmsOn:F3}″ with periodic-error prediction ({on.PredictingFraction(45, 75):P0} of the frames)");
        rmsOn.Should().BeLessThan(0.6 * rmsOff);
        rmsOn.Should().BeLessThan(0.35);
        on.Models.Should().NotBeEmpty("a curve that helps is offered for storing");
        on.Models[^1].PeriodSeconds.Should().BeApproximately(WormPeriod, 0.01 * WormPeriod);
        on.Models[^1].AmplitudeArcsec.Should().BeApproximately(15, 3, "RA-axis arcsec, independent of the declination");
    }

    [TestCase(true)]
    [TestCase(false)]
    [Category("Slow")]
    public async Task The_detected_period_snaps_to_a_tooth_count_only_when_there_is_one(bool physicalWorm)
    {
        // PoorPeriodicError's 480 s is 179.5 teeth: no worm has that, and its estimates come within 0.3 of 180
        var scenario = physicalWorm ? Worm() : SimulatorScenario.PoorPeriodicError;
        var run = await Guide(scenario, periodicError: true, TimeSpan.FromMinutes(100));
        TestContext.Out.WriteLine($"{scenario.Name}: {run.Final}");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, run.Notes.Where(n => n.Contains("worm:") || n.Contains("detected"))));
        run.Models.Should().NotBeEmpty();
        run.Models[^1].Teeth.Should().Be(physicalWorm ? 180 : null);
    }

    [Test]
    [Category("Slow")]
    public async Task With_the_tooth_count_it_predicts_after_two_worm_turns()
    {
        // before the curve is predicted the filter bank takes the periodic error for wander; the fit must not. The curve
        // counts once two worm turns agree (the stability test)
        var run = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(28), teeth: 180);
        TestContext.Out.WriteLine($"RA true RMS 20–28 min: {run.RaRms(20, 28):F3}″, predicting {run.PredictingFraction(20, 28):P0}");
        run.PredictingFraction(20, 28).Should().BeGreaterThan(0.9);
        run.Models.Should().NotBeEmpty();
        run.Models[0].Teeth.Should().Be(180);
        run.RaRms(20, 28).Should().BeLessThan(0.35);
    }

    // between two targets: guiding stops, the mount flips or is synced by a plate solve, guiding starts again
    private static (TimeSpan, Action<Guider, Simulator>) Between(double atMinutes, Action<Simulator> change) => (TimeSpan.FromMinutes(atMinutes), (g, sim) =>
    {
        g.StopGuiding();
        change(sim);
        _ = g.StartGuidingAsync(Settle);
    });

    [TestCase(135, 135)]
    [TestCase(180, 180)]
    [TestCase(135, 0)]
    [Category("Slow")]
    public async Task A_meridian_flip_keeps_or_relearns_the_phase(int wormTeeth, int teethSetting)
    {
        var run = await Guide(Worm(teeth: wormTeeth), periodicError: true, TimeSpan.FromMinutes(teethSetting > 0 ? 45 : 90), teeth: teethSetting,
            midway: Between(teethSetting > 0 ? 25 : 65, sim => sim.Mount.MeridianFlip()));
        double from = teethSetting > 0 ? 28 : 68, to = teethSetting > 0 ? 45 : 90;
        TestContext.Out.WriteLine($"after the flip: RA true RMS {run.RaRms(from, to):F3}″, predicting {run.PredictingFraction(from, to):P0}");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, run.Notes));
        run.Notes.Should().Contain(n => n.Contains("meridian flip"));
        if (teethSetting > 0)
        {
            run.PredictingFraction(from, to).Should().BeGreaterThan(0.9, "a known tooth count keeps the phase");
            run.RaRms(from, to).Should().BeLessThan(0.35);
        }
        else
        {
            run.PredictingFraction(to - 10, to).Should().BeGreaterThan(0.8, "the phase is learned again");
        }
    }

    [TestCase(6.0)]
    [TestCase(30.0)]
    [Category("Slow")]
    public async Task A_plate_solve_sync_between_targets_is_caught_up(double syncArcmin)
    {
        // a sync moves the reported RA, not the axis: the curve's phase against the hour angle is off by teeth × offset
        var run = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(60), teeth: 180,
            midway: Between(25, sim => sim.Mount.Sync(syncArcmin / 60.0 / 15.0)));
        TestContext.Out.WriteLine($"sync {syncArcmin}′ ({180 * syncArcmin / 60 / 15 / 24 * 360:F0}° of worm phase): RA true RMS 28–35 min {run.RaRms(28, 35):F3}″, " +
            $"35–60 min {run.RaRms(35, 60):F3}″, predicting {run.PredictingFraction(28, 35):P0} / {run.PredictingFraction(35, 60):P0}");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, run.Notes));
        TestContext.Out.WriteLine($"at the end: {run.Final}");
        run.RaRms(35, 60).Should().BeLessThan(0.35);
    }

    [Test]
    [Category("Slow")]
    public async Task A_stored_curve_predicts_right_away_after_a_slew()
    {
        var learned = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(60));
        var model = learned.Models[^1];

        // another target: other hour angle and declination; one guider restores the curve, one learns from scratch
        var elsewhere = Worm(raHours: 2.0, siderealHours: 4.5, decDeg: 45);
        var fresh = await Guide(elsewhere, periodicError: true, TimeSpan.FromMinutes(15));
        var restored = await Guide(elsewhere, periodicError: true, TimeSpan.FromMinutes(15), model);
        TestContext.Out.WriteLine($"RA true RMS 3–15 min: {fresh.RaRms(3, 15):F3}″ learning, {restored.RaRms(3, 15):F3}″ restored " +
            $"(predicting {restored.PredictingFraction(3, 15):P0} of the frames)");
        restored.PredictingFraction(3, 15).Should().BeGreaterThan(0.8);
        restored.RaRms(3, 15).Should().BeLessThan(0.7 * fresh.RaRms(3, 15));
    }

    [Test]
    [Category("Slow")]
    public async Task A_wrong_stored_curve_is_not_applied()
    {
        var learned = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(60));
        var model = learned.Models[^1];
        var wrong = model with { Sin = model.Sin.Select(v => -v).ToList(), Cos = model.Cos.Select(v => -v).ToList() };
        var fresh = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(15));
        var misled = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(15), wrong);
        TestContext.Out.WriteLine($"RA true RMS 0–15 min: {fresh.RaRms(1, 15):F3}″ learning, {misled.RaRms(1, 15):F3}″ with a curve half a cycle off " +
            $"(predicting {misled.PredictingFraction(1, 15):P0})");
        misled.RaRms(1, 15).Should().BeLessThan(1.15 * fresh.RaRms(1, 15), "the gate keeps a curve that predicts worse out");
    }

    [Test]
    [Category("Slow")]
    public async Task A_stored_period_that_does_not_hold_is_discarded()
    {
        // a stored curve of a detected 700 s period (a wobble another night took for periodic error); this mount's worm
        // turns in 478.7 s: the 700 s curve fails the stability test, is forgotten, and the host is told to delete it
        var stale = new PeriodicErrorModel { PeriodSeconds = 700, Sin = [1.0, 0, 0], Cos = [0, 0, 0], Cycles = 8, LearnedAt = DateTimeOffset.UnixEpoch };
        var run = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(50), stale);
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, run.Notes));
        run.Discarded.Should().ContainSingle().Which.PeriodSeconds.Should().Be(700);
        run.Models.Should().NotContain(m => Math.Abs(m.PeriodSeconds - 700) < 10, "a curve that does not hold is not stored again");
    }

    [TestCase(710.0, true)]
    [TestCase(745.0, false)]
    [Category("Slow")]
    public async Task Only_a_stored_curve_of_the_discarded_period_is_deleted(double heldSeconds, bool deleted)
    {
        // the host stored a 700 s curve; the algorithm holds one of another period (as if refined since) that does not
        // hold on this mount: the stored curve goes with it only within 2 % of that period
        var stored = new PeriodicErrorModel { PeriodSeconds = 700, Sin = [1.0, 0, 0], Cos = [0, 0, 0], Cycles = 8, LearnedAt = DateTimeOffset.UnixEpoch };
        var run = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(50), stored,
            prepare: g => ((PredictiveAlgorithm)g.RaAlgorithm).RestorePeriodicError(stored with { PeriodSeconds = heldSeconds }));
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, run.Notes));
        run.Notes.Should().Contain(n => n.Contains(FormattableString.Invariant($"{heldSeconds:F1} s no longer stable")) || n.Contains(FormattableString.Invariant($"period {heldSeconds:F1} s replaced by")));
        if (deleted)
        {
            run.Discarded.Should().ContainSingle().Which.Should().Be(stored);
        }
        else
        {
            run.Discarded.Should().BeEmpty();
        }
    }

    [Test]
    [Category("Slow")]
    public async Task Without_mount_information_it_learns_from_the_time_and_stores_nothing()
    {
        var run = await Guide(Worm(), periodicError: true, TimeSpan.FromMinutes(70), mountInfo: false);
        TestContext.Out.WriteLine($"RA true RMS 50–70 min: {run.RaRms(50, 70):F3}″, predicting {run.PredictingFraction(50, 70):P0}");
        run.PredictingFraction(50, 70).Should().BeGreaterThan(0.8);
        run.Models.Should().BeEmpty("without the hour angle there is no worm position to store");
    }
}

/// <summary>
/// The measurement uncertainty in the closed loop: frames under thin clouds are trusted less. A faint field (magnitude 12–13.5), so the
/// guide star's centroid noise is about the seeing's and grows two to five times under the clouds; Predictive on both
/// axes with and without the frame weighting, on the same sky.
/// </summary>
[TestFixture]
[NonParallelizable]
public class MeasurementNoiseGuidingTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    /// <summary>How the sky is from minute 6 on.</summary>
    public enum Clouds
    {
        /// <summary>No clouds.</summary>
        None,

        /// <summary>Every 2.5 min 30–60 s at 20–50 % transmission.</summary>
        Short,

        /// <summary>Every 10 min 4 min at 35 % transmission.</summary>
        Long,
    }

    internal static SimulatorScenario FaintField(int seed, Clouds clouds = Clouds.Short)
    {
        var windows = new List<TransparencyWindow>();
        double[] transmission = [0.3, 0.5, 0.2, 0.4];
        double[] seconds = [40, 60, 30, 50];
        for (int k = 0; clouds == Clouds.Long ? 360 + 600 * k + 300 < 2400 : clouds == Clouds.Short && 360 + 150 * k + 60 < 2400; k++)
        {
            windows.Add(clouds == Clouds.Long
                ? new TransparencyWindow(360 + 600 * k, 600 + 600 * k, 0.35, 20)
                : new TransparencyWindow(360 + 150 * k, 360 + 150 * k + seconds[k % 4], transmission[k % 4], 8));
        }

        var good = SimulatorScenario.GoodMount;
        return good with
        {
            Name = $"Faint{clouds}Clouds#{seed}",
            Mount = good.Mount with { Seed = seed },
            Sky = good.Sky with
            {
                BrightestMagnitude = 12, FaintestMagnitude = 13.5, Transparency = windows, SeeingSeed = 7 + 101 * seed, StarFieldSeed = 42 + seed,
            },
            Camera = good.Camera with { NoiseSeed = 99 + 13 * seed },
        };
    }

    [Test]
    [Category("Slow")]
    public async Task Frames_under_thin_clouds_are_trusted_less()
    {
        var scenario = FaintField(seed: 1);
        var on = await Guide(scenario, frameWeighting: true, TimeSpan.FromMinutes(20));
        var off = await Guide(scenario, frameWeighting: false, TimeSpan.FromMinutes(20));

        // from minute 6 on: under the thicker clouds (≤ 30 %) and in clear sky
        static List<Frame> Thick(List<Frame> run) => run.Where(f => f.Seconds >= 360 && f.Transmission <= 0.3).ToList();
        static List<Frame> Clear(List<Frame> run) => run.Where(f => f.Seconds >= 360 && f.Transmission >= 1).ToList();
        double gainOn = Thick(on).Average(f => f.Gain), gainOff = Thick(off).Average(f => f.Gain);
        double seeingOn = on.Where(f => f.Seconds >= 360).Max(f => f.SeeingPx), seeingOff = off.Where(f => f.Seconds >= 360).Max(f => f.SeeingPx);
        double before = on.Last(f => f.Seconds < 360).SeeingPx;
        TestContext.Out.WriteLine(
            $"under clouds: variance × {Median(Thick(on).Select(f => f.Factor)):F1} (median), gain {gainOn:F2} with frame weighting, {gainOff:F2} without; " +
            $"clear: {Clear(on).Count(f => f.Factor == 1) / (double)Clear(on).Count:P0} usual frames; seeing σ {before:F3} px before the clouds, " +
            $"at most {seeingOn:F3} px with frame weighting, {seeingOff:F3} px without; true RMS {Rms(on, 360):F3} / {Rms(off, 360):F3} px, " +
            $"under clouds {Rms(on.Where(f => f.Transmission < 1).ToList(), 360):F3} / {Rms(off.Where(f => f.Transmission < 1).ToList(), 360):F3} px");

        Median(Thick(on).Select(f => f.Factor)).Should().BeGreaterThan(2);
        Clear(on).Count(f => f.Factor == 1).Should().BeGreaterThan(Clear(on).Count * 3 / 4);
        gainOn.Should().BeLessThan(0.9 * gainOff);
        seeingOn.Should().BeLessThan(1.2 * before, "the clouds don't count as seeing");
        seeingOff.Should().BeGreaterThan(seeingOn);
        off.Should().OnlyContain(f => f.Factor == 1);
    }

    internal sealed record Frame(double Seconds, double RaError, double DecError, double Transmission, double Factor, double Gain, double SeeingPx,
        double LogWander, int Takeovers);

    internal sealed record Summary(double Rms, double RmsDim, double RmsClear, double SeeingClear, double SeeingDim, double LogWanderClear,
        double LogWanderDim, double Takeovers, double Noisy, double ClearNotUsual)
    {
        public override string ToString() => FormattableString.Invariant(
            $"true RMS {Rms:F3} px (clouds {RmsDim:F3}, clear {RmsClear:F3}), seeing σ {SeeingClear:F3}/{SeeingDim:F3} px, log wander {LogWanderClear:F2}/{LogWanderDim:F2}, {Takeovers} takeovers");
    }

    internal static Summary Summarize(List<Frame> run)
    {
        var frames = run.Where(f => f.Seconds >= 360).ToList();
        var dim = frames.Where(f => f.Transmission < 1).ToList();
        var clear = frames.Where(f => f.Transmission >= 1).ToList();
        static double Mean(List<Frame> f, Func<Frame, double> get) => f.Count > 0 ? f.Average(get) : double.NaN;
        return new Summary(Rms(frames, 0), dim.Count > 0 ? Rms(dim, 0) : double.NaN, Rms(clear, 0), Mean(clear, f => f.SeeingPx), Mean(dim, f => f.SeeingPx),
            Mean(clear, f => f.LogWander), Mean(dim, f => f.LogWander), frames[^1].Takeovers - frames[0].Takeovers,
            frames.Count(f => f.Factor >= 2) / (double)frames.Count, clear.Count(f => f.Factor > 1) / (double)clear.Count);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    // true RMS (RA and Dec about their means) from fromSec on
    internal static double Rms(List<Frame> run, double fromSec)
    {
        var frames = run.Where(f => f.Seconds >= fromSec).ToList();
        double ra = frames.Average(f => f.RaError), dec = frames.Average(f => f.DecError);
        return Math.Sqrt(frames.Average(f => (f.RaError - ra) * (f.RaError - ra) + (f.DecError - dec) * (f.DecError - dec)));
    }

    internal static async Task<List<Frame>> Guide(SimulatorScenario scenario, bool frameWeighting, TimeSpan duration)
    {
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings
        {
            FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000,
            RaAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive), DecAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive),
        };
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        var ra = (PredictiveAlgorithm)guider.RaAlgorithm;
        var dec = (PredictiveAlgorithm)guider.DecAlgorithm;
        ra.FrameWeighting = dec.FrameWeighting = frameWeighting;
        var frames = new List<Frame>();
        guider.EventRaised += (_, e) =>
        {
            if (e is GuideStepEvent { IsSettling: false, IsRecenterMove: false } s)
            {
                double t = (s.Timestamp - clock.Epoch).TotalSeconds;
                var error = sim.Mount.GetPointingErrorBreakdown(t).Total;
                lock (frames)
                {
                    frames.Add(new Frame(t, error.Ra, error.Dec, sim.Sky.TransparencyAt(t - settings.ExposureMs / 2000.0), s.RaNoiseFactor ?? 1, ra.LastGain,
                        ra.SeeingPx, 0.5 * (Math.Log10(ra.State.WanderRatio) + Math.Log10(dec.State.WanderRatio)), ra.State.Takeovers + dec.State.Takeovers));
                }
            }
        };
        _ = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);
        guider.RaAlgorithm.Should().BeSameAs(ra, "the algorithm was not rebuilt");
        return frames;
    }
}

/// <summary>Every closed-loop test again with the Predictive algorithm on both axes.</summary>
[TestFixture]
[NonParallelizable]
public class PredictiveClosedLoopTests : ClosedLoopTests
{
    [SetUp]
    public void UsePredictive() =>
        AlgorithmOverride = s => s with { RaAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive), DecAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive) };

    [TearDown]
    public void UseClassic() => AlgorithmOverride = null;
}
