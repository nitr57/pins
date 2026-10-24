// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// The periodic-error estimator on a synthetic open-loop RA motion whose periodic error is known exactly: a worm curve
/// (fundamental + harmonics, RA-axis arcsec) against the RA axis angle, seen on the guide camera × cos dec, plus drift,
/// wander and seeing.
/// </summary>
[TestFixture]
public class PeriodicErrorTests
{
    private const double SecondsPerSiderealHour = Sidereal.HourSeconds;

    [Test]
    public void Fits_the_curve_with_a_known_tooth_count()
    {
        var worm = new Worm(Teeth: 180);
        var pe = new PeriodicErrorEstimator(180);
        var sky = new Sky(worm);
        sky.Run(pe, minutes: 5);
        pe.IsSignificant.Should().BeFalse("less than one cycle of data");

        sky.Run(pe, minutes: 7);
        pe.IsSignificant.Should().BeFalse("the stability test needs two cycles");

        sky.Run(pe, minutes: 8);
        pe.IsSignificant.Should().BeTrue();
        pe.IsStable.Should().BeTrue();
        pe.State(PeriodicErrorPhase.Ready, 0, null).Stable.Should().BeTrue();
        pe.Teeth.Should().Be(180);
        pe.PeriodSeconds.Should().BeApproximately(478.689, 0.01);
        pe.Amplitude.Should().BeApproximately(worm.Fundamental, 0.1 * worm.Fundamental);
        double error = sky.PredictionRms(pe);
        TestContext.Out.WriteLine($"{pe.Describe()}; prediction error {error:F3} px vs curve {sky.CurveRms():F3} px");
        error.Should().BeLessThan(0.15 * sky.CurveRms());
    }

    [Test]
    public void Detects_the_period_and_snaps_to_the_tooth_count()
    {
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator();
        sky.Run(pe, minutes: 35);
        pe.PeriodSeconds.Should().BeNull("less than five cycles");

        sky.Run(pe, minutes: 10);
        pe.PeriodSeconds.Should().NotBeNull("five cycles after 40 minutes");
        pe.PeriodSeconds!.Value.Should().BeApproximately(478.7, 0.01 * 478.7);

        sky.Run(pe, minutes: 95);
        TestContext.Out.WriteLine(string.Join(" | ", pe.TakeNotes()));
        pe.Teeth.Should().Be(180);
        pe.IsSignificant.Should().BeTrue();
    }

    [Test]
    public void Never_snaps_a_period_that_is_no_whole_tooth_count()
    {
        // 480 s = 179.5 teeth, as in the older simulator scenarios: no worm has that
        var sky = new Sky(new Worm(Teeth: 0, PeriodSeconds: 480));
        var pe = new PeriodicErrorEstimator();
        sky.Run(pe, minutes: 120);
        pe.PeriodSeconds!.Value.Should().BeApproximately(480, 5);
        pe.Teeth.Should().BeNull();
    }

    [Test]
    public void A_stored_curve_predicts_at_once_at_another_hour_angle()
    {
        var worm = new Worm(Teeth: 144);
        var learn = new PeriodicErrorEstimator(144);
        new Sky(worm).Run(learn, minutes: 30);
        var model = learn.ModelForStorage(DateTimeOffset.UnixEpoch);
        model.Should().NotBeNull();
        model!.Teeth.Should().Be(144);

        // another night, another target: a different hour angle and declination, no data yet
        var restored = new PeriodicErrorEstimator(144);
        restored.Restore(model).Should().BeTrue();
        var elsewhere = new Sky(worm) { StartHours = 2.3, DeclinationDeg = 55 };
        double error = elsewhere.PredictionRms(restored);
        TestContext.Out.WriteLine($"restored: prediction error {error:F3} px vs curve {elsewhere.CurveRms():F3} px");
        error.Should().BeLessThan(0.2 * elsewhere.CurveRms());

        new PeriodicErrorEstimator(135).Restore(model).Should().BeFalse("a different tooth count is another mount");
    }

    [Test]
    public void A_flip_keeps_the_phase_with_a_known_odd_tooth_count()
    {
        // 135 teeth: a flip turns the axis by 180°, which is 67.5 worm turns, half a cycle
        var worm = new Worm(Teeth: 135);
        var pe = new PeriodicErrorEstimator(135);
        var sky = new Sky(worm);
        sky.Run(pe, minutes: 25);
        sky.Side = PierSide.West;
        pe.StartSegment();
        double error = sky.PredictionRms(pe);
        TestContext.Out.WriteLine($"after the flip: prediction error {error:F3} px vs curve {sky.CurveRms():F3} px");
        error.Should().BeLessThan(0.2 * sky.CurveRms());
        pe.IsSignificant.Should().BeTrue();
    }

    [Test]
    public void A_flip_without_a_tooth_count_learns_the_phase_anew()
    {
        var sky = new Sky(new Worm(Teeth: 135));
        var pe = new PeriodicErrorEstimator();
        sky.Run(pe, minutes: 60);
        pe.IsSignificant.Should().BeTrue();
        pe.Teeth.Should().BeNull("not precise enough after 60 minutes");

        sky.Side = PierSide.West;
        pe.StartSegment();
        sky.Run(pe, minutes: 1);
        pe.IsSignificant.Should().BeFalse("the phase is re-learned after the flip");
        pe.TakeNotes().Should().Contain(n => n.Contains("meridian flip"));
        sky.Run(pe, minutes: 15);
        pe.IsSignificant.Should().BeTrue();
    }

    [Test]
    public void Without_mount_information_the_phase_is_time_based_and_nothing_is_stored()
    {
        var sky = new Sky(new Worm(Teeth: 180)) { WithAxis = false };
        var pe = new PeriodicErrorEstimator(180);
        sky.Run(pe, minutes: 20);
        pe.IsSignificant.Should().BeTrue();
        pe.ModelForStorage(DateTimeOffset.UnixEpoch).Should().BeNull("without the hour angle the curve has no worm position");
        sky.PredictionRms(pe).Should().BeLessThan(0.2 * sky.CurveRms());
    }

    [Test]
    public void A_new_tooth_count_forgets_the_curve()
    {
        var pe = new PeriodicErrorEstimator(180);
        new Sky(new Worm(Teeth: 180)).Run(pe, minutes: 20);
        pe.IsSignificant.Should().BeTrue();

        pe.SetTeeth(180);
        pe.IsSignificant.Should().BeTrue("the same tooth count keeps it");
        pe.SetTeeth(144);
        pe.IsSignificant.Should().BeFalse();
        pe.PeriodSeconds.Should().BeApproximately(Sidereal.DaySeconds / 144, 1e-9);
        pe.SetTeeth(0);
        pe.PeriodSeconds.Should().BeNull("0 = detect");
    }

    [Test]
    public void The_store_keeps_one_curve_per_profile_and_mount()
    {
        var learn = new PeriodicErrorEstimator(180);
        new Sky(new Worm(Teeth: 180)).Run(learn, minutes: 20);
        var model = learn.ModelForStorage(new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero))!;
        var store = new PeriodicErrorStore();
        store.Set(new PeriodicErrorKey("profile-1", "EQ6-R"), model);
        store.Set(new PeriodicErrorKey("profile-1", "AM5"), model with { Teeth = 288, PeriodSeconds = 299.2 });

        var loaded = PeriodicErrorStore.FromJson(store.ToJson());
        loaded.Count.Should().Be(2);
        var back = loaded.Get(new PeriodicErrorKey("profile-1", "EQ6-R"))!;
        back.Should().BeEquivalentTo(model);
        new PeriodicErrorEstimator(180).Restore(back).Should().BeTrue();
        loaded.Get(new PeriodicErrorKey("profile-2", "EQ6-R")).Should().BeNull();

        loaded.Remove(new PeriodicErrorKey("profile-1", "AM5")).Should().BeTrue();
        PeriodicErrorStore.FromJson(loaded.ToJson()).Count.Should().Be(1);
        PeriodicErrorStore.FromJson("""{"version":1,"entries":[{"key":{"profileId":"p","mountName":"m"},"model":{"periodSeconds":0}}]}""")
            .Count.Should().Be(0, "an unusable entry is dropped");
    }

    [Test]
    public void A_restored_or_forgotten_curve_shows_in_the_state_at_once()
    {
        var ra = new PredictiveAlgorithm(periodicError: true);
        ra.State.PeriodicError!.PeriodSeconds.Should().BeNull();
        var model = new PeriodicErrorModel { Teeth = 180, PeriodSeconds = 478.69, Sin = [0.7, 0.2, 0.05], Cos = [0.3, 0.1, 0.0], Cycles = 25, LearnedAt = DateTimeOffset.UnixEpoch };
        ra.RestorePeriodicError(model).Should().BeTrue();
        ra.State.PeriodicError!.PeriodSeconds.Should().BeApproximately(Sidereal.DaySeconds / 180, 1e-9, "no frame needed");
        ra.State.PeriodicError.Teeth.Should().Be(180);
        ra.ForgetPeriodicError();
        ra.State.PeriodicError!.PeriodSeconds.Should().BeNull();
    }

    [Test]
    public void A_corrupted_stored_curve_is_discarded()
    {
        var good = new PeriodicErrorModel { Teeth = 180, PeriodSeconds = 478.69, Sin = [0.7, 0.2, 0.05], Cos = [0.3, 0.1, 0.0], Cycles = 25, LearnedAt = DateTimeOffset.UnixEpoch };
        PeriodicErrorEstimator.Invalid(good).Should().BeNull();
        PeriodicErrorModel[] bad =
        [
            good with { Sin = [double.NaN, 0.2, 0.05] },
            good with { Cos = [0.3, double.PositiveInfinity, 0.0] },
            good with { Cycles = double.NaN },
            good with { Teeth = 0 },
            good with { Teeth = PeriodicErrorEstimator.MaxTeeth + 1 },
            good with { Teeth = null, PeriodSeconds = 60 },
            good with { Teeth = null, PeriodSeconds = double.NaN },
            good with { Sin = [PeriodicErrorEstimator.MaxStoredAmplitudeArcsec + 1, 0.2, 0.05] },
            good with { Sin = [0.7, 0.2] },
        ];
        foreach (var model in bad)
        {
            var pe = new PeriodicErrorEstimator();
            pe.Restore(model).Should().BeFalse();
            pe.TakeNotes().Should().ContainSingle(n => n.StartsWith("stored periodic error model discarded: ", StringComparison.Ordinal));
            pe.IsSignificant.Should().BeFalse();
        }

        // the JSON store keeps named literals such as NaN; the estimator refuses them
        var store = new PeriodicErrorStore();
        store.Set(new PeriodicErrorKey("p", "m"), bad[0]);
        var loaded = PeriodicErrorStore.FromJson(store.ToJson()).Get(new PeriodicErrorKey("p", "m"))!;
        new PeriodicErrorEstimator(180).Restore(loaded).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void The_users_night_takes_no_wobble_for_a_worm(bool hourAngle)
    {
        // a real night (EQMod, 180-tooth worm of 478.7 s, 3.11″/px, Dec 56.8, 2 s frames): the worm is there but weak
        // (0.4-0.9″), stronger is an irregular ~12 min wobble (0.5-2″) over a red wander of ±4 px; the old detection
        // took it for a 769 s worm (≈112 teeth) after an hour and kept it all night
        var night = Night.Load(Night.Evening);
        var pe = new PeriodicErrorEstimator();
        var accepted = new List<double>();
        foreach (var (time, px) in night.Frames)
        {
            var context = night.Context(time, hourAngle);
            pe.Observe(time, px, context, true, night.SeeingPx2, 0.01 * night.SeeingPx2, 1e-10 * night.SeeingPx2);
            if (pe.PeriodSeconds is { } p)
            {
                accepted.Add(p);
                p.Should().NotBeInRange(600, 900, $"the wobble is no periodic error ({time / 60:F0} min: {pe.Describe()})");
                pe.ModelForStorage(DateTimeOffset.UnixEpoch)?.PeriodSeconds.Should().NotBeInRange(600, 900);
            }
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        TestContext.Out.WriteLine($"at the end: {pe.Describe()}, too small to predict: {pe.IsNegligible}");
        accepted.Should().BeEmpty("no period of that night passes the stability test, not even the weak worm");
        pe.PeriodSeconds.Should().BeNull();
        var rejections = notes.Where(n => n.Contains("not stable, not taken")).ToList();
        rejections.Should().NotBeEmpty("candidates were looked at and rejected");
        foreach (var note in rejections)
        {
            // only periods in the range, each once
            var periods = Regex.Matches(note, @"(\d+\.\d) s \(").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            periods.Should().NotBeEmpty().And.OnlyContain(v => v >= PeriodicErrorEstimator.MinPeriodSeconds && v <= PeriodicErrorEstimator.MaxPeriodSeconds, note);
            periods.Should().OnlyHaveUniqueItems(note);
        }
    }

    [Test]
    public void The_users_night_with_the_tooth_count_is_too_small_to_predict()
    {
        var night = Night.Load(Night.Evening);
        var pe = new PeriodicErrorEstimator(180);
        bool lastHourNegligible = true;
        foreach (var (time, px) in night.Frames)
        {
            pe.Observe(time, px, night.Context(time, hourAngle: true), true, night.SeeingPx2, 0.01 * night.SeeingPx2, 1e-10 * night.SeeingPx2);
            pe.IsStable.Should().BeFalse($"the fit changes from one worm turn to the next ({time / 60:F0} min: {pe.Describe()})");
            pe.ModelForStorage(DateTimeOffset.UnixEpoch).Should().BeNull("not stable");

            if (time > night.Frames[^1].Time - 3600)
            {
                lastHourNegligible &= pe.IsNegligible;
            }
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, pe.TakeNotes()));
        TestContext.Out.WriteLine($"at the end: {pe.Describe()}, size {pe.SizeRatio:F3}");
        pe.PeriodSeconds.Should().BeApproximately(478.69, 0.01);
        pe.IsNegligible.Should().BeTrue();
        lastHourNegligible.Should().BeTrue("it stays too small to predict");
    }

    [TestCase(0)]
    [TestCase(180)]
    public void The_Predictive_algorithm_never_predicts_on_the_users_night(int teeth)
    {
        // the night's open-loop motion as measured offsets with no corrections applied: the algorithm's own open-loop
        // reconstruction is the data, the seeing its own estimate
        var night = Night.Load(Night.Evening);
        var algorithm = new PredictiveAlgorithm(periodicError: true);
        algorithm.TrySetParam("wormTeeth", teeth);
        algorithm.PeriodicContext = night.Context(0, hourAngle: false);
        var start = new DateTimeOffset(2026, 9, 26, 19, 30, 0, TimeSpan.Zero);
        var notes = new List<string>();
        foreach (var (time, px) in night.Frames)
        {
            algorithm.Result(px, start.AddSeconds(time));
            notes.AddRange(algorithm.TakeNotes().Where(n => n.Kind == PredictiveNoteKind.PeriodicError).Select(n => n.Message));
            algorithm.PredictingPeriodicError.Should().BeFalse($"nothing to predict ({time / 60:F0} min: {algorithm.PeriodicError!.Describe()})");
            algorithm.TakePeriodicErrorModel(start.AddSeconds(time)).Should().BeNull();
            if (algorithm.PeriodicError!.PeriodSeconds is { } p && teeth == 0)
            {
                p.Should().NotBeInRange(600, 900);
            }
        }

        var state = algorithm.State.PeriodicError!;
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        TestContext.Out.WriteLine($"at the end: {state.Phase}, {algorithm.PeriodicError!.Describe()}");
        if (teeth > 0)
        {
            state.Phase.Should().Be(PeriodicErrorPhase.Negligible);
            state.PeriodSeconds.Should().BeApproximately(478.69, 0.01);
        }
        else
        {
            state.Phase.Should().BeOneOf(PeriodicErrorPhase.Learning, PeriodicErrorPhase.Negligible);
            state.Teeth.Should().BeNull();
        }
    }

    [Test]
    public void The_rest_of_that_night_with_the_tooth_count_set_is_never_ready()
    {
        // the same night from 22:28, when the tooth count was set to 180 by hand, to the end (2.9 h): the guider then in
        // use showed "ready" throughout while its fit jumped between ±0.5″ and ±12″ (RA axis) from one fit to the next,
        // and the prediction error with the curve stayed within ±2 % of the one without it. Even the largest fit, ±1.6″
        // on the sky, moves the star by 0.015 px per frame against a seeing σ of 0.1 px
        var night = Night.Load(Night.Late);
        var algorithm = new PredictiveAlgorithm(periodicError: true);
        algorithm.TrySetParam("wormTeeth", 180);
        var start = new DateTimeOffset(2026, 9, 26, 20, 28, 0, TimeSpan.Zero);
        var notes = new List<string>();
        var phases = new HashSet<PeriodicErrorPhase>();
        foreach (var (time, px) in night.Frames)
        {
            algorithm.PeriodicContext = night.Context(time, hourAngle: true);
            algorithm.Result(px, start.AddSeconds(time));
            notes.AddRange(algorithm.TakeNotes().Where(n => n.Kind == PredictiveNoteKind.PeriodicError).Select(n => n.Message));
            var pe = algorithm.PeriodicError!;
            var phase = algorithm.State.PeriodicError!.Phase;
            phases.Add(phase);
            phase.Should().BeOneOf([PeriodicErrorPhase.Learning, PeriodicErrorPhase.Negligible], $"never ready ({time / 60:F0} min: {pe.Describe()})");
            pe.IsStable.Should().BeFalse($"the curve changes from one part of the night to the next ({time / 60:F0} min: {pe.Describe()})");
            algorithm.State.PeriodicError!.Stable.Should().BeFalse();
            algorithm.PredictingPeriodicError.Should().BeFalse();
            algorithm.TakePeriodicErrorModel(start.AddSeconds(time)).Should().BeNull();
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        TestContext.Out.WriteLine($"at the end: {algorithm.State.PeriodicError!.Phase}, {algorithm.PeriodicError!.Describe()}");
        notes.Should().Contain(n => n.Contains("curve not stable"));
        phases.Should().Contain(PeriodicErrorPhase.Negligible, "too small to predict most of the time");
        algorithm.State.PeriodicError!.Phase.Should().Be(PeriodicErrorPhase.Negligible);
    }

    [Test]
    public void A_wobble_that_changes_amplitude_and_phase_is_no_periodic_error()
    {
        // worm-free: a ~720 s component whose amplitude and phase change every 15 min, over a red wander and the seeing
        var motion = new Motion(seed: 5)
        {
            Terms = { Wobble(720, amplitude: 1.2, coherenceSeconds: 900, seed: 1005) },
        };
        var pe = new PeriodicErrorEstimator();
        foreach (var (time, px) in motion.Frames(minutes: 180))
        {
            pe.Observe(time, px, motion.Context, true, motion.SeeingPx * motion.SeeingPx, 1e-4, 1e-10);
            pe.PeriodSeconds.Should().BeNull($"no stable period at {time / 60:F0} min ({pe.Describe()})");
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        notes.Should().Contain(n => n.Contains("not stable, not taken"));
    }

    [Test]
    public void A_weak_worm_is_found_under_a_stronger_wobble()
    {
        // like the user's night, but the worm (2″ on the sky, 478.7 s) stationary and large enough to predict, under a
        // wobble of 1.2 px RMS against its 0.94 px: it takes over two hours to tell apart from the wobble's leakage
        var motion = new Motion(seed: 7)
        {
            Terms =
            {
                t => 2.0 / 1.5 * Math.Sin(2 * Math.PI * t / 478.69 + 0.7),
                Wobble(720, amplitude: 1.2, coherenceSeconds: 900, seed: 1007),
            },
        };
        var pe = new PeriodicErrorEstimator();
        double? found = null;
        foreach (var (time, px) in motion.Frames(minutes: 180))
        {
            pe.Observe(time, px, motion.Context, true, motion.SeeingPx * motion.SeeingPx, 1e-4, 1e-10);
            if (pe.PeriodSeconds is { } p)
            {
                p.Should().BeApproximately(478.7, 0.02 * 478.7, $"only the worm ({time / 60:F0} min: {pe.Describe()})");
                found ??= time;
            }
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, pe.TakeNotes()));
        TestContext.Out.WriteLine($"found after {found / 60:F0} min; at the end: {pe.Describe()}");
        found.Should().NotBeNull();
        pe.PeriodSeconds.Should().NotBeNull();
        (pe.Teeth is null or 180).Should().BeTrue();
    }

    [Test]
    public void A_transient_taken_first_gives_way_to_the_worm()
    {
        // the first 40 minutes are dominated by a strong 300 s oscillation that then dies away: taken after five of its
        // cycles, the worm (478.7 s, there all along) takes over once it fades
        var motion = new Motion(seed: 9)
        {
            Terms =
            {
                t => 2.0 * Math.Sin(2 * Math.PI * t / 478.69 + 1.1),
                t => 6.0 * Math.Clamp((3000 - t) / 600, 0, 1) * Math.Sin(2 * Math.PI * t / 300 + 0.3),
            },
        };
        var pe = new PeriodicErrorEstimator();
        bool transientTaken = false;
        foreach (var (time, px) in motion.Frames(minutes: 150))
        {
            pe.Observe(time, px, motion.Context, true, motion.SeeingPx * motion.SeeingPx, 1e-4, 1e-10);
            transientTaken |= pe.PeriodSeconds is { } p && Math.Abs(p - 300) < 15;
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        transientTaken.Should().BeTrue("the transient looked stable at first");
        pe.PeriodSeconds!.Value.Should().BeApproximately(478.7, 0.01 * 478.7);
        notes.Should().Contain(n => n.Contains("no longer stable") || n.Contains("replaced by"));
    }

    [Test]
    public void A_stronger_stable_period_replaces_a_stored_one()
    {
        // a curve of 700 s from an earlier night (a real but small periodic error there); the worm is much stronger
        var worm = new Worm(Teeth: 180, Fundamental: 6, Second: 1, Third: 0.3);
        var old = new PeriodicErrorModel { PeriodSeconds = 700, Sin = [0.5, 0, 0], Cos = [0, 0, 0], Cycles = 8, LearnedAt = DateTimeOffset.UnixEpoch };
        var pe = new PeriodicErrorEstimator();
        pe.Restore(old).Should().BeTrue();
        var sky = new Sky(worm) { Extra = t => 0.3 * Math.Sin(2 * Math.PI * t / 700) };
        sky.Run(pe, minutes: 50);
        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        pe.PeriodSeconds!.Value.Should().BeApproximately(worm.Period, 0.01 * worm.Period);
        notes.Should().Contain(n => n.Contains("replaced by") || n.Contains("700.0 s no longer stable"));
        notes.Should().NotContain(n => n.Contains("fitted: period 6"), "the held period is not refined into another one");
        pe.TakeDiscardedPeriod().Should().Be(700, "a stored model with the old period is to be discarded");
    }

    [TestCase(0)]
    [TestCase(180)]
    public void A_stored_worm_survives_a_poor_night(int teethSetting)
    {
        // a curve with its tooth count from a good night, restored (with the tooth count set, or in detect mode) on the
        // user's night, where the worm is too weak to confirm under the wobble and the wander: taken back, not applied,
        // but the period, the tooth count and the stored curve stay (in detect mode it was dropped after 25 min and the
        // stored curve deleted)
        var model = new PeriodicErrorModel { Teeth = 180, PeriodSeconds = 478.69, Sin = [0.7, 0.2, 0.05], Cos = [0.3, 0.1, 0.0], Cycles = 25, LearnedAt = DateTimeOffset.UnixEpoch };
        var night = Night.Load(Night.Evening);
        var pe = new PeriodicErrorEstimator(teethSetting);
        pe.Restore(model).Should().BeTrue();
        foreach (var (time, px) in night.Frames)
        {
            pe.Observe(time, px, night.Context(time, hourAngle: true), true, night.SeeingPx2, 0.01 * night.SeeingPx2, 1e-10 * night.SeeingPx2);
            pe.TakeDiscardedPeriod().Should().BeNull($"a known tooth count is never discarded ({time / 60:F0} min: {pe.Describe()})");
            pe.Teeth.Should().Be(180);
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        pe.PeriodSeconds.Should().BeApproximately(478.69, 0.01);
        pe.IsStable.Should().BeFalse("the night does not confirm the curve");
        notes.Should().Contain(n => n.Contains("curve no longer stable"));
        pe.ModelForStorage(DateTimeOffset.UnixEpoch).Should().BeNull("stored again only once the data confirm it");
    }

    [Test]
    public void A_stored_period_too_weak_on_a_poor_night_keeps_its_stored_curve()
    {
        // a detected period without a tooth count, restored on the user's night: dropped, since the night cannot confirm
        // it, but nothing contradicts it and no other period is found, so its stored curve stays
        var model = new PeriodicErrorModel { PeriodSeconds = 478.7, Sin = [0.7, 0.2, 0.05], Cos = [0.3, 0.1, 0.0], Cycles = 25, LearnedAt = DateTimeOffset.UnixEpoch };
        var night = Night.Load(Night.Evening);
        var pe = new PeriodicErrorEstimator();
        pe.Restore(model).Should().BeTrue();
        foreach (var (time, px) in night.Frames)
        {
            pe.Observe(time, px, night.Context(time, hourAngle: true), true, night.SeeingPx2, 0.01 * night.SeeingPx2, 1e-10 * night.SeeingPx2);
            pe.TakeDiscardedPeriod().Should().BeNull($"{time / 60:F0} min: {pe.Describe()}");
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        notes.Should().Contain(n => n.Contains("478.7 s no longer stable") && n.Contains("stored curve is kept"));
    }

    [Test]
    public void A_stored_period_that_does_not_hold_gives_way_to_the_period_found()
    {
        // a stored 700 s curve on a mount with a worm of 478.7 s and nothing at 700 s: dropped since the data do not
        // confirm it, its stored curve kept until the worm is found, then discarded
        var model = new PeriodicErrorModel { PeriodSeconds = 700, Sin = [4.0, 0, 0], Cos = [0, 0, 0], Cycles = 8, LearnedAt = DateTimeOffset.UnixEpoch };
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator();
        pe.Restore(model).Should().BeTrue();
        double? discarded = null;
        bool dropped = false;
        for (int m = 0; m < 60 && discarded is null; m++)
        {
            sky.Run(pe, 1);
            dropped |= pe.PeriodSeconds is null;
            discarded = pe.TakeDiscardedPeriod();
            if (discarded is null)
            {
                pe.PeriodSeconds.Should().NotBeApproximately(478.7, 10, "the stored curve goes once the worm is found");
            }
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        dropped.Should().BeTrue();
        discarded.Should().Be(700);
        pe.PeriodSeconds!.Value.Should().BeApproximately(478.7, 0.01 * 478.7);
        notes.Should().Contain(n => n.Contains("700.0 s no longer stable") && n.Contains("stored curve is kept"));
        notes.Should().Contain(n => n.Contains("700.0 s, dropped earlier, gives way"));
    }

    [Test]
    public void A_period_is_detected_over_guiding_runs_and_kept_over_a_sync()
    {
        // NINA stops guiding for an autofocus or a filter change every 20-35 min: runs of 20 min, 3 min apart, each at
        // another lock position. The period is found over the runs; a plate-solve sync before the fifth (30′, 90° of worm
        // phase against the hour angle) starts a new stability run instead of reading as a change of the curve
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator();
        double? found = null;
        for (int k = 0; k < 8; k++)
        {
            if (k > 0)
            {
                sky.Pause(pe, 3);
            }

            if (k == 4)
            {
                sky.Sync(30 / 60.0 / 15.0);
            }

            sky.Run(pe, 20);
            if (pe.PeriodSeconds is { } p)
            {
                found ??= sky.Minutes;
                p.Should().BeApproximately(478.7, 0.01 * 478.7);
            }
            else
            {
                found.Should().BeNull($"a period is kept over the pauses and the sync ({sky.Minutes:F0} min)");
            }
        }

        var notes = pe.TakeNotes();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, notes));
        TestContext.Out.WriteLine($"found at the end of the run at {found:F0} min; at the end: {pe.Describe()}");
        found.Should().NotBeNull().And.BeLessThan(67, "five worm turns of data in the first three runs");
        notes.Should().NotContain(n => n.Contains("no longer stable") || n.Contains("replaced by"));
        pe.TakeDiscardedPeriod().Should().BeNull();
        pe.IsSignificant.Should().BeTrue();
        sky.PredictionRms(pe).Should().BeLessThan(0.2 * sky.CurveRms(), "the fit caught up with the sync");
    }

    [Test]
    public void Guiding_runs_shorter_than_five_worm_turns_add_up()
    {
        // runs of 25 min with 5 min between them: each alone has only about three turns of an 8″ worm
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator();
        for (int k = 0; k < 3 && pe.PeriodSeconds is null; k++)
        {
            if (k > 0)
            {
                sky.Pause(pe, 5);
            }

            sky.Run(pe, 25);
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, pe.TakeNotes()));
        pe.PeriodSeconds.Should().NotBeNull($"five turns over the runs ({sky.Minutes:F0} min)");
        pe.PeriodSeconds!.Value.Should().BeApproximately(478.7, 0.01 * 478.7);
    }

    [Test]
    public void With_the_tooth_count_runs_shorter_than_two_turns_add_up()
    {
        // runs of 6 min (three quarters of a worm turn) with 2 min between them: the curve counts once the runs together
        // hold two turns that agree; the progress is what the stability run has, and a sync starts it over
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator(180);
        sky.Run(pe, 6);
        double first = pe.Progress;
        first.Should().BeInRange(0.3, 0.45, "6 of the 16 min of the stability test");
        sky.Pause(pe, 2);
        sky.Run(pe, 6);
        pe.Progress.Should().BeGreaterThan(first + 0.3, "the second run adds to the first");
        pe.IsSignificant.Should().BeFalse();
        sky.Pause(pe, 2);
        sky.Run(pe, 6);
        pe.IsSignificant.Should().BeTrue($"two turns over three runs: {pe.Describe()}");

        var synced = new Sky(new Worm(Teeth: 180));
        var other = new PeriodicErrorEstimator(180);
        synced.Run(other, 6);
        synced.Pause(other, 2);
        synced.Sync(0.05);
        synced.Run(other, 3);
        other.Progress.Should().BeLessThan(0.25, "the sync started a new stability run");
    }

    [Test]
    public void A_gap_in_the_data_is_no_part_of_a_cycle()
    {
        // 10 min of data, 25 min without a frame (the star lost), then more: two worm turns of data (16 min) are two
        // parts of the stability test; counted from the span with the gap, the parts had covered less than a cycle each
        // and the test waited for 45 min of data
        var sky = new Sky(new Worm(Teeth: 180));
        var pe = new PeriodicErrorEstimator(180);
        sky.Run(pe, 10);
        sky.Gap(25);
        sky.Run(pe, 8);
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, pe.TakeNotes()));
        pe.IsSignificant.Should().BeTrue($"18 min of data: {pe.Describe()}");
    }

    [Test]
    public void Half_second_frames_detect_a_long_worm_period()
    {
        // 100 teeth (862 s): five turns take 72 min, 8600 frames of 0.5 s, more than the samples kept. Frames closer than
        // 1.2 s are averaged into one, so that the samples hold two hours
        var sky = new Sky(new Worm(Teeth: 100)) { FrameSeconds = 0.5 };
        var pe = new PeriodicErrorEstimator();
        sky.Run(pe, 90);
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, pe.TakeNotes()));
        pe.PeriodSeconds.Should().NotBeNull();
        pe.PeriodSeconds!.Value.Should().BeApproximately(861.6, 0.01 * 861.6);
        pe.IsSignificant.Should().BeTrue();
        sky.PredictionRms(pe).Should().BeLessThan(0.2 * sky.CurveRms());
    }

    [Test]
    public void A_cloud_does_not_make_the_curve_too_small()
    {
        // a 3″ worm, 2.5 s frames, seeing σ 0.25 px: its steepest change between frames is about a third of the seeing σ,
        // twice the limit. Under a cloud the frames are trusted less (measurement uncertainty, variance ×20), but the seeing is no worse
        var sky = new Sky(new Worm(Teeth: 180, Fundamental: 3, Second: 0.8, Third: 0.3));
        var pe = new PeriodicErrorEstimator(180);
        sky.Run(pe, 30);
        double clear = pe.SizeRatio;
        TestContext.Out.WriteLine($"clear: {pe.Describe()}, size {clear:F3}");
        pe.IsNegligible.Should().BeFalse();
        for (int m = 0; m < 15; m++)
        {
            sky.Run(pe, 1, noiseFactor: 20);
            pe.IsNegligible.Should().BeFalse($"under the cloud ({sky.Minutes:F0} min: size {pe.SizeRatio:F3})");
        }

        sky.Run(pe, 10);
        pe.IsNegligible.Should().BeFalse();
        pe.SizeRatio.Should().BeApproximately(clear, 0.3 * clear);
    }

    [Test]
    public void The_detection_is_spread_over_the_frames()
    {
        // the user's night in detect mode: a search every minute, five candidates each on up to 4353 samples. Done in one
        // frame that took up to 0.1 s on x86 (several times that on a Pi); one step per frame now, the heaviest a stability
        // test on the whole night
        var night = Night.Load(Night.Evening);
        var pe = new PeriodicErrorEstimator();
        long worst = 0, total = 0;
        double slowest = 0;
        var watch = new System.Diagnostics.Stopwatch();
        foreach (var (time, px) in night.Frames)
        {
            long before = PeriodicErrorEstimator.Evaluations;
            watch.Restart();
            pe.Observe(time, px, night.Context(time, hourAngle: true), true, night.SeeingPx2, 0.01 * night.SeeingPx2, 1e-10 * night.SeeingPx2);
            slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
            long used = PeriodicErrorEstimator.Evaluations - before;
            worst = Math.Max(worst, used);
            total += used;
        }

        TestContext.Out.WriteLine($"evaluations: worst frame {worst:N0}, all frames {total:N0}; slowest frame {slowest:F1} ms");
        worst.Should().BeLessThan(1_000_000);
        total.Should().BeGreaterThan(50 * worst, "the work is spread over the frames");
    }

    /// <summary>A worm curve in RA-axis arcsec: fundamental and two harmonics (the period follows the teeth unless given).</summary>
    private sealed record Worm(int Teeth, double PeriodSeconds = 0, double Fundamental = 8, double Second = 2.5, double Third = 1)
    {
        public double Period => PeriodSeconds > 0 ? PeriodSeconds : Sidereal.DaySeconds / Teeth;

        // against the axis angle in tracking seconds, like the estimator
        public double At(double axisSeconds)
        {
            double phase = 2 * Math.PI * axisSeconds / Period;
            return Fundamental * Math.Sin(phase + 0.4) + Second * Math.Sin(2 * phase + 1.3) + Third * Math.Sin(3 * phase + 2.2);
        }
    }

    /// <summary>
    /// The open-loop RA motion on the guide camera (px), frames every 2.5 s, as guiding runs: each run a new lock
    /// position (the level jumps), between runs a pause (autofocus: the mount tracks on) or a plate-solve sync (the
    /// reported RA moves, the axis does not).
    /// </summary>
    private sealed class Sky(Worm worm)
    {
        private readonly Random rng = new(3);
        private double time;
        private double wander;
        private double level;
        private double syncHours;

        public double StartHours { get; init; } = -1.0;

        public double DeclinationDeg { get; init; } = 20;

        public double PixelScale { get; init; } = 1.5;

        public double SeeingPx { get; init; } = 0.25;

        public double FrameSeconds { get; init; } = 2.5;

        public PierSide Side { get; set; } = PierSide.East;

        public bool WithAxis { get; init; } = true;

        /// <summary>Another motion in px against the time, e.g. a second periodic error.</summary>
        public Func<double, double>? Extra { get; init; }

        public double Minutes => time / 60;

        private double Cos => Math.Cos(DeclinationDeg * Math.PI / 180);

        /// <param name="noiseFactor">the extra measurement noise of the frames (measurement uncertainty), e.g. under a cloud; the frames themselves as usual</param>
        public void Run(PeriodicErrorEstimator pe, double minutes, double noiseFactor = 1)
        {
            double end = time + minutes * 60;
            for (; time < end; time += FrameSeconds)
            {
                wander += 0.01 * Math.Sqrt(FrameSeconds / 2.5) * Gauss();
                double truth = worm.At(AxisHours(time) * SecondsPerSiderealHour) * Cos / PixelScale + 0.002 * time + wander + level + (Extra?.Invoke(time) ?? 0);
                pe.Observe(time, truth + SeeingPx * Gauss(), Context(time), true, SeeingPx * SeeingPx, 1e-4, 1e-10, noiseFactor);
            }
        }

        /// <summary>Guiding stops for this long (the mount tracks on), then starts again at another lock position.</summary>
        public void Pause(PeriodicErrorEstimator pe, double minutes)
        {
            for (double end = time + minutes * 60; time < end; time += FrameSeconds)
            {
                wander += 0.01 * Math.Sqrt(FrameSeconds / 2.5) * Gauss();
            }

            level += 3 * Gauss();
            pe.StartSegment();
        }

        /// <summary>No frames for this long, guiding goes on (the star lost behind a cloud).</summary>
        public void Gap(double minutes)
        {
            for (double end = time + minutes * 60; time < end; time += FrameSeconds)
            {
                wander += 0.01 * Math.Sqrt(FrameSeconds / 2.5) * Gauss();
            }
        }

        /// <summary>A plate-solve sync: the reported RA moves by this much (hours), the axis and so the worm do not.</summary>
        public void Sync(double hours) => syncHours -= hours;

        /// <summary>RMS of the predicted curve against the true one over one worm cycle from now (px, both centred).</summary>
        public double PredictionRms(PeriodicErrorEstimator pe)
        {
            var diff = new List<double>();
            for (double t = time; t < time + worm.Period; t += 5)
            {
                double truth = worm.At(AxisHours(t) * SecondsPerSiderealHour) * Cos / PixelScale;
                diff.Add(pe.PredictPx(t, Context(t)) - truth);
            }

            double mean = diff.Average();
            return Math.Sqrt(diff.Sum(d => (d - mean) * (d - mean)) / diff.Count);
        }

        public double CurveRms()
        {
            var v = new List<double>();
            for (double t = time; t < time + worm.Period; t += 5)
            {
                v.Add(worm.At(AxisHours(t) * SecondsPerSiderealHour) * Cos / PixelScale);
            }

            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
        }

        // the true RA axis angle (the worm follows it)
        private double AxisHours(double t) => StartHours + t / SecondsPerSiderealHour + (Side == PierSide.West ? 12 : 0);

        private PeriodicErrorContext Context(double t) => new()
        {
            AxisHours = WithAxis ? AxisHours(t) + syncHours : null,
            DeclinationDeg = DeclinationDeg,
            PixelScale = PixelScale,
            PierSide = WithAxis ? Side : PierSide.Unknown,
        };

        private double Gauss()
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    // A wobble of the mount, no mechanical periodic error: a motion (px) of about the given period whose amplitude and
    // phase wander at random, coherent for about coherenceSeconds (narrowband noise: a complex Ornstein–Uhlenbeck
    // envelope on a carrier), with this RMS amplitude.
    private static Func<double, double> Wobble(double period, double amplitude, double coherenceSeconds, int seed)
    {
        const double step = 10;
        var rng = new Random(seed);
        double rho = Math.Exp(-step / coherenceSeconds), kick = amplitude * Math.Sqrt((1 - rho * rho) / 2);
        var re = new List<double> { amplitude };
        var im = new List<double> { 0.0 };
        return t =>
        {
            int n = (int)(t / step);
            while (re.Count < n + 2)
            {
                re.Add(rho * re[^1] + kick * Gauss(rng));
                im.Add(rho * im[^1] + kick * Gauss(rng));
            }

            double w = t / step - n, zr = re[n] + w * (re[n + 1] - re[n]), zi = im[n] + w * (im[n + 1] - im[n]);
            double phase = 2 * Math.PI * t / period;
            return zr * Math.Cos(phase) - zi * Math.Sin(phase);
        };
    }

    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>An open-loop RA motion in guide px from terms against the time, a red wander and the seeing; frames every 2.5 s, no mount information.</summary>
    private sealed class Motion(int seed)
    {
        private readonly Random rng = new(seed);

        public List<Func<double, double>> Terms { get; } = [];

        public double SeeingPx { get; init; } = 0.2;

        /// <summary>Random walk per frame (px).</summary>
        public double WanderPx { get; init; } = 0.02;

        public IEnumerable<(double Time, double Px)> Frames(double minutes)
        {
            double wander = 0;
            for (double t = 0; t < minutes * 60; t += 2.5)
            {
                wander += WanderPx * Gauss();
                yield return (t, Terms.Sum(term => term(t)) + wander + SeeingPx * Gauss());
            }
        }

        public PeriodicErrorContext Context { get; } = new() { DeclinationDeg = 20, PixelScale = 1.5 };

        private double Gauss()
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    /// <summary>
    /// The open-loop RA motion of a real night, rebuilt from its guide log (Data/*.csv): its first 2.8 h, and from 22:28,
    /// when the tooth count was set, to the end. Camera px = sky, 3.11″/px, Dec 56.8, 2 s exposures (~2.35 s apart).
    /// </summary>
    private sealed record Night(List<(double Time, double Px)> Frames, double SeeingPx2)
    {
        public const string Evening = "periodic-error-night-2026-09-26.csv";
        public const string Late = "periodic-error-night-2026-09-26-late.csv";

        public static Night Load(string file)
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", file);
            var frames = File.ReadLines(path)
                .Where(l => l.Length > 0 && char.IsDigit(l[0]))
                .Select(l => l.Split(','))
                .Select(p => (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)))
                .ToList();

            // the seeing σ from the frame-to-frame differences: median |Δ| = 0.954 σ for white noise (the wander is slow)
            var diffs = frames.Skip(1).Select((f, i) => Math.Abs(f.Item2 - frames[i].Item2)).Order().ToList();
            double sigma = diffs[diffs.Count / 2] / 0.954;
            return new Night(frames, sigma * sigma);
        }

        // the time as the only phase reference, or an hour angle that follows it (for storage)
        public PeriodicErrorContext Context(double time, bool hourAngle) => new()
        {
            AxisHours = hourAngle ? -2.0 + time / SecondsPerSiderealHour : null,
            DeclinationDeg = 56.8,
            PixelScale = 3.11,
            PierSide = hourAngle ? PierSide.East : PierSide.Unknown,
        };
    }
}
