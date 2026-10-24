// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>
/// Dec guide mode Drift in the closed-loop simulator: one Dec direction along the measured drift against PHD2's
/// Auto (both directions), judged on the simulator's true pointing error and the number of Dec reversals.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DecDriftGuidingTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    internal static readonly AlgorithmSettings ResistSwitch = new(GuideAlgorithmKind.ResistSwitch);
    internal static readonly AlgorithmSettings Predictive = new(GuideAlgorithmKind.Predictive);

    /// <summary>Dec backlash (12″) and a polar-alignment drift of 1″/min: the case one-direction guiding is made for.</summary>
    internal static SimulatorScenario BacklashDrift(int seed = 1) => Seeded(SimulatorScenario.Backlash, seed);

    /// <summary>The same mount with the drift slowing down and reversing after <paramref name="reverseMin"/> minutes.</summary>
    internal static SimulatorScenario ReversingDrift(double reverseMin, int seed = 1)
    {
        var s = Seeded(SimulatorScenario.Backlash, seed);
        return s with
        {
            Name = "ReversingDrift",
            Mount = s.Mount with { DecDriftArcsecPerMin = 1.0, DecDriftChangePerHour = -1.0 * 60 / reverseMin },
        };
    }

    /// <summary>The backlash mount with another Dec drift, e.g. at a new target where it goes the other way (−1″/min).</summary>
    internal static SimulatorScenario BacklashWithDrift(double arcsecPerMin, int seed = 1)
    {
        var s = Seeded(SimulatorScenario.Backlash, seed);
        return s with { Mount = s.Mount with { DecDriftArcsecPerMin = arcsecPerMin } };
    }

    /// <summary>A good mount without any Dec drift (the random walk of the mount stays).</summary>
    internal static SimulatorScenario NoDrift(int seed = 1)
    {
        var s = Seeded(SimulatorScenario.GoodMount, seed);
        return s with { Name = "NoDrift", Mount = s.Mount with { DecDriftArcsecPerMin = 0 } };
    }

    /// <summary>A good mount without backlash and a Dec drift of 1″/min.</summary>
    internal static SimulatorScenario DriftWithoutBacklash(int seed = 1)
    {
        var s = Seeded(SimulatorScenario.GoodMount, seed);
        return s with { Name = "DriftWithoutBacklash", Mount = s.Mount with { DecDriftArcsecPerMin = 1.0 } };
    }

    /// <summary>
    /// Like the mount of the guide logs in Data/dec-drift-*.csv: a short guide scope (3.1″/px) at Dec 57, a Dec drift of
    /// 1″/min, about 200 ms (1.5″, 0.5 px) of Dec backlash and a mount that wanders more than the good one.
    /// </summary>
    internal static SimulatorScenario ShortScope(int seed = 1)
    {
        var s = Seeded(SimulatorScenario.GoodMount, seed);
        return s with
        {
            Name = "ShortScope",
            Mount = s.Mount with { DeclinationDeg = 56.8, DecDriftArcsecPerMin = 1.0, DecBacklashArcsec = 1.5, RandomWalkArcsecPerSqrtSec = 0.05 },
            Camera = s.Camera with { FocalLengthMm = 249, PixelSizeUm = 3.75, SensorWidth = 1280, SensorHeight = 960 },
            Sky = s.Sky with { FwhmArcsec = 6.0 },
        };
    }

    internal static SimulatorScenario Seeded(SimulatorScenario s, int seed) => seed == 1 ? s : s with
    {
        Name = $"{s.Name}#{seed}",
        Mount = s.Mount with { Seed = seed },
        Sky = s.Sky with { SeeingSeed = 7 + 100 * seed },
        Camera = s.Camera with { NoiseSeed = 99 + 100 * seed },
    };

    /// <summary>
    /// The cases of the mode comparison (<see cref="Benchmarks.DecDriftBenchmarks.Compare"/>): the scenario, how long it guides and after how many
    /// minutes of guiding the mount flips (null: no flip).
    /// </summary>
    internal static (SimulatorScenario Scenario, int Minutes, int? FlipMin) Case(string name, int seed) => name switch
    {
        "Backlash" => (BacklashDrift(seed), 30, null),
        "NewTarget" => (BacklashWithDrift(-1.0, seed), 30, null),
        "Reversing" => (ReversingDrift(30, seed), 60, null),
        "Flip" => (BacklashDrift(seed), 45, 20),
        "WeakDrift" => (BacklashWithDrift(0.3, seed), 30, null),
        "GoodMount" => (DriftWithoutBacklash(seed), 30, null),
        "NoDrift" => (NoDrift(seed), 30, null),
        "ShortScope" => (ShortScope(seed), 30, null),
        _ => throw new ArgumentException($"unknown case {name}", nameof(name)),
    };

    internal static readonly string[] Cases = ["Backlash", "NewTarget", "Reversing", "Flip", "WeakDrift", "GoodMount", "NoDrift", "ShortScope"];

    [Test]
    [Category("Slow")]
    public async Task Predictive_on_a_backlash_mount_stops_reversing_and_guides_as_well()
    {
        var auto = await Guide(BacklashDrift(), DecGuideMode.Auto, Predictive, TimeSpan.FromMinutes(30));
        DecDirectionState? state = null;
        var drift = await Guide(BacklashDrift(), DecGuideMode.Drift, Predictive, TimeSpan.FromMinutes(30), onEvent: (g, _) => state = g.DecDirection);
        TestContext.Out.WriteLine($"Auto  {auto}");
        TestContext.Out.WriteLine($"Drift {drift}, directions {drift.Timeline}");
        drift.DecRms.Should().BeLessThan(auto.DecRms * 1.1, "Predictive copes with the backlash in Auto too; Drift spares the gears");
        drift.Reversals.Should().BeLessThan(auto.Reversals / 10);
        drift.FirstPickMin.Should().BeLessThan(4, "a drift of 1″/min holds after the first 2 minutes and is picked a minute later");
        drift.Timeline.Should().StartWith("South@", "the mount drifts North: South pulses counter it");
        state!.Direction.Should().Be(DecGuideDirection.South);
        state.DriftPxPerSec.Should().BeGreaterThan(0);
        drift.Calibration!.DecStepCount.Should().BeGreaterThan(0, "calibration moves Dec both ways in any mode");
    }

    [Test]
    [Category("Slow")]
    public async Task ResistSwitch_on_a_backlash_mount_is_not_worse()
    {
        var auto = await Guide(BacklashDrift(), DecGuideMode.Auto, ResistSwitch, TimeSpan.FromMinutes(30));
        var drift = await Guide(BacklashDrift(), DecGuideMode.Drift, ResistSwitch, TimeSpan.FromMinutes(30));
        TestContext.Out.WriteLine($"Auto  {auto}");
        TestContext.Out.WriteLine($"Drift {drift}, directions {drift.Timeline}");
        drift.DecRms.Should().BeLessThan(auto.DecRms * 1.05);
        drift.Reversals.Should().BeLessThanOrEqualTo(auto.Reversals);
        drift.Timeline.Should().StartWith("South@");
    }

    [TestCase(GuideAlgorithmKind.ResistSwitch)]
    [TestCase(GuideAlgorithmKind.Predictive)]
    [Category("Slow")]
    public async Task Without_a_drift_it_guides_like_auto(GuideAlgorithmKind kind)
    {
        var dec = new AlgorithmSettings(kind);
        var auto = await Guide(NoDrift(), DecGuideMode.Auto, dec, TimeSpan.FromMinutes(30));
        var drift = await Guide(NoDrift(), DecGuideMode.Drift, dec, TimeSpan.FromMinutes(30));
        TestContext.Out.WriteLine($"Auto  {auto}");
        TestContext.Out.WriteLine($"Drift {drift}, directions {drift.Timeline}");
        drift.DecRms.Should().BeLessThan(auto.DecRms * 1.1);
    }

    [TestCase(GuideAlgorithmKind.ResistSwitch)]
    [TestCase(GuideAlgorithmKind.Predictive)]
    [Category("Slow")]
    public async Task A_reversing_drift_gives_up_the_direction_and_never_guides_against_it(GuideAlgorithmKind kind)
    {
        // +1″/min falling to −1″/min over the hour (through 0 after 30 minutes), on the 12″ backlash mount
        var dec = new AlgorithmSettings(kind);
        var auto = await Guide(ReversingDrift(30), DecGuideMode.Auto, dec, TimeSpan.FromMinutes(60));
        var drift = await Guide(ReversingDrift(30), DecGuideMode.Drift, dec, TimeSpan.FromMinutes(60));
        TestContext.Out.WriteLine($"Auto  {auto}");
        TestContext.Out.WriteLine($"Drift {drift}, directions {drift.Timeline}");

        // South, both directions once the drift fades, and North only once the reversed drift holds whatever the dead
        // band (Predictive reverses too often for the dead band to be learned within the hour: both directions to the end)
        var directions = drift.Notes.Where(n => n.Kind == DecDirectionNoteKind.Switch).Select(n => n.Direction).ToList();
        directions.Take(2).Should().Equal(DecGuideDirection.South, DecGuideDirection.Both);
        directions.Skip(2).Should().NotContain(DecGuideDirection.South).And.HaveCountLessThanOrEqualTo(2);
        drift.DecRms.Should().BeLessThan(auto.DecRms * 1.3);
    }

    [Test]
    public async Task Predictive_learns_only_the_dec_pulses_that_went_out()
    {
        // South only against the drift: Predictive keeps asking for North pulses that the direction suppresses. Its own
        // drift estimate shows what it learned; had it counted the suppressed pulses as sent, it would read them as a
        // much faster North drift
        var drifts = new List<double>();
        var seeing = new List<double>();
        int suppressed = 0;
        int north = 0;
        await Guide(BacklashDrift(), DecGuideMode.Drift, Predictive, TimeSpan.FromMinutes(20), onEvent: (g, e) =>
        {
            if (e is GuideStepEvent { IsSettling: false } s && s.Time > 480 && g.DriftDecDirectionNow == GuideDirection.South)
            {
                suppressed += s.DecDistanceGuide < 0 ? 1 : 0;
                north += s.DecDirection == GuideDirection.North ? 1 : 0;
                var state = ((PredictiveAlgorithm)g.DecAlgorithm).State;
                drifts.Add(state.DriftPxPerSec * s.PixelScale * 60);
                seeing.Add(state.SeeingPx);
            }
        });

        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{drifts.Count} frames South only, {suppressed} North requests suppressed, Predictive's drift {drifts.Average():F2}″/min (true 1.00), seeing {seeing.Average():F3} px"));
        north.Should().Be(0);
        suppressed.Should().BeGreaterThan(drifts.Count / 5);
        drifts.Average().Should().BeApproximately(1.0, 0.2);
    }

    [Test]
    public async Task Settling_after_a_dither_guides_both_ways_and_keeps_the_drift()
    {
        var settling = new List<GuideDirection?>();
        var afterwards = new List<GuideDirection?>();
        int switchesAtDither = -1;
        Task<SettleResult>? dither = null;
        var run = await Guide(BacklashDrift(), DecGuideMode.Drift, ResistSwitch, TimeSpan.FromMinutes(12), onEvent: (g, e) =>
        {
            if (e is not GuideStepEvent step)
            {
                return;
            }

            if (dither is null && step.Time > 360 && !step.IsSettling)
            {
                switchesAtDither = g.DecDirection!.Switches;
                dither = g.DitherAsync(5, false, Settle);
            }
            else if (dither is not null)
            {
                (step.IsSettling ? settling : afterwards).Add(g.DriftDecDirectionNow);
            }
        });

        (await dither!).Success.Should().BeTrue();
        settling.Should().NotBeEmpty().And.OnlyContain(d => d == null, "both directions while settling");
        afterwards.Should().Contain(GuideDirection.South);
        run.Notes.Count(n => n.Kind == DecDirectionNoteKind.Switch).Should().Be(switchesAtDither, "a dither keeps the direction and the drift");
        run.Notes.Should().NotContain(n => n.Kind == DecDirectionNoteKind.Reset && n.Timestamp > run.Steps[0].Timestamp.AddMinutes(1));
    }

    [Test]
    public async Task Choosing_the_drift_mode_while_guiding_uses_the_drift_measured_so_far()
    {
        DecDirectionState? before = null;
        double? changedAt = null;
        var run = await Guide(BacklashDrift(), DecGuideMode.Auto, ResistSwitch, TimeSpan.FromMinutes(10), onEvent: (g, e) =>
        {
            if (e is GuideStepEvent { IsSettling: false } step && changedAt is null && step.Time > 300)
            {
                before = g.DecDirection;
                changedAt = step.Time;
                g.UpdateSettings(g.Settings with { DecGuideMode = DecGuideMode.Drift });
            }
        });

        before.Should().BeNull("no Dec direction in Auto");
        var pick = run.Notes.Should().Contain(n => n.Kind == DecDirectionNoteKind.Switch).Subject;
        double pickedAt = (pick.Timestamp - run.Steps[0].Timestamp).TotalSeconds;
        TestContext.Out.WriteLine($"Drift chosen at {changedAt:F0} s, {pick.Direction} at {pickedAt:F0} s: {pick.Message}");
        pickedAt.Should().BeLessThan(changedAt!.Value + 10, "the drift was measured and held in Auto already: picked on the next frame");
        pick.Direction.Should().Be(DecGuideDirection.South);
    }

    [Test]
    [Category("Slow")]
    public async Task Backlash_compensation_acts_only_while_both_directions_are_guided()
    {
        // compensation for the 12″ (1.6 s) dead band: while South is guided, a suppressed North request once made the next
        // South pulse carry it, which moved the mount far more than the step reported (3.67″ Dec RMS against 0.12″). It
        // still acts while both directions are guided, which costs a little in the first minutes (0.21″ against 0.11″ here).
        var compensated = await Guide(BacklashDrift(), DecGuideMode.Drift, Predictive, TimeSpan.FromMinutes(12),
            configure: s => s with { Backlash = new BacklashSettings { Enabled = true, PulseMs = 1500 } });
        var plain = await Guide(BacklashDrift(), DecGuideMode.Drift, Predictive, TimeSpan.FromMinutes(12));
        TestContext.Out.WriteLine($"with compensation {compensated}, directions {compensated.Timeline}");
        TestContext.Out.WriteLine($"without           {plain}, directions {plain.Timeline}");
        compensated.Timeline.Should().StartWith("South@");
        compensated.DecRms.Should().BeLessThan(2 * plain.DecRms);
    }

    [Test]
    public async Task A_lost_star_keeps_the_drift_while_a_flip_and_another_binning_start_over()
    {
        // one run in Drift: once South is guided, clouds hide the star for 20 s (the drift and the direction stay, the
        // open-loop position continues in a new segment); then a meridian flip while paused and another binning, each of
        // which starts over with both directions
        Simulator? sim = null;
        int phase = 0;
        double cloudEnd = 0;
        int lost = 0;
        DriftEstimate? before = null, after = null;
        DecDirectionState? beforeState = null, afterState = null, flipped = null;
        var run = await Guide(BacklashDrift(), DecGuideMode.Drift, ResistSwitch, TimeSpan.FromMinutes(11), attach: (_, _, s) => sim = s, onEvent: (g, e) =>
        {
            switch (phase, e)
            {
                case (0, GuideStepEvent { IsSettling: false } s) when s.Time > 240 && g.DecDirection?.Direction == DecGuideDirection.South:
                    before = g.OpenLoopDecDrift();
                    beforeState = g.DecDirection;
                    sim!.Sky.AddTransparencyWindow(new TransparencyWindow(sim.Now + 1, sim.Now + 21, 0.0));
                    cloudEnd = s.Time + 21;
                    phase = 1;
                    break;
                case (1, StarLostEvent):
                    lost++;
                    break;
                case (1, GuideStepEvent { IsSettling: false } s) when s.Time > cloudEnd + 60:
                    after = g.OpenLoopDecDrift();
                    afterState = g.DecDirection;
                    g.Pause();
                    phase = 2;
                    break;
                case (2, PausedEvent):
                    sim!.Mount.MeridianFlip();
                    g.Resume();
                    phase = 3;
                    break;
                case (3, ResumedEvent):
                    flipped = g.DecDirection;
                    g.UpdateSettings(g.Settings with { Binning = 2 });
                    phase = 4;
                    break;
            }
        });

        var resets = run.Notes.Where(n => n.Kind == DecDirectionNoteKind.Reset).Select(n => n.Message).ToList();
        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"drift {before!.Value.PxPerSec * 60:F3} ± {before.Value.SigmaPxPerSec * 60:F3} px/min before the clouds ({lost} frames lost), {after!.Value.PxPerSec * 60:F3} ± {after.Value.SigmaPxPerSec * 60:F3} after; resets: {string.Join(" | ", resets)}"));
        phase.Should().Be(4);
        lost.Should().BeGreaterThan(3);
        after.Value.PxPerSec.Should().BeApproximately(before.Value.PxPerSec, 3 * Math.Max(before.Value.SigmaPxPerSec, after.Value.SigmaPxPerSec));
        afterState!.Direction.Should().Be(DecGuideDirection.South);
        afterState.Switches.Should().Be(beforeState!.Switches, "a lost star is no reason to change the direction");

        flipped!.Direction.Should().Be(DecGuideDirection.Both);
        flipped.Switches.Should().Be(0);
        resets.Should().ContainSingle(m => m.StartsWith("meridian flip:", StringComparison.Ordinal));
        resets.Should().ContainSingle(m => m.StartsWith("binning changed:", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_guide_log_notes_the_mode_and_every_direction_change()
    {
        var sw = new StringWriter();
        GuideEngine.Logging.GuidingLog? log = null;
        GuideEngine.Logging.GuideLogBridge? bridge = null;
        await Guide(BacklashDrift(), DecGuideMode.Drift, ResistSwitch, TimeSpan.FromMinutes(6), fromSec: 0, attach: (g, clock, sim) =>
        {
            log = new GuideEngine.Logging.GuidingLog(sw, clock);
            log.EnableLogging();
            bridge = new GuideEngine.Logging.GuideLogBridge(g, log, sim.Mount, () => new GuideEngine.Logging.GuideLogContext());
        });
        bridge!.Dispose();
        log!.Close();

        string text = sw.ToString();
        text.Should().Contain("DEC guide mode = Drift");
        text.Should().Contain("INFO: Dec guide direction: guiding South only: Dec drift");
        text.Should().NotContain("Dec guide direction: guiding started", "resets go to the debug log only");
    }

    /// <summary>Result of one guided run: the true Dec RMS after <c>fromSec</c> and what the Dec direction did.</summary>
    internal sealed record DecRun(double DecRms, double RaRms, int Reversals, int DecPulses, int Switches, int ValveOpenings,
        double? FirstPickMin, IReadOnlyList<DecDirectionNoteEvent> Notes, IReadOnlyList<GuideStepEvent> Steps)
    {
        public GuideEngine.Calibration.CalibrationData? Calibration { get; init; }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"Dec RMS {DecRms:F3}″, RA {RaRms:F3}″, {Reversals} reversals / {DecPulses} Dec pulses, {Switches} switches, {ValveOpenings} valve, first pick {(FirstPickMin is { } m ? $"{m:F1} min" : "-")}");

        /// <summary>The direction changes with their minute, e.g. "South@2.2 Both@21.0 North@45.3".</summary>
        public string Timeline => Notes.Count(n => n.Kind == DecDirectionNoteKind.Switch) == 0 ? "Both"
            : string.Join(" ", Notes.Where(n => n.Kind == DecDirectionNoteKind.Switch)
                .Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Direction}@{(n.Timestamp - Steps[0].Timestamp).TotalMinutes:F1}")));
    }

    /// <summary>
    /// Guides <paramref name="scenario"/> for <paramref name="duration"/> (calibration included); with
    /// <paramref name="flipAfter"/> guiding stops after that much guide time, the mount flips and guiding starts again.
    /// </summary>
    internal static async Task<DecRun> Guide(SimulatorScenario scenario, DecGuideMode mode, AlgorithmSettings dec, TimeSpan duration, double fromSec = 300,
        Func<GuiderSettings, GuiderSettings>? configure = null, Action<Guider, GuiderEvent>? onEvent = null,
        Action<Guider, VirtualClock, Simulator>? attach = null, TimeSpan? flipAfter = null)
    {
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, DecAlgorithm = dec, DecGuideMode = mode };
        settings = configure?.Invoke(settings) ?? settings;
        var guider = new Guider(sim.Camera, sim.Mount, sim.Mount, clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        attach?.Invoke(guider, clock, sim);
        var steps = new List<GuideStepEvent>();
        var notes = new List<DecDirectionNoteEvent>();
        GuideEngine.Calibration.CalibrationData? calibration = null;
        var gate = new object();
        bool flipped = false;
        guider.EventRaised += (_, e) =>
        {
            lock (gate)
            {
                if (e is GuideStepEvent s)
                {
                    steps.Add(s);
                    if (flipAfter is { } after && !flipped && !s.IsSettling && s.Time >= after.TotalSeconds)
                    {
                        flipped = true;
                        guider.StopGuiding();
                        sim.Mount.MeridianFlip();
                        _ = guider.StartGuidingAsync(Settle);
                    }
                }
                else if (e is DecDirectionNoteEvent n)
                {
                    notes.Add(n);
                }
                else if (e is CalibrationCompleteEvent c)
                {
                    calibration = c.Calibration;
                }
            }

            onEvent?.Invoke(guider, e);
        };

        var guide = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);
        (await guide).Success.Should().BeTrue();

        double Seconds(DateTimeOffset t) => (t - clock.Epoch).TotalSeconds;
        var measured = steps.Where(s => !s.IsSettling && !s.IsRecenterMove && s.Time >= fromSec).ToList();
        measured.Should().NotBeEmpty();
        var truth = measured.Select(s => sim.Mount.GetPointingErrorBreakdown(Seconds(s.Timestamp)).Total).ToList();
        static double Rms(double[] a)
        {
            double m = a.Average();
            return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Length);
        }

        // every Dec pulse against the previous one takes up the backlash
        var decPulses = steps.Where(s => s.Time >= fromSec && s.DecDirection is not null).Select(s => s.DecDirection!.Value).ToList();
        int reversals = decPulses.Zip(decPulses.Skip(1)).Count(p => p.First != p.Second);
        var switches = notes.Where(n => n.Kind == DecDirectionNoteKind.Switch).ToList();
        var state = guider.DecDirection;
        double? firstPick = switches.Count > 0 ? (switches[0].Timestamp - steps[0].Timestamp).TotalMinutes : null;
        return new DecRun(Rms([.. truth.Select(t => t.Dec)]), Rms([.. truth.Select(t => t.Ra)]), reversals, decPulses.Count, switches.Count,
            state?.ValveOpenings ?? 0, firstPick, notes, steps) { Calibration = calibration };
    }
}
