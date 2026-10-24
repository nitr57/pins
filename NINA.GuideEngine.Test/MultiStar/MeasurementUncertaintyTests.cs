// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.MultiStar;

/// <summary>The per-frame centroid uncertainty: the model, its cap, the multi-star combination and its truth.</summary>
[TestFixture]
public class MeasurementUncertaintyTests
{
    [Test]
    public void Grows_with_the_star_size_and_the_noise()
    {
        double previous = 0;
        foreach (double hfd in new[] { 1.5, 2.0, 3.0, 5.0, 8.0 })
        {
            double sigma = Sigma(hfd, 20);
            sigma.Should().BeGreaterThan(previous);
            previous = sigma;
        }

        previous = double.MaxValue;
        foreach (double snr in new[] { 3.0, 5, 10, 30, 100, 300 })
        {
            double sigma = Sigma(3, snr);
            sigma.Should().BeLessThan(previous);
            previous = sigma;
        }

        // bright: the Gaussian centroid term HFD / (2.355 · SNR); faint: PHD2's threshold adds as much again at SNR 24
        MeasurementUncertainty.StarSigmaPx(2.355, 1000).Should().BeApproximately(0.001, 0.0001);
        MeasurementUncertainty.StarSigmaPx(3, 24).Should().BeApproximately(2 * 3 / 24.0 * 0.42, 0.01);
    }

    [TestCase(0.0, 10.0)]
    [TestCase(3.0, 0.0)]
    [TestCase(3.0, -5.0)]
    [TestCase(double.NaN, 10.0)]
    [TestCase(3.0, double.NaN)]
    [TestCase(double.PositiveInfinity, 10.0)]
    public void Without_SNR_or_size_it_is_unknown(double hfd, double snr)
    {
        MeasurementUncertainty.StarSigmaPx(hfd, snr).Should().BeNull();
        MeasurementUncertainty.StarSigmaPx(hfd, snr, StarFindMode.Peak).Should().BeNull();
    }

    [Test]
    public void A_star_barely_above_the_noise_is_capped()
    {
        MeasurementUncertainty.StarSigmaPx(40, 0.5).Should().Be(MeasurementUncertainty.MaxSigmaPx);
    }

    [Test]
    public void Peak_mode_has_the_pixel_quantisation_as_a_floor()
    {
        MeasurementUncertainty.StarSigmaPx(3, 200, StarFindMode.Peak).Should().BeApproximately(1 / Math.Sqrt(12), 1e-12);
        MeasurementUncertainty.StarSigmaPx(3, 5, StarFindMode.Peak).Should().Be(Sigma(3, 5));
    }

    [Test]
    public void A_frame_combines_the_stars_it_guided_on()
    {
        var primary = Snapshot(3.0, 20);
        double sp = Sigma(3.0, 20);
        double s1 = Sigma(2.5, 40);
        double s2 = Sigma(3.5, 10);
        TrackedStarInfo[] stars =
        [
            Info(0, primary, TrackedStarStatus.Primary, 1),
            Info(1, Snapshot(2.5, 40), TrackedStarStatus.Used, 2.0),
            Info(2, Snapshot(3.5, 10), TrackedStarStatus.Used, 0.5),
            Info(3, Snapshot(3.0, 50), TrackedStarStatus.Miss, 0),
            Info(4, Snapshot(3.0, 50), TrackedStarStatus.Lost, 0),
        ];

        // the primary alone
        var single = Result(TrackerOutcome.Found, primary, stars, refined: false);
        MeasurementUncertainty.FrameSigmaPx(single).Should().Be(sp);

        // the weighted mean of the primary and the used secondaries (weight 1, SNR ratio): √(Σ w²σ²) / Σ w
        double expected = Math.Sqrt(sp * sp + 4 * s1 * s1 + 0.25 * s2 * s2) / 3.5;
        var refined = Result(TrackerOutcome.Found, primary, stars, refined: true);
        MeasurementUncertainty.FrameSigmaPx(refined).Should().BeApproximately(expected, 1e-12);
        expected.Should().BeLessThan(sp, "more stars measure better");

        // estimated from secondaries: the median of their estimates
        TrackedStarInfo[] fallback =
        [
            Info(0, primary, TrackedStarStatus.PrimaryLost, 0),
            Info(1, Snapshot(2.5, 40), TrackedStarStatus.FallbackUsed, 0),
            Info(2, Snapshot(3.5, 10), TrackedStarStatus.FallbackUsed, 0),
            Info(3, Snapshot(3.0, 50), TrackedStarStatus.FallbackUsed, 0),
            Info(4, Snapshot(3.0, 5), TrackedStarStatus.FallbackRejected, 0),
        ];
        double s3 = Sigma(3.0, 50);
        var estimated = Result(TrackerOutcome.Estimated, primary, fallback, refined: false);
        MeasurementUncertainty.FrameSigmaPx(estimated).Should().BeApproximately(Math.Sqrt(Math.PI / 2) * Math.Sqrt(s1 * s1 + s2 * s2 + s3 * s3) / 3, 1e-12);

        // from one or two stars the median is their mean
        var fromTwo = Result(TrackerOutcome.Estimated, primary, fallback.Where(s => s.Index != 3).ToArray(), refined: false);
        MeasurementUncertainty.FrameSigmaPx(fromTwo).Should().BeApproximately(Math.Sqrt(s1 * s1 + s2 * s2) / 2, 1e-12);
        var fromOne = Result(TrackerOutcome.Estimated, primary, fallback.Where(s => s.Index is 0 or 1).ToArray(), refined: false);
        MeasurementUncertainty.FrameSigmaPx(fromOne).Should().BeApproximately(s1, 1e-12);

        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Lost, primary, stars, refined: false)).Should().BeNull();
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.JumpRejected, primary, stars, refined: false)).Should().BeNull();
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Estimated, primary, stars.Take(1).ToArray(), refined: false)).Should().BeNull();
    }

    [Test]
    public void A_star_without_SNR_or_size_makes_the_frame_unknown()
    {
        // a single star (multi-star off or not refined), the refined offset and the estimate from secondaries
        var unmeasured = Snapshot(0, 0);
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Found, unmeasured, [Info(0, unmeasured, TrackedStarStatus.Primary, 1)], refined: false))
            .Should().BeNull();
        var primary = Snapshot(3.0, 20);
        TrackedStarInfo[] stars =
        [
            Info(0, primary, TrackedStarStatus.Primary, 1),
            Info(1, Snapshot(2.5, 40), TrackedStarStatus.Used, 2.0),
            Info(2, Snapshot(3.0, 0), TrackedStarStatus.Used, 0.5),
        ];
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Found, primary, stars, refined: true)).Should().BeNull();
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Found, primary, stars, refined: false)).Should().Be(Sigma(3.0, 20), "the primary alone");
        TrackedStarInfo[] fallback =
        [
            Info(0, primary, TrackedStarStatus.PrimaryLost, 0),
            Info(1, Snapshot(2.5, 40), TrackedStarStatus.FallbackUsed, 0),
            Info(2, Snapshot(0, 12), TrackedStarStatus.FallbackUsed, 0),
        ];
        MeasurementUncertainty.FrameSigmaPx(Result(TrackerOutcome.Estimated, primary, fallback, refined: false)).Should().BeNull();
    }

    // synthetic stars with sub-pixel truth: the model against the true centroid error (the fit covers more cases, see
    // MeasurementUncertainty)
    [TestCase(PsfShape.Gaussian, 3.0, 800.0)]
    [TestCase(PsfShape.Gaussian, 3.0, 3000.0)]
    [TestCase(PsfShape.Gaussian, 3.0, 30000.0)]
    [TestCase(PsfShape.Moffat, 4.5, 2000.0)]
    [TestCase(PsfShape.Moffat, 4.5, 6000.0)]
    public void Matches_the_true_centroid_error_of_synthetic_stars(PsfShape shape, double fwhm, double flux)
    {
        var rng = new Random(17);
        double sumSq = 0, sumModel = 0, sumSnr = 0;
        int n = 0;
        for (int i = 0; i < 300; i++)
        {
            double x = 40 + rng.NextDouble(), y = 30 + rng.NextDouble();
            var r = new StarFieldRenderer(80, 60) { Seed = 1000 + i, Background = 200, ReadNoise = 5, Gain = 1 };
            r.Stars.Add(new SyntheticStar(x, y, flux, fwhm, shape));
            var s = new Star();
            if (s.Find(r.Render(), 15, 40, 30, StarFindMode.Centroid, 0.3, 30, 0))
            {
                sumSq += 0.5 * ((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y));
                sumModel += Math.Pow(Sigma(s.Hfd, s.Snr), 2);
                sumSnr += s.Snr;
                n++;
            }
        }

        double ratio = Math.Sqrt(sumSq / sumModel);
        TestContext.Out.WriteLine($"{shape} FWHM {fwhm} px, SNR {sumSnr / n:F1}: true σ {Math.Sqrt(sumSq / n):F4} px, model {Math.Sqrt(sumModel / n):F4} px ({ratio:F2}×)");
        n.Should().BeGreaterThan(250);
        ratio.Should().BeInRange(0.75, 1.35);
    }

    // the simulator's stars without seeing: the model against the known true position
    [TestCase(9.0, 1.0)]
    [TestCase(12.0, 1.0)]
    [TestCase(12.0, 0.3)]
    [TestCase(13.0, 0.3)]
    public void Matches_the_true_centroid_error_in_the_simulator(double magnitude, double transparency)
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = new MountSimConfig(),
            Sky = SimulatorScenario.GoodMount.Sky with
            {
                Stars = [new SimStar(0, 0, magnitude)],
                SeeingJitterArcsec = 0,
                Transparency = [new TransparencyWindow(0, 1e6, transparency)],
            },
            Camera = SimulatorScenario.GoodMount.Camera with { HotPixelCount = 0, ColdPixelCount = 0, SensorWidth = 256, SensorHeight = 256 },
        };
        var sim = new Simulator(scenario, new VirtualClock());
        double sumSq = 0, sumModel = 0, sumSnr = 0;
        int n = 0;
        for (int i = 0; i < 120; i++)
        {
            var frame = sim.Camera.Render(i * 2.1, 2000, 1, IntRect.Empty, i + 1);
            var truth = sim.LastFrameTruth!.Stars[0];
            var s = new Star();
            if (s.Find(frame, 15, (int)truth.X, (int)truth.Y, StarFindMode.Centroid, 0.3, 30, 0))
            {
                sumSq += 0.5 * ((s.X - truth.X) * (s.X - truth.X) + (s.Y - truth.Y) * (s.Y - truth.Y));
                sumModel += Math.Pow(Sigma(s.Hfd, s.Snr), 2);
                sumSnr += s.Snr;
                n++;
            }
        }

        double ratio = Math.Sqrt(sumSq / sumModel);
        TestContext.Out.WriteLine($"magnitude {magnitude}, transparency {transparency}, SNR {sumSnr / n:F1}: true σ {Math.Sqrt(sumSq / n):F4} px, model {Math.Sqrt(sumModel / n):F4} px ({ratio:F2}×)");
        n.Should().BeGreaterThan(110);
        ratio.Should().BeInRange(0.75, 1.25);
    }

    private static double Sigma(double hfd, double snr) => MeasurementUncertainty.StarSigmaPx(hfd, snr)!.Value;

    private static StarSnapshot Snapshot(double hfd, double snr) => new(new GuidePoint(100, 100), 1000, snr, hfd, 1000, StarFindResult.Ok);

    private static TrackedStarInfo Info(int index, StarSnapshot star, TrackedStarStatus status, double weight) =>
        new(index, star, star.Position, status, weight, new GuidePoint(0.1, 0.1), 0, 0);

    private static MultiStarFrameResult Result(TrackerOutcome outcome, StarSnapshot primary, IReadOnlyList<TrackedStarInfo> stars, bool refined) => new()
    {
        Outcome = outcome,
        Primary = primary,
        ErrorCode = StarFindResult.Ok,
        CameraOffset = new GuidePoint(0.1, 0.1),
        Refined = refined,
        Stars = stars,
    };
}
