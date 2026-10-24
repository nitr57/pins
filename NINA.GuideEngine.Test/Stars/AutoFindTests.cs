// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Stars;

[TestFixture]
public class AutoFindTests
{
    private static readonly StarFinderOptions Phd2 = new() { AutoFindScoring = false };
    private static readonly StarFinderOptions Scoring = new() { AutoFindScoring = true };

    private static List<GuideStar> Run(GuideFrame f, StarFinderOptions o, out AutoFindDiagnostics d, int maxStars = 12, int edge = 0, IntRect roi = default)
    {
        d = new AutoFindDiagnostics();
        return GuideStar.AutoFind(f, edge, o.SearchRegion, roi, maxStars, o, d);
    }

    private static StarFieldRenderer Field(int seed = 1) => new(400, 300) { Seed = seed, Background = 150, ReadNoise = 6 };

    [Test]
    public void PicksBrightestUnsaturatedStar_AvoidsSaturatedAndHotPixels()
    {
        var r = Field();
        r.Saturation = 60000;
        r.AddStar(100.3, 100.6, 40000);
        r.AddStar(250.7, 150.2, 90000);
        r.AddStar(320.1, 220.4, 8_000_000, 3.5); // saturated flat top
        r.HotPixels.Add((60, 250, 60000));
        r.HotPixels.Add((200, 50, 60000));
        var f = r.Render();

        var stars = Run(f, Phd2, out var d);
        stars.Should().NotBeEmpty();
        stars[0].X.Should().BeApproximately(250.7, 0.2);
        stars[0].Y.Should().BeApproximately(150.2, 0.2);
        d.Pass.Should().Be(1);
        d.SaturationLevel.Should().Be(60000, "a flat-topped star at the frame maximum sets the saturation level");
        d.Candidates.Should().NotContain(c => (c.X == 60 && c.Y == 250) || (c.X == 200 && c.Y == 50), "median3 removes hot pixels");
        stars.Should().NotContain(s => Math.Abs(s.X - 320.1) < 3, "saturated star ahead of the primary is dropped from the list");
    }

    [Test]
    public void KnownSaturationAdu_SetsThresholdFromIt()
    {
        var r = Field(2);
        r.AddStar(200.2, 150.3, 60000);
        var f = r.Render();
        f.Pedestal = 100;
        Run(f, Phd2 with { SaturationAdu = 20000 }, out var d);
        d.SaturationLevel.Should().Be(20100u);
        d.SaturationThreshold.Should().Be((ushort)(100 + 9 * 20000 / 10));
    }

    [Test]
    public void RejectsStarsTooCloseToEdge()
    {
        var r = Field(3);
        r.AddStar(12.4, 150.2, 200000); // brightest, within searchRegion of the edge
        r.AddStar(200.3, 120.8, 40000);
        var stars = Run(r.Render(), Phd2, out _);
        stars[0].X.Should().BeApproximately(200.3, 0.2);

        // extra edge allowance (e.g. calibration distance) widens the margin
        var r2 = Field(4);
        r2.AddStar(40.4, 150.2, 200000);
        r2.AddStar(200.3, 120.8, 40000);
        Run(r2.Render(), Phd2, out _, edge: 0)[0].X.Should().BeApproximately(40.4, 0.2);
        Run(r2.Render(), Phd2, out _, edge: 30)[0].X.Should().BeApproximately(200.3, 0.2);
    }

    [Test]
    public void RejectsCrowdedPairsUnlessOneIsFiveTimesBrighter()
    {
        var r = Field(5);
        r.AddStar(150.2, 150.3, 120000);
        r.AddStar(160.7, 152.1, 100000); // similar brightness within the search box: both excluded
        r.AddStar(300.4, 100.6, 30000);
        Run(r.Render(), Phd2, out _)[0].X.Should().BeApproximately(300.4, 0.2);
    }

    [Test]
    public void MultiStarList_PrimaryFirst_SeparatedAndLimited()
    {
        var r = Field(6);
        var rng = new Random(3);
        for (int i = 0; i < 20; i++)
            r.AddStar(40 + (i % 5) * 75 + rng.NextDouble(), 40 + (i / 5) * 60 + rng.NextDouble(), 20000 + 5000 * i);
        var f = r.Render();
        var stars = Run(f, Phd2, out var d, maxStars: 12);
        stars.Count.Should().BeInRange(2, 12);
        for (int i = 0; i < stars.Count; i++)
        {
            stars[i].Snr.Should().BeGreaterThanOrEqualTo(6);
            stars[i].ReferencePoint.Should().Be(stars[i].Position);
            for (int j = i + 1; j < stars.Count; j++)
                stars[i].Position.Distance(stars[j].Position).Should().BeGreaterThanOrEqualTo(25);
        }

        // offsets are relative to the primary's integer peak
        for (int i = 1; i < stars.Count; i++)
        {
            stars[i].OffsetFromPrimary.X.Should().BeApproximately(stars[i].X - d.PrimaryPeak.X, 1e-9);
            stars[i].OffsetFromPrimary.Y.Should().BeApproximately(stars[i].Y - d.PrimaryPeak.Y, 1e-9);
        }

        Run(f, Phd2, out _, maxStars: 4).Count.Should().BeLessThanOrEqualTo(4);
        Run(f, Phd2, out _, maxStars: 1).Should().HaveCount(1);
    }

    [Test]
    public void SubframeImage_ReturnsNothing()
    {
        var r = Field(7);
        r.AddStar(200, 150, 50000);
        var f = r.Render();
        f.Subframe = new IntRect(100, 100, 100, 100);
        Run(f, Phd2, out _).Should().BeEmpty();
    }

    [Test]
    public void Roi_RestrictsSearch()
    {
        var r = Field(8);
        r.AddStar(100.2, 100.4, 150000);
        r.AddStar(300.6, 200.3, 40000);
        var f = r.Render();
        Run(f, Phd2, out _)[0].X.Should().BeApproximately(100.2, 0.2);
        Run(f, Phd2, out _, roi: new IntRect(220, 120, 160, 160))[0].X.Should().BeApproximately(300.6, 0.2);
        Run(f, Phd2, out _, roi: new IntRect(220, 120, 10, 10)).Should().BeEmpty("ROI smaller than the search region");
    }

    [Test]
    public void AutoDownsample_ForSmallPixelScale()
    {
        var r = Field(9);
        r.AddStar(200.3, 150.8, 60000, 5);
        var stars = Run(r.Render(), Phd2 with { PixelScale = 0.5 }, out var d);
        d.Downsample.Should().Be(2);
        stars[0].X.Should().BeApproximately(200.3, 0.3);
        Run(r.Render(), Phd2 with { PixelScale = 1.2 }, out var d1);
        d1.Downsample.Should().Be(1);
    }

    [Test]
    public void NoStars_ReturnsEmpty()
    {
        Run(Field(10).Render(), Phd2, out var d).Should().BeEmpty();
        d.Pass.Should().Be(0);
    }

    [Test]
    public void Scoring_AvoidsNearEdgeCandidate_ButPhd2ModeDoesNot()
    {
        var r = Field(11);
        r.AddStar(24.3, 150.4, 150000); // passes PHD2's edge cut (15) but within 2 search regions
        r.AddStar(200.6, 120.2, 50000);
        r.AddStar(300.2, 220.7, 40000);
        var f = r.Render();
        Run(f, Phd2, out _)[0].X.Should().BeApproximately(24.3, 0.2);
        var scored = Run(f, Scoring, out var d);
        scored[0].X.Should().BeApproximately(200.6, 0.2);
        d.Phd2PrimaryPeak.X.Should().BeApproximately(24, 1);
        d.Candidates.Single(c => Math.Abs(c.X - 24) <= 1).Penalty.Should().HaveFlag(AutoFindPenalty.NearEdge);
    }

    [Test]
    public void Scoring_AvoidsHotPixelLikeCompactSource()
    {
        var r = Field(12);
        // compact 3x3 blob brighter than the stars (hot-pixel cluster / cosmic ray), survives median3
        // (values differ so that the flat-top saturation heuristic does not flag it)
        int k = 0;
        for (int y = 149; y <= 151; y++)
            for (int x = 199; x <= 201; x++)
                r.HotPixels.Add((x, y, (ushort)(x == 200 && y == 150 ? 15000 : 11000 + 150 * k++)));
        r.AddStar(100.3, 80.6, 60000, 4);
        r.AddStar(300.8, 90.1, 50000, 4);
        r.AddStar(90.2, 220.3, 45000, 4);
        r.AddStar(310.5, 230.9, 40000, 4);
        r.AddStar(220.4, 250.2, 35000, 4);
        var f = r.Render();
        var phd2 = Run(f, Phd2, out _);
        phd2[0].X.Should().BeApproximately(200, 0.5, "PHD2 picks the bright compact blob");
        var scored = Run(f, Scoring, out var d);
        scored[0].X.Should().BeApproximately(100.3, 0.3);
        d.Candidates.Single(c => Math.Abs(c.X - 200) <= 1).Penalty.Should().HaveFlag(AutoFindPenalty.LowHfd);
    }

    [Test]
    public void Scoring_PrefersUncrowdedCandidate()
    {
        var r = Field(13);
        r.AddStar(150.4, 150.2, 150000);
        r.AddStar(162.2, 158.9, 20000); // < 1/5 of the bright one: PHD2 keeps both
        r.AddStar(300.7, 80.3, 60000);
        var f = r.Render();
        Run(f, Phd2, out _)[0].X.Should().BeApproximately(150.4, 0.3);
        var scored = Run(f, Scoring, out var d);
        scored[0].X.Should().BeApproximately(300.7, 0.3);
        d.Candidates.Single(c => Math.Abs(c.X - 150) <= 1).Penalty.Should().HaveFlag(AutoFindPenalty.Crowded);
    }

    [Test]
    public void Scoring_FallsBackToPhd2ChoiceWhenAllArePenalised()
    {
        var r = Field(14);
        r.AddStar(22.2, 150.3, 50000); // only star, near edge
        var stars = Run(r.Render(), Scoring, out _);
        stars.Should().HaveCount(1);
        stars[0].X.Should().BeApproximately(22.2, 0.3);
    }

    [Test]
    public void Pass2And3_UsedWhenOnlySaturatedOrFaintStars()
    {
        var r = Field(15);
        r.Saturation = 50000;
        r.AddStar(200.2, 150.1, 9_000_000, 3.5);
        var stars = Run(r.Render(), Phd2, out var d);
        stars.Should().HaveCount(1);
        d.Pass.Should().Be(3);
        stars[0].LastFindResult.Should().Be(StarFindResult.Saturated);
    }
}
