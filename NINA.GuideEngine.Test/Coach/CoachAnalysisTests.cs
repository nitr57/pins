// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Test.Coach;

/// <summary>Unit tests of the coach measurement math on synthetic data with known truth.</summary>
[TestFixture]
public class CoachAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    private static List<DriftSample> Series(double seconds, double interval, Func<double, (double Ra, double Dec)> truth, double noisePx, int seed = 3,
        Func<int, (double Ra, double Dec)>? spikes = null)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var list = new List<DriftSample>();
        int i = 0;
        for (double t = 0; t <= seconds; t += interval, i++)
        {
            var (ra, dec) = truth(t);
            var (sr, sd) = spikes?.Invoke(i) ?? (0, 0);
            list.Add(new DriftSample(t, ra + noisePx * Gauss() + sr, dec + noisePx * Gauss() + sd, 50));
        }

        return list;
    }

    [Test]
    public void Seeing_is_the_high_frequency_rms_and_ignores_drift()
    {
        // 0.4 px white seeing per axis on top of steady drifts
        var samples = Series(240, 2.1, t => (0.02 * t, -0.01 * t), 0.4);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 1.5, declinationDeg: 30)!;

        a.SeeingRaPx.Should().BeApproximately(0.4, 0.06);
        a.SeeingDecPx.Should().BeApproximately(0.4, 0.06);
        a.SeeingTotalArcsec.Should().BeApproximately(0.4 * Math.Sqrt(2) * 1.5, 0.12);
        a.RaDriftPxPerSec.Should().BeApproximately(0.02, 0.002);
        a.DecDriftPxPerSec.Should().BeApproximately(-0.01, 0.002);
        a.PeriodSeconds.Should().BeNull("a straight drift has no period");
        a.GustPercent.Should().BeLessThan(2);
    }

    [Test]
    public void Periodic_error_period_amplitude_and_rate_are_recovered()
    {
        // 4 px amplitude, 150 s period over 400 s (2.7 periods) plus drift and seeing
        double w = 2 * Math.PI / 150;
        var samples = Series(400, 2.1, t => (4 * Math.Sin(w * t + 0.3) + 0.01 * t, 0), 0.3);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 2.0, declinationDeg: 0)!;

        a.PeriodSeconds.Should().BeApproximately(150, 5);
        a.PeriodicAmplitudeArcsec.Should().BeApproximately(8, 0.6);
        a.RaMaxRateArcsecPerSec.Should().BeApproximately(2 * (4 * w + 0.01), 0.1 * 2 * 4 * w);
        a.SeeingRaPx.Should().BeApproximately(0.3, 0.06, "the sinusoid does not leak into the seeing");
        a.DriftLimitingExposureSeconds.Should().BeApproximately(a.SeeingRaArcsec / a.RaMaxRateArcsecPerSec, 1e-9);

        // the published model reproduces the samples (relative to the first one) up to the seeing:
        // Ra(T) = offset + drift·T/60 + amplitude·sin(2πT/period + phase)
        double RaModel(double t) => a.PeriodicOffsetArcsec!.Value + a.RaDriftArcsecPerMin * t / 60
            + a.PeriodicAmplitudeArcsec * Math.Sin(2 * Math.PI * t / a.PeriodSeconds!.Value + a.PeriodicPhaseRad!.Value);
        double rms = Math.Sqrt(samples.Average(x => Math.Pow((x.RaPx - samples[0].RaPx) * 2.0 - RaModel(x.T), 2)));
        rms.Should().BeLessThan(1.2 * 0.3 * 2.0, "residuals are the 0.3 px seeing");
        a.RaDriftArcsecPerMin.Should().BeApproximately(0.01 * 60 * 2.0, 0.3);
    }

    [Test]
    public void No_period_is_reported_when_the_run_covers_less_than_1_2_periods()
    {
        double w = 2 * Math.PI / 480;
        var samples = Series(180, 2.1, t => (6 * Math.Sin(w * t), 0), 0.2);
        var a = DriftAnalyzer.Analyze(samples, 2.0, 1.0, 0)!;

        a.PeriodSeconds.Should().BeNull();
        a.PeriodicAmplitudeArcsec.Should().BeGreaterThan(0.5, "peak-to-peak based amplitude is still reported");
        a.RaMaxRateArcsecPerSec.Should().BeApproximately(6 * w, 0.35 * 6 * w);
        a.RaPeakToPeakArcsec.Should().BeGreaterThan(5);
    }

    [Test]
    public void Polar_alignment_error_from_dec_drift_and_assumed_declination()
    {
        // 0.5 px/min at 2″/px = 1″/min; at dec 60°: 3.8197 × 1 / 0.5 = 7.64′
        var samples = Series(180, 2.1, t => (0, 0.5 / 60 * t), 0.2);
        var a = DriftAnalyzer.Analyze(samples, 2.0, 2.0, 60)!;
        a.DecDriftArcsecPerMin.Should().BeApproximately(1.0, 0.1);
        a.PolarAlignmentErrorArcmin.Should().BeApproximately(7.64, 0.8);
        a.DeclinationAssumed.Should().BeFalse();

        var unknown = DriftAnalyzer.Analyze(samples, 2.0, 2.0, null)!;
        unknown.DeclinationAssumed.Should().BeTrue();
        unknown.PolarAlignmentErrorArcmin.Should().BeApproximately(3.82, 0.4, "dec 0 is assumed");
    }

    [Test]
    public void Gusts_are_counted_as_large_frame_to_frame_jumps()
    {
        // single-frame 4 px displacements on 5 of ~115 frames: each gives two jumps (into and out of the spike)
        var samples = Series(240, 2.1, _ => (0, 0), 0.2, spikes: i => i is 20 or 40 or 60 or 80 or 100 ? (4.0, 0) : (0, 0));
        var a = DriftAnalyzer.Analyze(samples, 2.0, 1.0, 0)!;
        a.GustPercent.Should().BeInRange(5, 10);
    }

    [Test]
    public void Min_move_follows_the_seeing_with_phd2_multipliers()
    {
        var samples = Series(240, 2.1, _ => (0, 0), 0.3);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 1.0, declinationDeg: 0)!;
        double expectedDec = Math.Ceiling(a.SeeingDecPx * 1.28 / 0.05 - 1e-9) * 0.05;
        double expectedRa = Math.Max(0.1, Math.Ceiling(a.SeeingRaPx * 1.28 * 0.65 / 0.05 - 1e-9) * 0.05);
        a.MinMoveDecPx.Should().BeApproximately(expectedDec, 1e-9);
        a.MinMoveRaPx.Should().BeApproximately(expectedRa, 1e-9);
        a.MinMoveDecPx.Should().BeInRange(0.35, 0.45);

        // bad data (min-move above 1.25″) falls back to the smart default
        var wild = DriftAnalyzer.Analyze(Series(240, 2.1, _ => (0, 0), 3.0), 2.0, 1.0, 0, smartMinMovePx: 0.25)!;
        wild.MinMoveDecPx.Should().Be(0.25);
    }

    [Test]
    public void Camera_check_jitter_removes_linear_drift_and_selection_prefers_stars_then_short_exposure()
    {
        // positions on a line plus ±0.1 px alternating residuals
        var frames = Enumerable.Range(0, 6).Select(i => new CameraFrameMeasurement(i * 2.0, true, 100 + 0.5 * i + (i % 2 == 0 ? 0.1 : -0.1), 50 - 0.2 * i, 40, 2.5, false))
            .ToList();
        double jitter = CameraCheckAnalyzer.DetrendedJitter(frames);
        jitter.Should().BeLessThan(0.15).And.BeGreaterThan(0.08);

        var r = CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames, stars: 6, pixelScale: 2.0);
        r.Feasible.Should().BeTrue();
        r.JitterArcsec.Should().BeApproximately(2 * r.JitterPx!.Value, 0.002);
        r.Gain.Should().Be(50);

        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames.Select(f => f with { Saturated = true }).ToList(), 6, 2.0).Reason.Should().Be("saturated");
        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames.Select(f => f with { Snr = 9 }).ToList(), 6, 2.0).Reason.Should().Be("lowSnr");
        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames, 0, 2.0).Reason.Should().Be("noStar");

        CoachCameraResult R(double exp, int gain, double jit, int stars, bool feasible = true) =>
            new() { ExposureSeconds = exp, Gain = gain, JitterArcsec = jit, Stars = stars, Feasible = feasible };
        var results = new[]
        {
            R(1, 0, 0.30, 4),
            R(2, 0, 0.21, 5),
            R(3, 0, 0.20, 5),
            R(2, 50, 0.215, 9),
            R(3, 100, 0.10, 12, feasible: false),
        };
        CameraCheckAnalyzer.SelectRecommended(results).Should().BeSameAs(results[3], "within 10 % of the best jitter more stars win");
        var tie = new[] { R(3, 0, 0.20, 5), R(2, 0, 0.21, 5) };
        CameraCheckAnalyzer.SelectRecommended(tie).Should().BeSameAs(tie[1], "equal stars: the shorter exposure");
        CameraCheckAnalyzer.SelectRecommended([R(1, 0, 0.3, 1, false)]).Should().BeNull();
    }

    [Test]
    public void Pulse_response_analysis_finds_min_pulse_asymmetry_and_rate()
    {
        CoachPulse P(string dir, int ms, double ratio) => new() { Direction = dir, DurationMs = ms, Ratio = ratio, ExpectedArcsec = ms / 100.0, MovedArcsec = ratio * ms / 100.0 };
        var pulses = new List<CoachPulse>
        {
            P("West", 100, 0.05), P("East", 100, 0.0), P("West", 250, 0.3), P("East", 250, 0.2),
            P("West", 500, 0.7), P("East", 500, 0.55), P("West", 1000, 1.1), P("East", 1000, 0.7),
            P("North", 100, 0.9), P("South", 100, 1.1), P("North", 1000, 1.0), P("South", 1000, 1.0),
        };
        MountResponseAnalyzer.MinEffectivePulse(pulses, ra: true).Should().Be(500);
        MountResponseAnalyzer.MinEffectivePulse(pulses, ra: false).Should().Be(100);
        MountResponseAnalyzer.Asymmetry(pulses, "West", "East", 500)!.Value.Should().BeApproximately(0.9 / 0.625, 1e-9);
        MountResponseAnalyzer.RateRatio(pulses, ra: false, 500)!.Value.Should().BeApproximately(1.0, 1e-9);
        MountResponseAnalyzer.MinEffectivePulse([P("East", 1000, 0.2)], true).Should().BeNull();
        MountResponseAnalyzer.StictionPulse(pulses, ra: true, 0).Should().Be(500);
        MountResponseAnalyzer.StictionPulse(pulses, ra: false, 0).Should().BeNull();

        // with 0.5″ of measurement noise (mean of 4 moves: 0.25″) the 100 and 250 ms moves (1″, 2.5″ expected) are not
        // conclusive enough at 100 ms: expected 1″ < 3 × 0.25″ is false (1 ≥ 0.75), so shrink the pulses to see the rule
        var small = pulses.Select(p => p with { ExpectedArcsec = p.ExpectedArcsec / 4, MovedArcsec = p.MovedArcsec / 4 }).ToList();
        var verdicts = MountResponseAnalyzer.Classify(small, ra: true, sigmaMoveArcsec: 0.5);
        verdicts.Single(v => v.DurationMs == 100).Verdict.Should().Be(PulseVerdict.Inconclusive, "0.25″ expected is within 3σ = 0.75″");
        verdicts.Single(v => v.DurationMs == 1000).Verdict.Should().Be(PulseVerdict.Effective);
        MountResponseAnalyzer.StictionPulse(small, ra: true, sigmaMoveArcsec: 0.5).Should().BeNull("an inconclusive pulse is no evidence of stiction");
    }

    [Test]
    public void Noisy_mount_without_stiction_gives_no_stiction()
    {
        foreach (var (noise, seed, seeing) in new[] { (0.25, 1, (double?)null), (0.25, 2, 0.25), (0.4, 3, null), (0.4, 4, 0.4), (0.6, 5, null) })
        {
            var mount = new KinematicDecMount(seed) { NoisePx = noise };
            var result = Run(mount, new MountResponseProcedure(0, 0, lostTimeoutSec: 30, seeingRaPx: seeing, seeingDecPx: seeing));
            TestContext.Out.WriteLine($"noise {noise} px seed {seed}: stiction RA {result.StictionRaMs} Dec {result.StictionDecMs}, " +
                $"min pulse {result.Response.MinEffectivePulseRaMs}/{result.Response.MinEffectivePulseDecMs}, σ {result.SigmaRaArcsec:F3}″");
            result.StictionRaMs.Should().BeNull();
            result.StictionDecMs.Should().BeNull();
            result.SigmaRaArcsec.Should().BeApproximately(noise * Math.Sqrt(2.0 / 3) * mount.PixelScale, 0.5 * noise * mount.PixelScale);
        }
    }

    [Test]
    public void Real_stiction_is_found_through_the_noise()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var mount = new KinematicDecMount(seed) { RaStictionMs = 300, NoisePx = 0.25 };
            var result = Run(mount, new MountResponseProcedure(0, 0, lostTimeoutSec: 30, seeingRaPx: 0.25, seeingDecPx: 0.25));
            TestContext.Out.WriteLine($"seed {seed}: stiction RA {result.StictionRaMs} Dec {result.StictionDecMs}");
            result.StictionRaMs.Should().Be(1000, "300 ms stiction leaves 500 ms pulses at 40 % of the expected move");
            result.StictionDecMs.Should().BeNull();
        }
    }

    [Test]
    public void Defocus_needs_both_pixels_and_arcseconds()
    {
        CameraCheckAnalyzer.IsDefocused(2.2, 3.1).Should().BeFalse("an undersampled guide scope shows ~2 px ≈ 7″ in focus");
        CameraCheckAnalyzer.IsDefocused(8, 0.5).Should().BeFalse("8 px at 0.5″/px is a normal 4″ star");
        CameraCheckAnalyzer.IsDefocused(14, 0.5).Should().BeTrue();
        CameraCheckAnalyzer.IsDefocused(5, 1.5).Should().BeTrue();
        CameraCheckAnalyzer.IsDefocused(3.01, 1.51).Should().BeFalse("the smoke test's 3 px / 4.5″ stars are fine");
    }

    [Test]
    public void Trial_winner_must_beat_the_baseline_by_two_standard_errors()
    {
        CoachTrial T(string id, string kind, double rms, int frames) => new() { Id = id, Kind = kind, State = CoachTrialStates.Done, RmsTotalArcsec = rms, Frames = frames };

        // the smoke test: B 5.6 % better than A with ~100 frames each is within the noise
        var smoke = TrialJudge.Judge([T("A", "current", 0.268, 100), T("B", "suggestion", 0.253, 100), T("C", "variant", 0.285, 100)]);
        smoke.Significant.Should().BeFalse();
        smoke.Winner!.Id.Should().Be("A", "the current settings are as good within the noise");
        smoke.Best!.Id.Should().Be("B");
        smoke.ImprovementPercent!.Value.Should().BeApproximately(5.6, 0.1);

        var clear = TrialJudge.Judge([T("A", "current", 0.40, 100), T("B", "suggestion", 0.30, 100)]);
        clear.Significant.Should().BeTrue();
        clear.Winner!.Id.Should().Be("B");
        TrialJudge.StandardError(T("A", "current", 0.4, 100)).Should().BeApproximately(0.4 / Math.Sqrt(200), 1e-12);

        // A2 differs by 33 %: with 20 frames each that is noise, with 200 frames each it is a change of conditions
        TrialJudge.Judge([T("A", "current", 0.30, 20), T("A2", "currentRepeat", 0.40, 20)]).ConditionsChanged.Should().BeFalse();
        var changed = TrialJudge.Judge([T("A", "current", 0.30, 200), T("A2", "currentRepeat", 0.40, 200)]);
        changed.ConditionsChanged.Should().BeTrue();
        changed.ConditionsChangePercent!.Value.Should().BeApproximately(33.3, 0.1);
        TrialJudge.Judge([T("A", "current", 0.30, 200), T("A2", "currentRepeat", 0.36, 200)]).ConditionsChanged.Should().BeFalse("20 % is below 25 %");

        var noBaseline = TrialJudge.Judge([T("B", "suggestion", 0.3, 50)]);
        noBaseline.Winner.Should().BeNull();
        TrialJudge.Judge([T("A2", "currentRepeat", 0.4, 100), T("B", "suggestion", 0.3, 100)]).Baseline!.Id.Should().Be("A2");
    }

    [Test]
    public void Backlash_procedure_measures_the_dead_band_of_a_kinematic_mount_without_bias()
    {
        foreach (double backlashMs in new[] { 0.0, 450.0, 1500.0, 3200.0 })
        {
            var mount = new KinematicDecMount { BacklashMs = backlashMs, NoisePx = 0.05 };
            var result = Run(mount, new MountResponseProcedure(0, 0, lostTimeoutSec: 30));
            var r = result.Response;
            TestContext.Out.WriteLine($"true {backlashMs} ms -> {r.BacklashMs} ms ({r.BacklashState}), points {r.BacklashPoints.Count}");
            if (backlashMs == 0)
            {
                r.BacklashState.Should().Be(CoachBacklashStates.None);
                r.BacklashMs!.Value.Should().BeLessThan(50);
            }
            else
            {
                r.BacklashState.Should().Be(CoachBacklashStates.Measured);
                r.BacklashMs!.Value.Should().BeApproximately(backlashMs, 40, "the method has no systematic bias");
                double arcsec = backlashMs * mount.DecRate * mount.PixelScale;
                r.BacklashArcsec!.Value.Should().BeApproximately(arcsec, 0.05 * arcsec + 0.1);
            }

            r.BacklashPoints.Should().NotBeEmpty();
            r.MinEffectivePulseRaMs.Should().Be(100);
            r.MinEffectivePulseDecMs.Should().Be(100, "Dec response pulses are measured with the gear engaged");
            r.RateRatioRa!.Value.Should().BeApproximately(1.0, 0.05);
            r.RateRatioDec!.Value.Should().BeApproximately(1.0, 0.05);
            r.AsymmetryDec!.Value.Should().BeApproximately(1.0, 0.1);
            Math.Abs(mount.X).Should().BeLessThan(1.5, "the star is recentred");
            Math.Abs(mount.Y).Should().BeLessThan(1.5);
        }
    }

    [Test]
    public void Reversal_test_measures_the_smaller_slack_of_reversals_after_small_moves()
    {
        // soft slack: a reversal loses 300 ms plus half the preceding travel (up to 1400 ms). After the long North series the
        // first South pulses lose 1000 ms. Alternating pulses of P lose 300 + P/2 and start to move the star above
        // P = 300 / (1 − 0.5) = 600 ms: the compensation at which a reversal after a same-sized move just lands
        var mount = new KinematicDecMount { SoftFraction = 0.5, SoftBaseMs = 300, SoftMaxTravelMs = 1400, NoisePx = 0.05 };
        var proc = new MountResponseProcedure(0, 0, lostTimeoutSec: 30);
        var live = new List<(string? State, double? Ms)>();
        var r = Run(mount, proc, onFrame: () => live.Add((proc.Snapshot().BacklashState, proc.Snapshot().BacklashMs))).Response;
        TestContext.Out.WriteLine($"large move {r.LargeMoveBacklashMs} ms, guiding {r.BacklashMs} ms, reversal onset {r.ReversalPulseMs} ms");

        r.BacklashState.Should().Be(CoachBacklashStates.Measured);
        r.LargeMoveBacklashMs!.Value.Should().BeApproximately(1000, 50);
        r.ReversalPulseMs.Should().NotBeNull().And.BeLessThan(900, "an earlier ladder step moved the star");
        r.ReversalMoves.Should().NotBeEmpty();
        r.BacklashMs!.Value.Should().BeApproximately(600, 100);

        // the live status never shows the large-move value as the result: no value while measuring, then the final one
        live.Should().Contain(x => x.State == CoachBacklashStates.Measuring);
        live.Where(x => x.State == CoachBacklashStates.Measuring).Should().OnlyContain(x => x.Ms == null);
        live.Where(x => x.Ms != null).Should().OnlyContain(x => x.Ms == r.BacklashMs);
    }

    [Test]
    public void Frames_exposed_during_a_pulse_do_not_bias_the_measured_moves()
    {
        // every second pulse is followed by a frame that began its exposure during the pulse and shows half the move
        var mount = new KinematicDecMount { BacklashMs = 450, NoisePx = 0.03 };
        var r = Run(mount, new MountResponseProcedure(0, 0, lostTimeoutSec: 30), earlyFrames: true).Response;

        r.RateRatioRa!.Value.Should().BeApproximately(1.0, 0.05);
        r.RateRatioDec!.Value.Should().BeApproximately(1.0, 0.05);
        r.LargeMoveBacklashMs!.Value.Should().BeApproximately(450, 40);
        r.BacklashMs!.Value.Should().BeApproximately(450, 40, "a hard dead band: no ladder step below it moves the star");
    }

    [Test]
    public void Camera_recommendation_keeps_the_current_settings_unless_a_change_is_justified()
    {
        static CoachCameraResult R(double exposure, int gain, double jitterPx, int frames = 5, int stars = 12, bool feasible = true) => new()
        {
            ExposureSeconds = exposure, Gain = gain, Frames = frames, Snr = 100, Hfd = 2.5, Stars = stars, JitterPx = jitterPx,
            JitterArcsec = jitterPx * 3.1, Feasible = feasible, Reason = feasible ? null : CameraCheckAnalyzer.ReasonSaturated,
        };

        // the rig's 2026-09-29 camera check: 9 combinations of 5 frames, jitter 0.07–0.21 px without a trend
        var current = R(2, 50, 0.144);
        var rig = new List<CoachCameraResult>
        {
            R(1, 50, 0.129), current, R(3, 50, 0.098), R(1, 25, 0.12), R(2, 25, 0.15), R(3, 25, 0.185), R(1, 75, 0.209), R(2, 75, 0.072),
            R(3, 75, 0.14),
        };
        CameraCheckAnalyzer.SelectRecommended(rig)!.Gain.Should().Be(75, "gain 75 had the lowest jitter");
        CameraCheckAnalyzer.Recommend(rig, current).Should().BeSameAs(current, "half the jitter of 5 frames, the best of 9, is not significant");

        // a clearly steadier combination measured with enough frames is recommended
        var noisy = R(0.5, 50, 0.5, frames: 12);
        var steady = R(2, 50, 0.15, frames: 12);
        CameraCheckAnalyzer.Recommend([noisy, steady], noisy).Should().BeSameAs(steady);

        // infeasible current settings, or too few stars for multi-star, always take the recommendation
        var saturated = R(2, 50, 0.1, feasible: false);
        var fine = R(1, 25, 0.14);
        CameraCheckAnalyzer.Recommend([saturated, fine], saturated).Should().BeSameAs(fine);
        var fewStars = R(1, 50, 0.13, stars: 2);
        var manyStars = R(2, 50, 0.13, stars: 8);
        CameraCheckAnalyzer.Recommend([fewStars, manyStars], fewStars).Should().BeSameAs(manyStars);
    }

    [Test]
    public void Upper_normal_quantile_matches_the_table()
    {
        CameraCheckAnalyzer.UpperNormalQuantile(0.05).Should().BeApproximately(1.645, 0.001);
        CameraCheckAnalyzer.UpperNormalQuantile(0.025).Should().BeApproximately(1.960, 0.001);
        CameraCheckAnalyzer.UpperNormalQuantile(0.00625).Should().BeApproximately(2.498, 0.001);
    }

    [Test]
    public void Response_procedure_detects_stiction_asymmetry_and_corrects_dec_drift()
    {
        var mount = new KinematicDecMount { BacklashMs = 800, RaStictionMs = 200, EastEfficiency = 0.5, SouthEfficiency = 0.6, DecDriftPxPerSec = 0.03, NoisePx = 0.03 };
        var result = Run(mount, new MountResponseProcedure(0, mount.DecDriftPxPerSec, lostTimeoutSec: 30));
        var r = result.Response;
        r.MinEffectivePulseRaMs.Should().Be(1000, "200 ms stiction plus a half-speed East leaves 500 ms pulses below 50 %");
        r.AsymmetryRa!.Value.Should().BeApproximately(0.8 / 0.4, 0.2);
        // the dead band (800 ms at the North rate) takes 800 / 0.6 ms of the slower South pulses; the angle is the same
        r.LargeMoveBacklashMs!.Value.Should().BeApproximately(800 / 0.6, 70, "the known Dec drift is removed and the slower South rate is measured");
        r.LargeMoveBacklashArcsec!.Value.Should().BeApproximately(800 * mount.DecRate * mount.PixelScale, 0.4);
        // equal alternating pulses with a slower South motor creep North through the play, as guiding pulses would: the
        // guiding value lies below the large-move one
        r.BacklashMs!.Value.Should().BeInRange(0, r.LargeMoveBacklashMs.Value);
        r.AsymmetryDec!.Value.Should().BeApproximately(1 / 0.6, 0.15);
        result.StarLost.Should().BeFalse();
    }

    [Test]
    public void Autocorrelated_trials_with_equal_rms_rarely_produce_a_winner()
    {
        int winners = 0, naiveWinners = 0;
        double rhoSum = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(seed);
            var a = Ar1Trial("A", CoachTrialKinds.Current, 100, 0.7, sigma: 0.5, rng);
            var b = Ar1Trial("B", CoachTrialKinds.Suggestion, 100, 0.7, sigma: 0.5, rng);
            rhoSum += a.Stats.RhoRa;
            var neff = new Dictionary<string, double> { ["A"] = a.Stats.EffectiveFrames, ["B"] = b.Stats.EffectiveFrames };
            if (TrialJudge.Judge([a.Trial, b.Trial], neff).Significant)
            {
                winners++;
            }

            if (TrialJudge.Judge([a.Trial, b.Trial]).Significant)
            {
                naiveWinners++;
            }
        }

        TestContext.Out.WriteLine($"mean ρ {rhoSum / 200:F2}; false winners {winners}/200 (frame-count rule: {naiveWinners}/200)");
        winners.Should().BeLessThanOrEqualTo(10, "equal trials must produce a winner in at most 5 % of the seeds");
        naiveWinners.Should().BeGreaterThan(winners, "ignoring the autocorrelation overstates the significance");
    }

    [Test]
    public void A_truly_better_trial_still_wins()
    {
        // deterministic, uncorrelated errors (lag-1 autocorrelation ≤ 0): B has 30 % lower RMS over 100 frames
        var pattern = new[] { 1.0, 1.0, -1.0, -1.0 };
        List<TrialSample> Series(double amplitude) => Enumerable.Range(0, 100)
            .Select(i => new TrialSample(i * 2.0, amplitude * pattern[i % 4], amplitude * pattern[(i + 1) % 4], 50)).ToList();
        var sa = TrialStatistics.Compute(Series(0.5), 1.0)!;
        var sb = TrialStatistics.Compute(Series(0.35), 1.0)!;
        sa.EffectiveFrames.Should().BeGreaterThan(95, "hardly any autocorrelation");
        var verdict = TrialJudge.Judge([ToTrial("A", CoachTrialKinds.Current, sa), ToTrial("B", CoachTrialKinds.Suggestion, sb)],
            new Dictionary<string, double> { ["A"] = sa.EffectiveFrames, ["B"] = sb.EffectiveFrames });
        verdict.Significant.Should().BeTrue();
        verdict.Winner!.Id.Should().Be("B");
        verdict.ImprovementPercent!.Value.Should().BeApproximately(30, 0.1);

        // random independent errors: the 30 % improvement is found in most runs despite the noise of both RMS values
        int wins = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(1000 + seed);
            var a = Ar1Trial("A", CoachTrialKinds.Current, 100, 0.0, sigma: 0.5, rng);
            var b = Ar1Trial("B", CoachTrialKinds.Suggestion, 100, 0.0, sigma: 0.35, rng);
            if (TrialJudge.Judge([a.Trial, b.Trial], new Dictionary<string, double> { ["A"] = a.Stats.EffectiveFrames, ["B"] = b.Stats.EffectiveFrames }).Significant)
            {
                wins++;
            }
        }

        TestContext.Out.WriteLine($"30 % better trial won {wins}/200 (independent frames)");
        wins.Should().BeGreaterThanOrEqualTo(160);

        // moderately correlated errors (ρ = 0.5, 120 frames): the improvement is still found in most runs (~90 %)
        int correlatedWins = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(5000 + seed);
            var a = Ar1Trial("A", CoachTrialKinds.Current, 120, 0.5, sigma: 0.5, rng);
            var b = Ar1Trial("B", CoachTrialKinds.Suggestion, 120, 0.5, sigma: 0.35, rng);
            if (TrialJudge.Judge([a.Trial, b.Trial], new Dictionary<string, double> { ["A"] = a.Stats.EffectiveFrames, ["B"] = b.Stats.EffectiveFrames }).Significant)
            {
                correlatedWins++;
            }
        }

        TestContext.Out.WriteLine($"30 % better trial won {correlatedWins}/200 (ρ = 0.5, 120 frames)");
        correlatedWins.Should().BeGreaterThanOrEqualTo(160);
    }

    [Test]
    public void Cloud_noise_is_the_centroid_noise_beyond_the_usual_scatter()
    {
        List<TrialSample> Series(Func<int, double?> sigma) => Enumerable.Range(0, 100).Select(i => new TrialSample(i * 2.0, 0.1, -0.1, 50, sigma(i))).ToList();

        TrialStatistics.Compute(Series(_ => null), 1.5)!.CloudNoiseArcsec.Should().Be(0, "no per-frame σ");
        TrialStatistics.Compute(Series(i => 0.05 + 0.02 * (i % 3)), 1.5)!.CloudNoiseArcsec.Should().Be(0, "usual scatter");

        // 30 frames at 0.3 px under the median's 0.05 px: their σ² beyond 1.5² × 0.05², averaged over all 100, on both axes
        double expected = Math.Sqrt(2 * 30 * (0.09 - 2.25 * 0.0025) / 100) * 1.5;
        TrialStatistics.Compute(Series(i => i < 30 ? 0.3 : 0.05), 1.5)!.CloudNoiseArcsec.Should().BeApproximately(expected, 1e-12);
        TrialStatistics.Compute(Series(i => i < 30 ? 0.3 : i < 50 ? null : 0.05), 1.5)!.CloudNoiseArcsec.Should().BeApproximately(expected, 1e-12,
            "frames without σ count as usual ones");
    }

    [Test]
    public void A_cloud_over_the_baseline_makes_no_winner()
    {
        // equal settings, but thin clouds over a third of A's frames: the centroid noise of the faint star grows 8×
        int winners = 0, winnersWithoutM3 = 0, betterWins = 0, betterWinsClear = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(7000 + seed);
            var a = CloudyTrial("A", CoachTrialKinds.Current, 0.25, i => i is >= 30 and < 70, rng);
            var clear = CloudyTrial("A", CoachTrialKinds.Current, 0.25, _ => false, rng);
            var b = CloudyTrial("B", CoachTrialKinds.Suggestion, 0.25, _ => false, rng);
            var better = CloudyTrial("B", CoachTrialKinds.Suggestion, 0.175, _ => false, rng);
            bool Wins((CoachTrial Trial, TrialStatistics Stats) baseline, (CoachTrial Trial, TrialStatistics Stats) alternative, bool m3 = true) =>
                TrialJudge.Judge([baseline.Trial, alternative.Trial],
                    new Dictionary<string, double> { ["A"] = baseline.Stats.EffectiveFrames, ["B"] = alternative.Stats.EffectiveFrames },
                    m3 ? new Dictionary<string, double> { ["A"] = baseline.Stats.CloudNoiseArcsec, ["B"] = alternative.Stats.CloudNoiseArcsec } : null)
                .Significant;
            winners += Wins(a, b) ? 1 : 0;
            winnersWithoutM3 += Wins(a, b, m3: false) ? 1 : 0;
            betterWins += Wins(a, better) ? 1 : 0;
            betterWinsClear += Wins(clear, better) ? 1 : 0;
        }

        TestContext.Out.WriteLine($"false winners {winners}/200 ({winnersWithoutM3}/200 without the cloud noise); a 30 % better B won {betterWins}/200 " +
            $"({betterWinsClear}/200 against a clear baseline)");
        winnersWithoutM3.Should().BeGreaterThan(100);
        winners.Should().BeLessThanOrEqualTo(10);
        betterWins.Should().BeGreaterThan(betterWinsClear * 3 / 4, "a better trial still wins in most runs");
    }

    // 120 frames of AR(1) errors (ρ = 0.5) measured with a faint star's centroid noise (0.05 px, 0.4 px when cloudy), σ reported
    private static (CoachTrial Trial, TrialStatistics Stats) CloudyTrial(string id, string kind, double sigma, Func<int, bool> cloudy, Random rng)
    {
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var ra = Ar1(120, 0.5, sigma, rng);
        var dec = Ar1(120, 0.5, sigma, rng);
        var samples = Enumerable.Range(0, 120).Select(i =>
        {
            double c = cloudy(i) ? 0.4 : 0.05;
            return new TrialSample(i * 2.0, ra[i] + c * Gauss(), dec[i] + c * Gauss(), 50, c * (1 + 0.1 * Gauss()));
        }).ToList();
        var stats = TrialStatistics.Compute(samples, 1.0)!;
        return (ToTrial(id, kind, stats), stats);
    }

    [Test]
    public void Effective_frames_follow_the_lag_one_autocorrelation()
    {
        TrialJudge.EffectiveFrames(100, 0.7).Should().BeApproximately(100 * 0.51 / 1.49, 1e-9);
        TrialJudge.EffectiveFrames(100, -0.4).Should().Be(100, "negative autocorrelation is clamped to 0");
        TrialJudge.EffectiveFrames(100, 0.99).Should().BeApproximately(100 * (1 - 0.9025) / 1.9025, 1e-9, "clamped to 0.95");
        TrialJudge.LagOneAutocorrelation([1, -1, 1, -1, 1, -1]).Should().BeLessThan(-0.5);
        var rng = new Random(3);
        TrialJudge.LagOneAutocorrelation(Ar1(5000, 0.7, 1.0, rng)).Should().BeApproximately(0.7, 0.05);

        // conditionsChanged uses the effective frames too: 33 % with strongly correlated 200-frame trials is noise
        CoachTrial T(string id, string kind, double rms) => new() { Id = id, Kind = kind, State = CoachTrialStates.Done, RmsTotalArcsec = rms, Frames = 200 };
        TrialJudge.Judge([T("A", "current", 0.30), T("A2", "currentRepeat", 0.40)]).ConditionsChanged.Should().BeTrue();
        TrialJudge.Judge([T("A", "current", 0.30), T("A2", "currentRepeat", 0.40)],
            new Dictionary<string, double> { ["A"] = TrialJudge.EffectiveFrames(200, 0.9), ["A2"] = TrialJudge.EffectiveFrames(200, 0.9) })
            .ConditionsChanged.Should().BeFalse();
    }

    private static List<double> Ar1(int n, double rho, double sigma, Random rng)
    {
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var list = new List<double>(n);
        double x = sigma * Gauss();
        for (int i = 0; i < n; i++)
        {
            list.Add(x);
            x = rho * x + Math.Sqrt(1 - rho * rho) * sigma * Gauss();
        }

        return list;
    }

    private static (CoachTrial Trial, TrialStatistics Stats) Ar1Trial(string id, string kind, int n, double rho, double sigma, Random rng)
    {
        var ra = Ar1(n, rho, sigma, rng);
        var dec = Ar1(n, rho, sigma, rng);
        var stats = TrialStatistics.Compute(Enumerable.Range(0, n).Select(i => new TrialSample(i * 2.0, ra[i], dec[i], 50)).ToList(), 1.0)!;
        return (ToTrial(id, kind, stats), stats);
    }

    private static CoachTrial ToTrial(string id, string kind, TrialStatistics s) => new()
    {
        Id = id,
        Kind = kind,
        State = CoachTrialStates.Done,
        Frames = s.Frames,
        RmsTotalArcsec = s.RmsTotalArcsec,
        RmsRaArcsec = s.RmsRaArcsec,
        RmsDecArcsec = s.RmsDecArcsec,
    };

    [Test]
    public void Report_grade_budget_and_ranking()
    {
        CoachReportBuilder.Grade(0.4, 1.0).Grade.Should().Be(CoachGrades.Excellent);
        CoachReportBuilder.Grade(0.6, 1.0).Grade.Should().Be(CoachGrades.Good);
        CoachReportBuilder.Grade(0.9, 1.0).Grade.Should().Be(CoachGrades.Fair);
        CoachReportBuilder.Grade(1.2, 1.0).Grade.Should().Be(CoachGrades.Poor);
        CoachReportBuilder.Grade(0.9, 2.0).Should().Be((CoachGrades.Excellent, 0.45));
        CoachReportBuilder.Grade(0.9, null).Should().Be((CoachGrades.Fair, 0.9), "no imaging scale: graded by arcsec");
        CoachReportBuilder.Grade(null, 1.0).Grade.Should().Be(CoachGrades.Unknown);

        var ts = T0.UtcDateTime;
        var findings = new List<CoachFinding>
        {
            CoachFindings.Create(CoachCodes.DriftPolarAlignment, CoachStepNames.Drift, CoachSeverities.Warning, ts, new() { ["arcmin"] = 7.0 }, 0.05),
            CoachFindings.Create(CoachCodes.ResponseDecBacklash, CoachStepNames.MountResponse, CoachSeverities.Warning, ts, new() { ["ms"] = 1500.0 }, 0.4),
            CoachFindings.Create(CoachCodes.ResponseRateMismatch, CoachStepNames.MountResponse, CoachSeverities.Warning, ts,
                new() { ["axis"] = "Dec", ["ratio"] = 0.6 }, qualifier: "Dec"),
            CoachFindings.Create(CoachCodes.DriftSeeing, CoachStepNames.Drift, CoachSeverities.Good, ts, new() { ["rmsArcsec"] = 0.5 }),
        };
        var (report, reportFindings) = CoachReportBuilder.Build(new CoachReportInput
        {
            Id = "r1",
            Timestamp = T0,
            PixelScale = 1.5,
            ImagingScale = 1.2,
            Findings = findings,
            Drift = new CoachDrift { SeeingTotalArcsec = 0.5 },
            Camera = new CoachCameraCheck { Recommended = new CoachCameraResult { Hfd = 2.355, Snr = 20 } },
            Trials =
            [
                new CoachTrial { Id = "A", Kind = CoachTrialKinds.Current, State = CoachTrialStates.Done, RmsTotalArcsec = 1.3, RmsRaArcsec = 1.0, RmsDecArcsec = 0.83 },
                new CoachTrial { Id = "B", Kind = CoachTrialKinds.Suggestion, State = CoachTrialStates.Done, RmsTotalArcsec = 1.1, RmsRaArcsec = 0.8, RmsDecArcsec = 0.75 },
            ],
        });

        report.GuidedSource.Should().Be(CoachGuidedSources.Trials);
        report.GuidedRmsArcsec.Should().Be(1.1);
        report.CentroidNoiseArcsec!.Value.Should().BeApproximately(Math.Sqrt(2) / 20 * 1.5, 1e-3);
        double mount = Math.Sqrt(1.1 * 1.1 - 0.25 - Math.Pow(Math.Sqrt(2) / 20 * 1.5, 2));
        report.MountArcsec!.Value.Should().BeApproximately(mount, 1e-3);
        report.Grade.Should().Be(CoachGrades.Fair, "1.1″ / 1.2″ per px = 0.92");
        report.GradeRatio!.Value.Should().BeApproximately(1.1 / 1.2, 1e-3);
        reportFindings.Should().ContainSingle(f => f.Code == CoachCodes.ReportMountLimited);
        report.Night.Should().NotBeNullOrEmpty();

        // rate mismatch impact estimated from the Dec RMS: |1 − 0.6| × 0.75 = 0.3
        report.Findings.Single(f => f.Code == CoachCodes.ResponseRateMismatch).ImpactArcsec.Should().BeApproximately(0.3, 1e-3);
        report.Actions.Should().Equal("response.decBacklash", "response.rateMismatch:Dec", "report.mountLimited", "drift.polarAlignment");
    }

    /// <param name="earlyFrames">After every second pulse the camera delivers a frame whose exposure began during the pulse
    /// (arriving 2 s after the previous one, showing half the move) before the regular one.</param>
    /// <param name="onFrame">Called after every regular frame (live status checks).</param>
    private static MountResponseResult Run(KinematicDecMount mount, MountResponseProcedure proc, bool earlyFrames = false,
        Action? onFrame = null)
    {
        var transform = new MountTransform(0, Math.PI / 2, mount.RaRate, mount.DecRate);
        double t = 0;
        int pulseCount = 0;
        IReadOnlyList<PulseCommand> pulses = [];
        for (int i = 0; i < 1000 && !proc.Completion.IsCompleted; i++)
        {
            if (pulses.Count > 0 && earlyFrames && pulseCount++ % 2 == 1)
            {
                var (x0, y0) = (mount.X, mount.Y);
                foreach (var p in pulses)
                {
                    mount.Apply(p);
                }

                proc.OnFrame(Frame(t + 2.0, (x0 + mount.X) / 2, (y0 + mount.Y) / 2)).Should().BeEmpty("a frame exposed during the pulse is not used");
                t += pulses.Sum(p => p.DurationMs) / 1000.0;
                pulses = [];
            }

            foreach (var p in pulses)
            {
                mount.Apply(p);
                t += p.DurationMs / 1000.0;
            }

            t += 2.0;
            mount.DriftTo(t);
            var (x, y) = mount.Observe();
            pulses = proc.OnFrame(Frame(t, x, y));
            onFrame?.Invoke();
        }

        proc.Completion.IsCompleted.Should().BeTrue("the procedure terminates");
        return proc.Completion.Result;

        CoachFrame Frame(double time, double x, double y) => new()
        {
            Time = T0.AddSeconds(time),
            StarFound = true,
            StarPosition = new GuidePoint(500 + x, 400 + y),
            MountOffset = new GuidePoint(x, y),
            MountPosition = new GuidePoint(x, y),
            Snr = 50,
            Transform = transform,
            FrameWidth = 1000,
            FrameHeight = 800,
            SearchRegion = 15,
            PixelScale = mount.PixelScale,
            ExposureMs = 2000,
            MaxRaDurationMs = 2500,
            MaxDecDurationMs = 2500,
        };
    }

    /// <summary>Kinematic mount in mount-axis px: Dec backlash (dead band in pulse ms), RA stiction and East efficiency, Dec drift.</summary>
    private sealed class KinematicDecMount(int seed = 11)
    {
        private readonly Random rng = new(seed);
        private double play; // motor position inside the dead band, 0..BacklashMs
        private double lastT;

        public double RaRate { get; } = 0.005;

        public double DecRate { get; } = 0.005;

        public double PixelScale { get; } = 1.5;

        public double BacklashMs { get; init; }

        public double RaStictionMs { get; init; }

        public double EastEfficiency { get; init; } = 1.0;

        public double SouthEfficiency { get; init; } = 1.0;

        public double DecDriftPxPerSec { get; init; }

        public double NoisePx { get; init; }

        /// <summary>
        /// Soft slack instead of the dead band (when &gt; 0): a reversal loses <see cref="SoftBaseMs"/> plus this fraction of the
        /// motor travel in the previous direction (up to <see cref="SoftMaxTravelMs"/>), so it loses more after a long move.
        /// </summary>
        public double SoftFraction { get; init; }

        public double SoftBaseMs { get; init; }

        public double SoftMaxTravelMs { get; init; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public void Apply(PulseCommand p)
        {
            if (SoftFraction > 0 && p.Direction is GuideDirection.North or GuideDirection.South)
            {
                ApplySoft(p);
                return;
            }

            switch (p.Direction)
            {
                case GuideDirection.East:
                    X += RaRate * EastEfficiency * Math.Max(0, p.DurationMs - RaStictionMs);
                    break;
                case GuideDirection.West:
                    X -= RaRate * Math.Max(0, p.DurationMs - RaStictionMs);
                    break;
                case GuideDirection.North:
                {
                    double take = Math.Min(p.DurationMs, BacklashMs - play);
                    play += take;
                    Y += DecRate * (p.DurationMs - take);
                    break;
                }

                default:
                {
                    // the motor turns slower South: the dead band takes longer to cross, the move is slower
                    double motorMs = p.DurationMs * SouthEfficiency;
                    double take = Math.Min(motorMs, play);
                    play -= take;
                    Y -= DecRate * (motorMs - take);
                    break;
                }
            }
        }

        private GuideDirection? softDirection;
        private double softTravel;
        private double softLoss;

        private void ApplySoft(PulseCommand p)
        {
            if (softDirection is { } last && last != p.Direction)
            {
                softLoss = SoftBaseMs + SoftFraction * Math.Min(softTravel, SoftMaxTravelMs);
                softTravel = 0;
            }

            softDirection = p.Direction;
            double motorMs = p.DurationMs * (p.Direction == GuideDirection.South ? SouthEfficiency : 1.0);
            double take = Math.Min(motorMs, softLoss);
            softLoss -= take;
            softTravel += motorMs;
            Y += (p.Direction == GuideDirection.North ? 1 : -1) * DecRate * (motorMs - take);
        }

        public void DriftTo(double t)
        {
            Y += DecDriftPxPerSec * (t - lastT);
            lastT = t;
        }

        public (double X, double Y) Observe() => (X + NoisePx * Gauss(), Y + NoisePx * Gauss());

        private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
    }
}
