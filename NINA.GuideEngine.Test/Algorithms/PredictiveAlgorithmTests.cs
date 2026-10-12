// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// The Predictive algorithm on one synthetic mount axis whose true error is known: it moves by drift, random wander and
/// periodic error, the measurement adds seeing, and the correction the algorithm asks for is applied (optionally with
/// a calibration error). Compared with PHD2's Hysteresis on the same noise.
/// </summary>
[TestFixture]
public class PredictiveAlgorithmTests
{
    private const double MinMovePx = 0.15;

    [Test]
    public void DoesNotChaseSeeing()
    {
        var plant = new Plant { SeeingPx = 0.5, WanderPx = 0.05 };
        double predictive = plant.Run(Predictive());
        double hysteresis = plant.Run(Hysteresis());
        TestContext.Out.WriteLine($"seeing-limited: predictive {predictive:F3} px, hysteresis {hysteresis:F3} px");
        predictive.Should().BeLessThan(hysteresis * 0.8);
    }

    [Test]
    public void FollowsAWanderingMount()
    {
        var plant = new Plant { SeeingPx = 0.1, WanderPx = 0.3 };
        double predictive = plant.Run(Predictive());
        double hysteresis = plant.Run(Hysteresis());
        TestContext.Out.WriteLine($"mount-limited: predictive {predictive:F3} px, hysteresis {hysteresis:F3} px");
        predictive.Should().BeLessThan(hysteresis * 1.05);
    }

    [Test]
    public void CancelsDriftWithoutLag()
    {
        var plant = new Plant { SeeingPx = 0.3, WanderPx = 0.05, DriftPxPerSec = 0.03 };
        var alg = Predictive();
        double predictive = plant.Run(alg);
        double predictiveMean = plant.LastMean;
        double hysteresis = plant.Run(Hysteresis());
        double hysteresisMean = plant.LastMean;
        TestContext.Out.WriteLine($"drift: predictive {predictive:F3} px (mean {predictiveMean:F3}), hysteresis {hysteresis:F3} px (mean {hysteresisMean:F3})");
        alg.DriftPxPerSec.Should().BeApproximately(0.03, 0.01);
        predictive.Should().BeLessThan(hysteresis);
        Math.Abs(predictiveMean).Should().BeLessThan(Math.Abs(hysteresisMean), "the drift is predicted, not chased");
        // with min move the error runs up to the min move before a correction: a sawtooth whose mean is at most half of it
        Math.Abs(predictiveMean).Should().BeLessThan(MinMovePx / 2 + 0.01);
    }

    [Test]
    public void TracksPeriodicError()
    {
        var plant = new Plant { SeeingPx = 0.3, WanderPx = 0.05, PeriodicPx = 3, PeriodSec = 480 };
        double predictive = plant.Run(Predictive());
        double hysteresis = plant.Run(Hysteresis());
        TestContext.Out.WriteLine($"periodic error: predictive {predictive:F3} px, hysteresis {hysteresis:F3} px");
        predictive.Should().BeLessThan(hysteresis * 1.05);
    }

    [Test]
    public void EstimatesSeeingAndWander()
    {
        var alg = Predictive();
        new Plant { SeeingPx = 0.4, WanderPx = 0.1 }.Run(alg);
        TestContext.Out.WriteLine($"estimated seeing {alg.SeeingPx:F3} px, wander {alg.WanderPx:F3} px, gain {alg.LastGain:F2}");
        alg.SeeingPx.Should().BeApproximately(0.4, 0.4 * 0.25);
        alg.WanderPx.Should().BeApproximately(0.1, 0.1 * 0.6);
    }

    [TestCase(0.6)]
    [TestCase(1.6)]
    public void StaysStableWithACalibrationError(double calibrationError)
    {
        var plant = new Plant { SeeingPx = 0.3, WanderPx = 0.1 };
        double exact = plant.Run(Predictive());
        var off = plant with { CalibrationError = calibrationError };
        double wrong = off.Run(Predictive());
        TestContext.Out.WriteLine($"calibration × {calibrationError}: {wrong:F3} px vs {exact:F3} px");
        wrong.Should().BeLessThan(exact * 2);
    }

    [Test]
    public void DitherRestartsThePositionButKeepsTheDrift()
    {
        var alg = Predictive();
        var plant = new Plant { SeeingPx = 0.2, WanderPx = 0.05, DriftPxPerSec = 0.02 };
        plant.Run(alg, frames: 400);
        double drift = alg.DriftPxPerSec;

        alg.GuidingDithered(5);
        var t = plant.Time;
        alg.Result(5.0, t).Should().BeApproximately(5.0 + drift * 2, 0.2, "the first frame after a dither is taken as it is");
        alg.CorrectionApplied(5.0 + drift * 2);
        alg.Result(0.0, t + TimeSpan.FromSeconds(2));
        alg.DriftPxPerSec.Should().BeApproximately(drift, 0.005, "a dither is not drift");
    }

    [Test]
    public void HasNoMinMoveUnlessSetExplicitly()
    {
        // the guider's smart default is for the PHD2 filters: the factory must not apply it
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Predictive, Core.GuideAxis.Ra, 0.25).MinMove.Should().Be(-1);
        var alg = new PredictiveAlgorithm();
        alg.Result(0.01, DateTimeOffset.UnixEpoch).Should().BeApproximately(0.01, 1e-9);
        alg.TrySetParam("minMove", 0.2).Should().BeTrue();
        alg.MinMove.Should().Be(0.2);
    }

    [Test]
    public void IgnoresOffsetsBelowMinMove()
    {
        var alg = Predictive();
        var t = DateTimeOffset.UnixEpoch;
        alg.Result(0.05, t).Should().Be(0);
        alg.Result(1.0, t + TimeSpan.FromSeconds(2)).Should().BeGreaterThan(0);
    }

    [Test]
    public void PaceScalesThePositionAndIsBounded()
    {
        var alg = Predictive();
        alg.SettingsSummary.Should().StartWith("Correction pace = 1.00, ");
        alg.TrySetParam("pace", 0.5).Should().BeTrue();
        alg.Result(2.0, DateTimeOffset.UnixEpoch).Should().BeApproximately(0.5 * 2.0, 1e-9, "the first frame is taken as it is");
        alg.TrySetParam("pace", 1.5).Should().BeFalse("correcting more than the error overshoots");
        alg.Pace.Should().Be(PredictiveAlgorithm.DefaultPace);
        alg.TrySetParam("pace", 0.05).Should().BeFalse();
        alg.Pace.Should().Be(PredictiveAlgorithm.DefaultPace);
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Predictive).Should().BeOfType<PredictiveAlgorithm>();
        GuideAlgorithmFactory.IsSupported(GuideAlgorithmKind.Predictive).Should().BeTrue();
    }

    [Test]
    public void PaceSlowsThePositionButNotTheDrift()
    {
        // both see the same frames and the same corrections, so they hold the same estimate: the paced one sends the
        // drift until the next frame in full and half of the position
        var full = new PredictiveAlgorithm();
        var paced = new PredictiveAlgorithm();
        paced.TrySetParam("pace", 0.5).Should().BeTrue();
        var rng = new Random(3);
        double error = 0, fullOut = 0, pacedOut = 0;
        for (int k = 0; k < 300; k++)
        {
            var t = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2 * k);
            error += 0.02 * 2;
            double measured = error + 0.1 * (rng.NextDouble() - 0.5);
            fullOut = full.Result(measured, t);
            pacedOut = paced.Result(measured, t);
            full.CorrectionApplied(fullOut);
            paced.CorrectionApplied(fullOut);
            error -= fullOut;
        }

        paced.DriftPxPerSec.Should().Be(full.DriftPxPerSec);
        full.DriftPxPerSec.Should().BeApproximately(0.02, 0.01);
        double motion = full.DriftPxPerSec * 2;
        pacedOut.Should().BeApproximately(0.5 * (fullOut - motion) + motion, 1e-9);
    }

    [TestCase(1.0)]
    [TestCase(0.5)]
    public void PaceTakesABumpOutOverAFewFramesWithoutOvershoot(double pace)
    {
        var alg = new PredictiveAlgorithm();
        alg.TrySetParam("pace", pace).Should().BeTrue();
        var rng = new Random(11);
        double position = 0;
        var after = new List<double>();
        for (int k = 0; k < 110; k++)
        {
            if (k == 100)
            {
                position += 1.0;
            }

            double measured = position + 0.01 * (rng.NextDouble() - 0.5);
            if (k >= 100)
            {
                after.Add(measured);
            }

            double correction = alg.Result(measured, DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2 * k));
            alg.CorrectionApplied(correction);
            position -= correction;
        }

        after[0].Should().BeApproximately(1.0, 0.02, "the bump shows");
        after.Should().OnlyContain(z => z > -0.05, "the error doesn't swing past zero");
        if (pace == 1.0)
        {
            after[1].Should().BeLessThan(0.05, "all of it at once");
        }
        else
        {
            after[1].Should().BeApproximately(0.5, 0.05, "half of it in the first frame");
            after[2].Should().BeApproximately(0.25, 0.05);
            after[4].Should().BeLessThan(0.1, "most of it within a few frames");
        }
    }

    [Test]
    public void ReportsLearningProgressThenStaysAdapted()
    {
        var alg = Predictive();
        alg.State.Phase.Should().Be(PredictivePhase.Learning);
        alg.State.Progress.Should().Be(0);

        var phases = new List<PredictivePhase>();
        int fitNotes = 0;
        var plant = new Plant
        {
            SeeingPx = 0.3, WanderPx = 0.05,
            OnFrame = k =>
            {
                phases.Add(alg.State.Phase);
                fitNotes += alg.TakeNotes().Count(n => n.Kind == PredictiveNoteKind.Fit);
            },
        };
        plant.Run(alg, frames: 61);
        alg.State.Phase.Should().Be(PredictivePhase.Learning);
        alg.State.Progress.Should().BeApproximately(0.5, 0.01, "the first frame starts the position, the next 60 are learned");

        phases.Clear();
        plant.Run(alg = Predictive(), frames: 3000);
        phases.IndexOf(PredictivePhase.Adapted).Should().Be(PredictiveAlgorithm.ScoreWindowFrames);
        phases.Skip(PredictiveAlgorithm.ScoreWindowFrames).Should().OnlyContain(p => p == PredictivePhase.Adapted, "once learned it stays learned");
        alg.State.Progress.Should().Be(1);
        alg.State.Fit.Should().BeInRange(0.5, 1.6);
        fitNotes.Should().Be(0, "nothing changes: no false alarms in the debug log");
    }

    [TestCase(0.5, 480)]
    [TestCase(1.0, 240)]
    [TestCase(2.0, 120)]
    [TestCase(8.0, 120)]
    public void TheScoreMemorySpans240SecondsAndAtLeast120Frames(double intervalSec, int frames)
    {
        var alg = Predictive();
        new Plant { SeeingPx = 0.2, WanderPx = 0.05, IntervalSec = intervalSec }.Run(alg, frames: 50);
        alg.ScoreMemoryFrames.Should().BeApproximately(frames, 1e-9);
    }

    [Test]
    public void AtTwoSecondsAndLongerTheScoresForgetExactlyAsBefore()
    {
        // 120 frames of memory at 2.1 s and more (2 s exposure plus download): λ is the constant the algorithm was tuned
        // with, to the bit
        foreach (double interval in new[] { 2.1, 2.35, 4.0 })
        {
            var alg = Predictive();
            new Plant { SeeingPx = 0.2, WanderPx = 0.05, IntervalSec = interval }.Run(alg, frames: 20);
            (1.0 - 1.0 / alg.ScoreMemoryFrames).Should().Be(1.0 - 1.0 / 120);
        }
    }

    [Test]
    public void AtShortFrameIntervalsTheLearningTakes240SecondsAndStaysLearnedWhenTheExposureDrops()
    {
        // 0.5 s frames: adapted once the learned frames span 240 s (480 frames), not after 120 frames
        var alg = Predictive();
        var states = new List<PredictiveState>();
        new Plant { SeeingPx = 0.3, WanderPx = 0.05, IntervalSec = 0.5, OnFrame = _ => states.Add(alg.State) }.Run(alg, frames: 700);
        var first = states.First(s => s.Phase == PredictivePhase.Adapted);
        first.FramesLearned.Should().Be(480);
        states.SkipWhile(s => s.Phase == PredictivePhase.Learning).Should().OnlyContain(s => s.Phase == PredictivePhase.Adapted);
        states.Last(s => s.Phase == PredictivePhase.Learning).Progress.Should().BeApproximately(479.0 / 480, 1e-9);

        // learned at 2 s, then the exposure drops to 0.5 s: the memory grows to 480 frames, the phase stays
        alg = Predictive();
        new Plant { SeeingPx = 0.3, WanderPx = 0.05, IntervalSec = 2 }.Run(alg, frames: 200);
        alg.State.Phase.Should().Be(PredictivePhase.Adapted);
        var phases = new List<PredictivePhase>();
        new Plant { SeeingPx = 0.3, WanderPx = 0.05, IntervalSec = 0.5, Seed = 8, OnFrame = _ => phases.Add(alg.State.Phase) }.Run(alg, frames: 200);
        phases.Should().OnlyContain(p => p == PredictivePhase.Adapted);
        alg.ScoreMemoryFrames.Should().BeApproximately(480, 1e-6, "the frame interval follows within a few frames");
    }

    [Test]
    public void WithoutTimeWindowsTheMemoriesAreFramesOnly()
    {
        // the comparison setting: today's behaviour before the windows were in time
        var alg = Predictive();
        alg.TimeWindows = false;
        var phases = new List<PredictivePhase>();
        new Plant { SeeingPx = 0.3, WanderPx = 0.05, IntervalSec = 0.5, OnFrame = _ => phases.Add(alg.State.Phase) }.Run(alg, frames: 300);
        alg.ScoreMemoryFrames.Should().Be(PredictiveAlgorithm.ScoreWindowFrames);
        alg.UsualSigmaWindow.Should().Be(3 * PredictiveAlgorithm.ScoreWindowFrames);
        phases.IndexOf(PredictivePhase.Adapted).Should().Be(PredictiveAlgorithm.ScoreWindowFrames);
    }

    [TestCase(0.1, 4000)]
    [TestCase(0.5, 1440)]
    [TestCase(2.0, 360)]
    [TestCase(8.0, 360)]
    public void TheUsualNoiseIsTheMedianOf12MinutesAndAtLeast360Frames(double intervalSec, int frames)
    {
        var alg = Predictive();
        new Plant { SeeingPx = 0.2, WanderPx = 0.05, IntervalSec = intervalSec }.Run(alg, frames: 50);
        alg.UsualSigmaWindow.Should().Be(frames);
    }

    [Test]
    public void AtShortFrameIntervalsAFourMinuteCloudDoesNotBecomeTheUsualNoise()
    {
        // 480 cloudy frames: 4 minutes at 0.5 s, but 16 minutes at 2 s. At 0.5 s the median spans 1440 frames and the
        // cloud stays unusual throughout; at 2 s it spans 360 frames, and after 180 cloudy ones the cloud is the new normal
        static List<double> Factors(double intervalSec)
        {
            const int cloud = 1500;
            var alg = Predictive();
            var factors = new List<double>();
            new Plant
            {
                SeeingPx = 0.1, WanderPx = 0.03, IntervalSec = intervalSec, CentroidSigmaPx = k => k >= cloud ? 0.3 : 0.03,
                OnFrame = k =>
                {
                    if (k >= cloud)
                    {
                        factors.Add(alg.LastNoiseFactor);
                    }
                },
            }.Run(alg, frames: cloud + 480);
            return factors;
        }

        Factors(0.5).Should().OnlyContain(f => f > 2, "a cloud over a third of the window is no new normal");
        Factors(2.0).Skip(200).Should().OnlyContain(f => f == 1, "the frame window takes the cloud as the usual noise");
    }

    [TestCase(0.2, 0.6)]
    [TestCase(0.6, 0.2)]
    public void NotesAChangedSkyInTheDebugLog(double seeingBefore, double seeingAfter)
    {
        var alg = Predictive();
        var fitNotes = new List<int>();
        const int change = 1000;
        new Plant
        {
            SeeingPx = seeingBefore, SeeingPxAfter = seeingAfter, ChangeFrame = change, WanderPx = 0.05,
            OnFrame = k =>
            {
                if (alg.TakeNotes().Any(n => n.Kind == PredictiveNoteKind.Fit))
                {
                    fitNotes.Add(k);
                }
            },
        }.Run(alg, frames: 1600);

        TestContext.Out.WriteLine($"seeing {seeingBefore} -> {seeingAfter} px at frame {change}: fit notes at frames {string.Join(", ", fitNotes)}");
        fitNotes.Should().HaveCount(2, "one when the error leaves its long-run level, one when it is back");
        fitNotes[0].Should().BeInRange(change, change + 50);
        fitNotes[1].Should().BeInRange(fitNotes[0] + 1, change + 300);
        alg.State.Phase.Should().Be(PredictivePhase.Adapted, "the change is a note, not a phase");
        alg.SeeingPx.Should().BeApproximately(seeingAfter, seeingAfter * 0.25);
    }

    [Test]
    public void LearnsNothingWhileSettling()
    {
        // a dither with a 30 % calibration error: the settling corrections miss by a lot more than the seeing
        var alg = Predictive();
        PredictiveState? before = null;
        PredictiveState? settled = null;
        var plant = new Plant
        {
            SeeingPx = 0.2, WanderPx = 0.03, DriftPxPerSec = 0.01, CalibrationError = 1.3, DitherFrame = 300, DitherPx = 5,
            OnFrame = k =>
            {
                if (k == 299)
                {
                    before = alg.State;
                }
                else if (k == 330)
                {
                    settled = alg.State;
                }

                alg.Settling = k >= 299 && k < 330; // frames 300 … 330
            },
        };
        plant.Run(alg, frames: 400);

        settled!.FramesLearned.Should().Be(before!.FramesLearned, "settling frames are not learned");
        settled.SeeingPx.Should().Be(before.SeeingPx);
        settled.DriftPxPerSec.Should().Be(before.DriftPxPerSec, "the drift is held");
        settled.Takeovers.Should().Be(before.Takeovers);
        settled.Fit.Should().Be(before.Fit);
        alg.State.FramesLearned.Should().Be(before.FramesLearned + 69, "learning goes on after settling (frames 331 … 399)");
    }

    [Test]
    public void NotesWhatHappensForTheLog()
    {
        var alg = Predictive();
        new Plant { SeeingPx = 0.3, WanderPx = 0.1 }.Run(alg, frames: 301);
        var notes = alg.TakeNotes();
        alg.TakeNotes().Should().BeEmpty("taken notes are gone");

        notes[0].Kind.Should().Be(PredictiveNoteKind.Restart);
        notes[0].Message.Should().Contain("first frame");
        notes.Should().ContainSingle(n => n.Kind == PredictiveNoteKind.Learned);
        notes.Count(n => n.Kind == PredictiveNoteKind.Summary).Should().Be(5, "every 50 frames, counted anew from the end of the learning: 50, 100, 170, 220, 270");
        notes.Where(n => n.Kind == PredictiveNoteKind.Takeover).Should().HaveCount(alg.State.Takeovers).And.OnlyContain(n => n.Message.Contains("->"));
        notes.Should().OnlyContain(n => !n.Message.Contains("NaN") && !n.Message.Contains('∞'));

        alg.GuidingDithered(3);
        alg.Result(3, DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(700));
        alg.TakeNotes().Should().ContainSingle(n => n.Kind == PredictiveNoteKind.Restart).Which.Message.Should().Contain("dither");

        alg.Reset();
        alg.TakeNotes().Should().ContainSingle(n => n.Kind == PredictiveNoteKind.Reset);
        alg.State.Should().BeEquivalentTo(new PredictiveState { Gain = 0, WanderRatio = 0.1, DriftRatio = 1e-3 });
    }

    [Test]
    public void FramesNoNoisierThanUsualAreTreatedAsBefore()
    {
        // frame weighting: the same run with the centroid σ reported but not added: frames within the usual scatter of σ, better ones
        // and unknown ones change nothing (a faint star: its centroid noise is about the seeing's)
        var plant = new Plant { SeeingPx = 0.05, WanderPx = 0.03, DriftPxPerSec = 0.01, PeriodicPx = 1 };
        var plain = Predictive();
        double without = plant.Run(plain);
        var reported = Predictive();
        var steady = plant with { CentroidSigmaPx = k => k % 7 == 0 ? double.NaN : 0.04 * (1 + 0.3 * Math.Sin(k)), CentroidNoise = false };
        double with = steady.Run(reported);

        with.Should().Be(without);
        steady.LastErrors.Should().Equal(plant.LastErrors);
        reported.SeeingPx.Should().Be(plain.SeeingPx);
        reported.UsualSigmaPx.Should().BeApproximately(0.04, 0.003);
        reported.LastNoiseFactor.Should().Be(1);
    }

    [Test]
    public void ANoisierFrameIsTrustedLessNeverMore()
    {
        var alg = Predictive();
        var plant = new Plant { SeeingPx = 0.2, WanderPx = 0.05, CentroidSigmaPx = _ => 0.05 };
        plant.Run(alg, frames: 400);
        alg.UsualSigmaPx.Should().Be(0.05);
        var t = plant.Time;
        double Frame(double? sigma)
        {
            alg.MeasurementSigmaPx = sigma;
            t += TimeSpan.FromSeconds(plant.IntervalSec);
            alg.CorrectionApplied(alg.Result(0, t));
            return alg.LastNoiseFactor;
        }

        Frame(0.05).Should().Be(1, "a usual frame");
        double usualGain = alg.LastGain;
        Frame(0.02).Should().Be(1, "a better frame is not trusted more than the learned level");
        Frame(0.1).Should().Be(1, "twice the usual σ, but next to the seeing the variance grows by less than a quarter");
        Frame(null).Should().Be(1, "unknown");
        Frame(double.NaN).Should().Be(1);
        Frame(0).Should().Be(1);

        // the frame's centroid variance beyond the usual band adds to the learned measurement variance r
        double r = alg.SeeingPx * alg.SeeingPx;
        double band = Math.Max(1.5 * 1.5 * 0.05 * 0.05, 0.05 * 0.05 + r / 4);
        double noisy = Frame(0.3);
        TestContext.Out.WriteLine($"seeing σ {alg.SeeingPx:F3} px: variance × {noisy:F2}, gain {alg.LastGain:F2} instead of {usualGain:F2}");
        noisy.Should().BeApproximately(1 + (0.3 * 0.3 - band) / r, 1e-9);
        alg.LastGain.Should().BeLessThan(0.6 * usualGain);
        Frame(5).Should().Be(100, "capped");

        alg.FrameWeighting = false;
        Frame(0.3).Should().Be(1, "switched off");

        alg.Reset();
        alg.UsualSigmaPx.Should().Be(0);
        alg.LastNoiseFactor.Should().Be(1);
    }

    [Test]
    public void CloudyStretchesAreTrustedLessAndDoNotTeachTheSeeing()
    {
        // 30 of every 150 frames under thin clouds: the faint star's centroid gets 20× noisier
        static bool Cloudy(int k) => k % 150 is >= 100 and < 130;
        var plant = new Plant { SeeingPx = 0.15, WanderPx = 0.05, DriftPxPerSec = 0.01, CentroidSigmaPx = k => Cloudy(k) ? 0.6 : 0.03 };
        var on = Predictive();
        double rmsOn = plant.Run(on);
        var errorsOn = plant.LastErrors;
        var off = Predictive();
        off.FrameWeighting = false;
        double rmsOff = plant.Run(off);
        var errorsOff = plant.LastErrors;

        // during the clouds and 10 frames after
        static double CloudyRms(IReadOnlyList<double> e) =>
            Math.Sqrt(Enumerable.Range(200, e.Count - 200).Where(k => Cloudy(k) || Cloudy(k - 10)).Average(k => e[k] * e[k]));
        double cloudyOn = CloudyRms(errorsOn), cloudyOff = CloudyRms(errorsOff);
        TestContext.Out.WriteLine($"true RMS {rmsOn:F3} px with frame weighting, {rmsOff:F3} px without; in and after the clouds {cloudyOn:F3} / {cloudyOff:F3} px; " +
            $"seeing learned {on.SeeingPx:F3} / {off.SeeingPx:F3} px");

        on.SeeingPx.Should().BeApproximately(Math.Sqrt(0.15 * 0.15 + 0.03 * 0.03), 0.03, "the cloudy frames don't count as seeing");
        off.SeeingPx.Should().BeGreaterThan(1.5 * on.SeeingPx);
        on.UsualSigmaPx.Should().Be(0.03);
        cloudyOn.Should().BeLessThan(0.8 * cloudyOff);
        rmsOn.Should().BeLessThan(rmsOff);
    }

    [Test]
    public void ALongCloudIsTrustedLessUntilItBecomesTheNewNormal()
    {
        // 5 minutes of thin clouds (frames 500 … 649), later a star that stays 10× noisier (from frame 1000 on)
        var alg = Predictive();
        var factors = new List<double>();
        var seeing = new List<double>();
        var levelNotes = new List<(int Frame, string Message)>();
        new Plant
        {
            SeeingPx = 0.1, WanderPx = 0.03, CentroidSigmaPx = k => k is >= 500 and < 650 || k >= 1000 ? 0.3 : 0.03,
            OnFrame = k =>
            {
                factors.Add(alg.LastNoiseFactor);
                seeing.Add(alg.SeeingPx);
                levelNotes.AddRange(alg.TakeNotes().Where(n => n.Message.StartsWith("new usual", StringComparison.Ordinal)).Select(n => (k, n.Message)));
            },
        }.Run(alg, frames: 1600);

        factors.Skip(500).Take(150).Should().OnlyContain(f => f >= 2, "the whole cloud is trusted less");
        seeing[649].Should().BeLessThan(1.3 * seeing[499], "and teaches the seeing little");
        factors.Skip(650).Take(350).Should().OnlyContain(f => f == 1);
        levelNotes.Should().OnlyContain(n => n.Frame >= 1000, "the cloud was no new usual level");

        // once it has been noisier for more than half of the usual level's memory (180 frames) that is the usual noise: its
        // frames count as usual, and at the same frame the seeing estimate takes over the difference to the level it was
        // learned with (the frame before, the median of an even count is halfway there)
        int normal = factors.FindIndex(1000, f => f == 1);
        TestContext.Out.WriteLine($"new normal after {normal - 1000} frames; seeing {seeing[normal - 2]:F3} px before, {seeing[normal]:F3} px then, " +
            $"{seeing[^1]:F3} px at the end; {string.Join("; ", levelNotes.Select(n => $"{n.Frame}: {n.Message}"))}");
        normal.Should().BeInRange(1170, 1200);
        seeing[normal - 2].Should().BeLessThan(1.3 * seeing[999], "until then the extra noise was the frames' own");
        seeing[normal].Should().BeApproximately(Math.Sqrt(0.1 * 0.1 + 0.3 * 0.3), 0.05, "taken over at once, not learned over the next hundred frames");
        levelNotes.Select(n => n.Frame).Should().OnlyContain(f => f == normal - 1 || f == normal);
        levelNotes[^1].Message.Should().StartWith("new usual centroid σ 0.300 px");
        seeing[^1].Should().BeApproximately(Math.Sqrt(0.1 * 0.1 + 0.3 * 0.3), 0.05);
    }

    [Test]
    public void ANewUsualNoiseLevelIsTakenOverWithoutATransient()
    {
        // a star that stays 10× noisier from frame 1000 on (fainter star after a slew, shorter exposure, lasting haze): the
        // 120 frames after its noise became the usual level, 8 seeds, against the same runs without frame weighting
        const int change = 1000;
        double sumWith = 0, sumWithout = 0;
        int jumpsWith = 0, jumpsWithout = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            var (rmsOn, switchFrame, jumps) = Run(seed, frameWeighting: true, switchFrame: null);
            var (rmsOff, _, jumpsOff) = Run(seed, frameWeighting: false, switchFrame);
            TestContext.Out.WriteLine($"seed {seed}: new normal at frame {switchFrame}, true RMS {rmsOn:F3} px with frame weighting, {rmsOff:F3} px without; jumps {jumps} / {jumpsOff}");
            sumWith += rmsOn;
            sumWithout += rmsOff;
            jumpsWith += jumps;
            jumpsWithout += jumpsOff;
        }

        TestContext.Out.WriteLine($"mean true RMS {sumWith / 8:F3} px with frame weighting, {sumWithout / 8:F3} px without; jumps {jumpsWith} / {jumpsWithout}");
        sumWith.Should().BeLessThanOrEqualTo(sumWithout);
        jumpsWith.Should().BeLessThanOrEqualTo(jumpsWithout, "the frames of the new usual level are not trusted more than they deserve");

        static (double Rms, int SwitchFrame, int Jumps) Run(int seed, bool frameWeighting, int? switchFrame)
        {
            var alg = Predictive();
            alg.FrameWeighting = frameWeighting;
            var factors = new List<double>();
            var jumpFrames = new List<int>();
            var plant = new Plant
            {
                SeeingPx = 0.1, WanderPx = 0.03, Seed = seed, CentroidSigmaPx = k => k >= change ? 0.3 : 0.03,
                OnFrame = k =>
                {
                    factors.Add(alg.LastNoiseFactor);
                    if (alg.TakeNotes().Any(n => n.Kind == PredictiveNoteKind.Jump))
                    {
                        jumpFrames.Add(k);
                    }
                },
            };
            plant.Run(alg, frames: change + 400);
            int first = switchFrame ?? factors.FindIndex(change, f => f == 1);
            var window = Enumerable.Range(first, PredictiveAlgorithm.ScoreWindowFrames).ToList();
            return (Math.Sqrt(window.Average(k => plant.LastErrors[k] * plant.LastErrors[k])), first, jumpFrames.Count(window.Contains));
        }
    }

    [Test]
    public void NotesFramesTrustedLessForTheDebugLog()
    {
        var alg = Predictive();
        var notes = new List<(int Frame, PredictiveNote Note)>();

        // 10 noisy frames in every 40: the stretches come faster than they may be noted
        new Plant
        {
            SeeingPx = 0.15, WanderPx = 0.05, CentroidSigmaPx = k => k % 40 >= 30 ? 0.5 : 0.03,
            OnFrame = k => notes.AddRange(alg.TakeNotes().Select(n => (k, n))),
        }.Run(alg, frames: 1000);

        var noise = notes.Where(n => n.Note.Kind == PredictiveNoteKind.Noise).ToList();
        var starts = noise.Where(n => n.Note.Message.StartsWith("frames trusted less", StringComparison.Ordinal)).ToList();
        var ends = noise.Where(n => n.Note.Message.StartsWith("measurement noise back to normal", StringComparison.Ordinal)).ToList();
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, noise.Take(4).Select(n => $"{n.Frame}: {n.Note.Message}")));
        starts.Should().HaveCountGreaterThan(8);
        starts.Zip(starts.Skip(1)).Should().OnlyContain(p => p.Second.Frame - p.First.Frame >= PredictiveAlgorithm.SummaryFrames, "rate-limited");
        starts[0].Note.Message.Should().Contain("centroid σ 0.500 px, usually 0.030 px");
        ends.Count.Should().BeInRange(starts.Count - 1, starts.Count, "each noted stretch is noted again when it ends (the last one may not be over)");
        starts.Zip(ends).Should().OnlyContain(p => p.Second.Frame > p.First.Frame && p.Second.Frame - p.First.Frame < 40);
        ends[0].Note.Message.Should().Contain("10 of the last");
        notes.Where(n => n.Note.Kind == PredictiveNoteKind.Summary).Should().Contain(n => n.Note.Message.Contains("frames trusted less"));
    }

    private static PredictiveAlgorithm Predictive()
    {
        var a = new PredictiveAlgorithm { MinMove = MinMovePx };
        return a;
    }

    private static HysteresisAlgorithm Hysteresis() => new() { MinMove = MinMovePx };

    /// <summary>One mount axis, frames every <see cref="IntervalSec"/>; <see cref="Run"/> returns the true RMS.</summary>
    private sealed record Plant
    {
        public double SeeingPx { get; init; }

        public double WanderPx { get; init; }

        public double DriftPxPerSec { get; init; }

        public double PeriodicPx { get; init; }

        public double PeriodSec { get; init; } = 480;

        /// <summary>Applied correction = requested × this (a wrong calibration rate).</summary>
        public double CalibrationError { get; init; } = 1;

        public double IntervalSec { get; init; } = 2;

        public int Seed { get; init; } = 7;

        /// <summary>Seeing from <see cref="ChangeFrame"/> on (null = unchanged).</summary>
        public double? SeeingPxAfter { get; init; }

        public int ChangeFrame { get; init; } = int.MaxValue;

        /// <summary>Called after each frame with its index.</summary>
        public Action<int>? OnFrame { get; init; }

        /// <summary>Frame at which the error jumps by <see cref="DitherPx"/> and the algorithm hears of a dither (−1 = none).</summary>
        public int DitherFrame { get; init; } = -1;

        public double DitherPx { get; init; }

        /// <summary>Centroid σ of frame k (px), reported to a Predictive algorithm (null = none reported).</summary>
        public Func<int, double>? CentroidSigmaPx { get; init; }

        /// <summary>Adds the <see cref="CentroidSigmaPx"/> noise to the measurement (false: only report it).</summary>
        public bool CentroidNoise { get; init; } = true;

        public double LastMean { get; private set; }

        /// <summary>True error at each frame's measurement in the last run.</summary>
        public IReadOnlyList<double> LastErrors { get; private set; } = [];

        public DateTimeOffset Time { get; private set; }

        /// <summary>True RMS of the error at the measurement times after the first 200 frames.</summary>
        public double Run(IGuideAlgorithm alg, int frames = 3000)
        {
            var rng = new Random(Seed);
            double error = 0;
            double sum = 0;
            double sumSq = 0;
            int n = 0;
            var errors = new List<double>(frames);
            for (int k = 0; k < frames; k++)
            {
                double t = k * IntervalSec;
                double pe = PeriodicPx * Math.Sin(2 * Math.PI * t / PeriodSec);
                double pePrev = PeriodicPx * Math.Sin(2 * Math.PI * (t - IntervalSec) / PeriodSec);
                error += DriftPxPerSec * IntervalSec + WanderPx * Gauss(rng) + (k > 0 ? pe - pePrev : 0);
                errors.Add(error);
                if (k >= 200)
                {
                    sum += error;
                    sumSq += error * error;
                    n++;
                }

                if (k == DitherFrame)
                {
                    error += DitherPx;
                    alg.GuidingDithered(DitherPx);
                }

                Time = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(t);
                double seeing = k >= ChangeFrame && SeeingPxAfter is { } after ? after : SeeingPx;
                double measured = error + seeing * Gauss(rng);
                if (CentroidSigmaPx is { } centroid)
                {
                    double sigma = centroid(k);
                    measured += CentroidNoise ? sigma * Gauss(rng) : 0;
                    if (alg is PredictiveAlgorithm predictive)
                    {
                        predictive.MeasurementSigmaPx = sigma;
                    }
                }

                double correction = alg.Result(measured, Time);
                alg.CorrectionApplied(correction);
                error -= correction * CalibrationError;
                OnFrame?.Invoke(k);
            }

            LastErrors = errors;
            LastMean = sum / n;
            return Math.Sqrt(sumSq / n);
        }

        private static double Gauss(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
