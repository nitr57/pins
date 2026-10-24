// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Stars;

[TestFixture]
public class StarFindTests
{
    private const int Sr = 15;

    private static Star FindAt(GuideFrame f, double x, double y, ushort sat = 0, StarFindMode mode = StarFindMode.Centroid, double minHfd = 1.5,
        double maxHfd = 20)
    {
        var s = new Star();
        s.Find(f, Sr, (int)x, (int)y, mode, minHfd, maxHfd, sat);
        return s;
    }

    [TestCase(PsfShape.Gaussian, 2.5, 20000.0, 0.005)]
    [TestCase(PsfShape.Gaussian, 4.0, 20000.0, 0.005)]
    [TestCase(PsfShape.Moffat, 3.0, 20000.0, 0.02)]
    [TestCase(PsfShape.Gaussian, 3.0, 5000.0, 0.005)]
    public void Centroid_NoiseFree_SystematicErrorIsSmall(PsfShape shape, double fwhm, double flux, double maxRms)
    {
        // Pixel-phase (systematic) error of the thresholded first moment over random sub-pixel positions.
        // Moffat wings extend beyond the r=7 aperture centred on the integer peak, which biases slightly
        // towards the peak pixel (inherent to PHD2's algorithm).
        var rng = new Random(42);
        double sumSq = 0, maxErr = 0;
        const int n = 60;
        for (int i = 0; i < n; i++)
        {
            double x = 40 + rng.NextDouble(), y = 30 + rng.NextDouble();
            var r = new StarFieldRenderer(80, 60) { Seed = 1000 + i, Background = 200, ReadNoise = 0, Gain = 0 };
            r.Stars.Add(new SyntheticStar(x, y, flux, fwhm, shape));
            var s = FindAt(r.Render(), x, y);
            s.WasFound().Should().BeTrue();
            double e2 = (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
            sumSq += e2;
            maxErr = Math.Max(maxErr, Math.Sqrt(e2));
        }

        double rms = Math.Sqrt(sumSq / n);
        TestContext.Out.WriteLine($"noise-free {shape} fwhm={fwhm} flux={flux}: centroid rms={rms:F4} px max={maxErr:F4} px");
        rms.Should().BeLessThan(maxRms);
        maxErr.Should().BeLessThan(2 * maxRms);
    }

    [TestCase(PsfShape.Gaussian, 2.5, 20000.0, 0.05)]
    [TestCase(PsfShape.Gaussian, 4.0, 20000.0, 0.05)]
    [TestCase(PsfShape.Moffat, 3.0, 20000.0, 0.06)]
    [TestCase(PsfShape.Gaussian, 3.0, 50000.0, 0.03)]
    [TestCase(PsfShape.Gaussian, 3.0, 5000.0, 0.12)]
    [TestCase(PsfShape.Gaussian, 3.0, 2000.0, 0.25)]
    public void Centroid_WithNoise_RmsErrorVsTruth(PsfShape shape, double fwhm, double flux, double maxRms)
    {
        // With shot + read noise the error is noise dominated (≈ 3.7 px / SNR for FWHM 3); the systematic
        // part is covered by the noise-free test. Bounds leave ~20 % headroom over measured values.
        var rng = new Random(42);
        double sumSq = 0, sumSnr = 0, bx = 0, by = 0;
        const int n = 100;
        for (int i = 0; i < n; i++)
        {
            double x = 40 + rng.NextDouble(), y = 30 + rng.NextDouble();
            var r = new StarFieldRenderer(80, 60) { Seed = 1000 + i, Background = 200, ReadNoise = 6 };
            r.Stars.Add(new SyntheticStar(x, y, flux, fwhm, shape));
            var s = FindAt(r.Render(), x, y);
            s.WasFound().Should().BeTrue();
            sumSnr += s.Snr;
            bx += s.X - x;
            by += s.Y - y;
            sumSq += (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
        }

        double rms = Math.Sqrt(sumSq / n);
        TestContext.Out.WriteLine(
            $"{shape} fwhm={fwhm} flux={flux}: mean SNR={sumSnr / n:F1} centroid rms={rms:F4} px bias=({bx / n:F4},{by / n:F4})");
        rms.Should().BeLessThan(maxRms);
        Math.Abs(bx / n).Should().BeLessThan(0.03);
        Math.Abs(by / n).Should().BeLessThan(0.03);
    }

    [Test]
    public void Snr_IncreasesMonotonicallyWithFlux()
    {
        double prev = 0;
        foreach (double flux in new[] { 1500.0, 3000, 6000, 12000, 24000, 48000 })
        {
            var r = new StarFieldRenderer(60, 60) { Seed = 7, Background = 100, ReadNoise = 5 };
            r.AddStar(30.3, 29.6, flux);
            var s = FindAt(r.Render(), 30, 30);
            s.WasFound().Should().BeTrue();
            s.Snr.Should().BeGreaterThan(prev);
            // PHD2 SNR uses a nominal gain of 0.5 e-/ADU, so for a bright star SNR ≈ sqrt(0.5·mass)
            if (flux >= 24000)
                s.Snr.Should().BeInRange(0.7 * Math.Sqrt(0.5 * s.Mass), Math.Sqrt(0.5 * s.Mass) * 1.001);
            prev = s.Snr;
        }
    }

    [Test]
    public void Hfd_TracksFwhmAndIsPlausible()
    {
        double prev = 0;
        foreach (double fwhm in new[] { 2.0, 3.0, 4.5, 6.0 })
        {
            var r = new StarFieldRenderer(80, 80) { Seed = 11, Background = 50, ReadNoise = 3 };
            r.AddStar(40.4, 39.7, 200000, fwhm);
            var s = FindAt(r.Render(), 40, 40);
            s.WasFound().Should().BeTrue();
            TestContext.Out.WriteLine($"fwhm {fwhm}: HFD {s.Hfd:F2}");
            // For a Gaussian HFD = FWHM; PHD2's thresholded HFD is slightly smaller
            s.Hfd.Should().BeInRange(0.75 * fwhm, 1.1 * fwhm);
            s.Hfd.Should().BeGreaterThan(prev);
            prev = s.Hfd;
        }
    }

    [Test]
    public void Result_Ok_And_Position()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 1 };
        r.AddStar(25.25, 33.75, 30000);
        var s = FindAt(r.Render(), 27, 31);
        s.LastFindResult.Should().Be(StarFindResult.Ok);
        s.IsValid.Should().BeTrue();
        s.X.Should().BeApproximately(25.25, 0.1);
        s.Y.Should().BeApproximately(33.75, 0.1);
        s.PeakValue.Should().BeGreaterThan(1000);
    }

    [Test]
    public void Result_LowMass_OnNoiseFreeFlatField()
    {
        var f = new GuideFrame(50, 50);
        Array.Fill(f.Pixels, (ushort)1000);
        var s = FindAt(f, 25, 25);
        s.LastFindResult.Should().Be(StarFindResult.LowMass);
        s.WasFound().Should().BeFalse();
        s.X.Should().Be(25, "position stays at the search hint");
        s.Hfd.Should().Be(0);
    }

    [Test]
    public void Result_LowSnr_OnPureNoise()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 3, ReadNoise = 20 };
        var s = FindAt(r.Render(), 30, 30);
        s.LastFindResult.Should().BeOneOf(StarFindResult.LowSnr, StarFindResult.LowMass);
        s.WasFound().Should().BeFalse();

        // a very faint star is below SNR 3 as well
        var r2 = new StarFieldRenderer(60, 60) { Seed = 5, ReadNoise = 20 };
        r2.AddStar(30, 30, 600);
        FindAt(r2.Render(), 30, 30).LastFindResult.Should().Be(StarFindResult.LowSnr);
    }

    [Test]
    public void Result_LowHfd_ForHotPixel()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 4, ReadNoise = 3 };
        r.HotPixels.Add((30, 30, 20000));
        var s = FindAt(r.Render(), 30, 30);
        s.LastFindResult.Should().Be(StarFindResult.LowHfd);
        s.Hfd.Should().BeLessThan(1.5);
        s.X.Should().BeApproximately(30, 0.01, "centroid position is kept for low HFD");
    }

    [Test]
    public void Result_HighHfd()
    {
        var r = new StarFieldRenderer(80, 80) { Seed = 6 };
        r.AddStar(40, 40, 300000, 9);
        FindAt(r.Render(), 40, 40, maxHfd: 5).LastFindResult.Should().Be(StarFindResult.HighHfd);
    }

    [Test]
    public void Result_Saturated_KnownAdu_And_FlatTop16And8Bit()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 8, Saturation = 40000 };
        r.AddStar(30.2, 29.8, 2_000_000, 3.5);
        var f = r.Render();
        var known = FindAt(f, 30, 30, sat: 40000);
        known.LastFindResult.Should().Be(StarFindResult.Saturated);
        known.WasFound().Should().BeTrue("saturated counts as found");

        // flat-top heuristic, 16 bit: top 3 values equal
        FindAt(f, 30, 30, sat: 0).LastFindResult.Should().Be(StarFindResult.Saturated);

        // not saturated when the known level is higher
        FindAt(f, 30, 30, sat: 50000).LastFindResult.Should().Be(StarFindResult.Ok);

        // 8 bit camera, flat top within 1/191
        var r8 = new StarFieldRenderer(60, 60) { Seed = 9, Saturation = 255, BitsPerPixel = 8, Bias = 10, Background = 5, ReadNoise = 1, Gain = 0 };
        r8.AddStar(30, 30, 20000, 3.5);
        FindAt(r8.Render(), 30, 30).LastFindResult.Should().Be(StarFindResult.Saturated);

        // unsaturated bright star: top values differ, not flagged
        var ok = new StarFieldRenderer(60, 60) { Seed = 10 };
        ok.AddStar(30.3, 30.1, 100000, 3.0);
        FindAt(ok.Render(), 30, 30).LastFindResult.Should().Be(StarFindResult.Ok);
    }

    [Test]
    public void Result_Error_ForInvalidCoordinates()
    {
        var f = new StarFieldRenderer(40, 40).Render();
        var s = FindAt(f, -100, -100);
        s.LastFindResult.Should().Be(StarFindResult.Error);
        s.Mass.Should().Be(0);
        s.Snr.Should().Be(0);
        s.WasFound().Should().BeFalse();
        s.X.Should().Be(-100);
    }

    [Test]
    public void ResultCodes_KeepPhd2Order()
    {
        ((int)StarFindResult.Ok).Should().Be(0);
        Enum.GetValues<StarFindResult>().Should().Equal(StarFindResult.Ok, StarFindResult.Saturated, StarFindResult.LowSnr, StarFindResult.LowMass,
            StarFindResult.LowHfd, StarFindResult.HighHfd, StarFindResult.TooNearEdge, StarFindResult.MassChange, StarFindResult.Error);
        Star.WasFound(StarFindResult.Saturated).Should().BeTrue();
        Star.WasFound(StarFindResult.MassChange).Should().BeFalse();
        // TooNearEdge exists for compatibility but, as in PHD2, Star.Find never returns it;
        // MassChange is set by the multi-star tracker (see MultiStarTrackerTests).
    }

    [Test]
    public void StarNearFrameEdge_IsStillMeasured()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 12 };
        r.AddStar(4.3, 30.2, 30000);
        var s = FindAt(r.Render(), 5, 30);
        s.WasFound().Should().BeTrue();
        s.X.Should().BeApproximately(4.3, 0.2);
    }

    [Test]
    public void PeakMode_ReturnsBrightestPixel()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 13 };
        r.AddStar(30.4, 30.4, 50000);
        var f = r.Render();
        var s = FindAt(f, 30, 30, mode: StarFindMode.Peak);
        s.WasFound().Should().BeTrue();
        s.Hfd.Should().Be(0.5, "single pixel: HFR 0.25");
        s.Mass.Should().Be(s.PeakValue);
        int px = (int)Math.Round(s.X), py = (int)Math.Round(s.Y);
        f[px, py].Should().Be(s.PeakValue);
    }

    [Test]
    public void Subframe_LimitsSearch()
    {
        var r = new StarFieldRenderer(100, 100) { Seed = 14 };
        r.AddStar(50.5, 50.5, 40000);
        r.AddStar(70.2, 50.2, 90000); // brighter but outside the subframe
        var f = r.Render();
        f.Subframe = new IntRect(35, 35, 31, 31);
        var s = FindAt(f, 55, 50);
        s.WasFound().Should().BeTrue();
        s.X.Should().BeApproximately(50.5, 0.2);
    }

    [Test]
    public void ConvenienceFind_TruncatesHintLikePhd2()
    {
        var r = new StarFieldRenderer(60, 60) { Seed = 15 };
        r.AddStar(30.2, 30.2, 30000);
        var f = r.Render();
        var s = new Star { Position = new GuidePoint(29.99, 29.99) };
        s.Find(f, Sr, StarFindMode.Centroid, 1.5, 20, 0).Should().BeTrue();
        var s2 = FindAt(f, 29, 29);
        s.Position.Should().Be(s2.Position);
    }

    [Test]
    public void Find_DoesNotAllocate()
    {
        var r = new StarFieldRenderer(200, 200) { Seed = 16 };
        r.AddStar(100.3, 100.7, 30000);
        var f = r.Render();
        var s = new Star();
        s.Find(f, Sr, 100, 100, StarFindMode.Centroid, 1.5, 20, 0); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            s.Find(f, Sr, 100, 100, StarFindMode.Centroid, 1.5, 20, 0);
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).Should().Be(0);
    }

    [Test]
    public void Invalidate_KeepsCoordinatesAsHint()
    {
        var s = new Star { Position = new GuidePoint(12.5, 7.25), Mass = 5, Snr = 3, LastFindResult = StarFindResult.Ok };
        s.Invalidate();
        s.IsValid.Should().BeFalse();
        s.X.Should().Be(12.5);
        s.Mass.Should().Be(0);
        s.LastFindResult.Should().Be(StarFindResult.Error);
    }
}
