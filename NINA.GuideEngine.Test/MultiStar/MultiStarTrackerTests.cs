// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.MultiStar;

[TestFixture]
public class MultiStarTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Star field that can be shifted as a whole, with per-star extra offsets and omissions.</summary>
    private sealed class Scene
    {
        public List<(double X, double Y, double Flux)> Stars { get; } = new()
        {
            (200.3, 150.6, 90000), // primary (brightest)
            (80.2, 70.9, 50000),
            (320.7, 60.3, 45000),
            (90.6, 230.2, 40000),
            (310.1, 240.8, 35000),
            (150.4, 260.5, 30000),
            (260.9, 110.2, 28000),
        };

        public bool NoiseFree { get; set; }

        public (int X, int Y)? StaticBlob { get; set; }

        public GuideFrame Render(double dx, double dy, int seed, IReadOnlyDictionary<int, (double X, double Y)>? extra = null,
            ISet<int>? omit = null, IReadOnlyDictionary<int, double>? fluxScale = null)
        {
            var r = new StarFieldRenderer(400, 300)
            {
                Seed = seed,
                Background = 150,
                ReadNoise = NoiseFree ? 0 : 6,
                Gain = NoiseFree ? 0 : 1,
            };
            for (int i = 0; i < Stars.Count; i++)
            {
                if (omit?.Contains(i) == true) continue;
                var (x, y, flux) = Stars[i];
                var e = extra is not null && extra.TryGetValue(i, out var v) ? v : (0, 0);
                double scale = fluxScale is not null && fluxScale.TryGetValue(i, out var s) ? s : 1.0;
                r.AddStar(x + dx + e.Item1, y + dy + e.Item2, flux * scale);
            }

            if (StaticBlob is { } b)
            {
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                        r.HotPixels.Add((b.X + i, b.Y + j, (ushort)(i == 0 && j == 0 ? 15000 : i == 0 || j == 0 ? 9000 : 6000)));
            }

            return r.Render();
        }
    }

    private static MultiStarTracker NewTracker(MultiStarOptions? o = null, StarFinderOptions? f = null)
        => new(f ?? new StarFinderOptions(), o ?? new MultiStarOptions());

    private static DateTimeOffset T(int frame) => T0.AddSeconds(2 * frame);

    /// <summary>Selects, then guides 'frames' frames with a small deterministic drift; returns the lock position.</summary>
    private static GuidePoint SelectAndGuide(MultiStarTracker t, Scene scene, int frames, List<MultiStarFrameResult>? results = null)
    {
        var sel = t.AutoSelect(scene.Render(0, 0, 1));
        sel.Success.Should().BeTrue();
        var lockPos = sel.LockPosition;
        for (int i = 1; i <= frames; i++)
        {
            var (dx, dy) = Drift(i);
            var res = t.ProcessFrame(scene.Render(dx, dy, 100 + i), lockPos, TrackerState.Guiding, T(i));
            results?.Add(res);
        }

        return lockPos;
    }

    private static (double, double) Drift(int i) => (0.3 * Math.Sin(i * 0.7), 0.25 * Math.Cos(i * 1.3));

    [Test]
    public void NoStarSelected_Initially()
    {
        var t = NewTracker();
        var res = t.ProcessFrame(new Scene().Render(0, 0, 1), GuidePoint.Invalid, TrackerState.Looping, T0);
        res.Outcome.Should().Be(TrackerOutcome.NoStarSelected);
        res.HasOffset.Should().BeFalse();
    }

    [Test]
    public void AutoSelect_InitialisesPrimaryAndSecondaries()
    {
        var t = NewTracker();
        var sel = t.AutoSelect(new Scene().Render(0, 0, 1));
        sel.Success.Should().BeTrue();
        sel.Primary.Position.X.Should().BeApproximately(200.3, 0.2);
        sel.LockPosition.Should().Be(sel.Primary.Position);
        t.GuideStars.Count.Should().Be(7);
        t.IsStabilizing.Should().BeTrue("lock position was set");
        t.IsLocked.Should().BeTrue();
    }

    [Test]
    public void Tracking_ReportsOffsetFromLock_WhileLooping()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = t.AutoSelect(scene.Render(0, 0, 1)).LockPosition;
        var res = t.ProcessFrame(scene.Render(1.0, -0.5, 2), lockPos, TrackerState.Looping, T(1));
        res.Outcome.Should().Be(TrackerOutcome.Found);
        res.CameraOffset.X.Should().BeApproximately(1.0, 0.1);
        res.CameraOffset.Y.Should().BeApproximately(-0.5, 0.1);
        res.Refined.Should().BeFalse("secondaries are only used while guiding");
        res.Stars.Skip(1).Should().OnlyContain(s => s.Status == TrackedStarStatus.NotMeasured);
        res.Stars[0].Status.Should().Be(TrackedStarStatus.Primary);
    }

    [Test]
    public void Guiding_StabilisesThenUsesWeightedSecondaries()
    {
        var t = NewTracker();
        var results = new List<MultiStarFrameResult>();
        SelectAndGuide(t, new Scene(), 40, results);

        results.Take(5).Should().OnlyContain(r => r.Stabilizing, "first 5 frames always stabilise");
        results.Should().Contain(r => r.Stars.Any(s => s.Status == TrackedStarStatus.Resnapped), "references re-snap after the lock was set");
        var used = results.Skip(10).Where(r => r.Stars.Any(s => s.Status == TrackedStarStatus.Used)).ToList();
        used.Should().NotBeEmpty();
        foreach (var r in used)
        {
            foreach (var s in r.Stars.Where(s => s.Status == TrackedStarStatus.Used))
                s.Weight.Should().BeApproximately(s.Star.Snr / r.Primary.Snr, 1e-12);
            r.StarsUsed.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(9);
            if (r.Refined)
                r.CameraOffset.Distance().Should().BeLessThan(r.PrimaryOnlyOffset.Distance(), "weighted offset only used when smaller");
            else
                r.CameraOffset.Should().Be(r.PrimaryOnlyOffset);
        }

        results.Should().Contain(r => r.Refined);
        // refined offsets still follow the true drift
        for (int i = 10; i < results.Count; i++)
        {
            var (dx, dy) = Drift(i + 1);
            results[i].CameraOffset.X.Should().BeApproximately(dx, 0.2);
            results[i].CameraOffset.Y.Should().BeApproximately(dy, 0.2);
        }
    }

    [Test]
    public void LargePrimaryExcursion_EntersStabilisation()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 25);
        t.IsStabilizing.Should().BeFalse();
        var res = t.ProcessFrame(scene.Render(4, 3, 999), lockPos, TrackerState.Guiding, T(26));
        res.Stabilizing.Should().BeTrue("distance > 5 sigma");
        res.Refined.Should().BeFalse();
        res.CameraOffset.X.Should().BeApproximately(4, 0.15);
        // back near the lock: leaves stabilisation (distance <= 2 sigma)
        MultiStarFrameResult last = res;
        for (int i = 27; i < 35 && last.Stabilizing; i++)
            last = t.ProcessFrame(scene.Render(0, 0, 1000 + i), lockPos, TrackerState.Guiding, T(i));
        last.Stabilizing.Should().BeFalse();
    }

    [Test]
    public void SecondaryExcursion_IsRejectedAsMiss_ThenReferenceResetAfterTenMisses()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        int idx = FindIndex(t, 80.2, 70.9);
        var extra = new Dictionary<int, (double, double)> { [1] = (3.0, 0.0) };
        var statuses = new List<TrackedStarStatus>();
        for (int i = 21; i <= 33; i++)
        {
            var (dx, dy) = Drift(i);
            var res = t.ProcessFrame(scene.Render(dx, dy, 100 + i, extra), lockPos, TrackerState.Guiding, T(i));
            statuses.Add(res.Stars.Single(s => s.Index == idx).Status);
        }

        // Misses may already have accumulated before the excursion (PHD2 gates |displacement| at 2.5 sigma of
        // the primary distance), so the reset can come earlier than 11 frames; until then the star is never used.
        int reset = statuses.IndexOf(TrackedStarStatus.ReferenceReset);
        reset.Should().BeGreaterThanOrEqualTo(0);
        statuses.Take(reset).Should().OnlyContain(s => s == TrackedStarStatus.Miss);
        t.GuideStars[idx].MissCount.Should().BeLessThanOrEqualTo(10);
    }

    [Test]
    public void StaticHotPixelBlob_IsEvicted()
    {
        var t = NewTracker(f: new StarFinderOptions { AutoFindScoring = false });
        var scene = new Scene { NoiseFree = true, StaticBlob = (140, 40) };
        var results = new List<MultiStarFrameResult>();
        SelectAndGuide(t, scene, 20, results);
        results.SelectMany(r => r.Stars).Should().Contain(s => s.Status == TrackedStarStatus.DroppedZero);
        t.GuideStars.Should().NotContain(s => Math.Abs(s.X - 140) < 1 && Math.Abs(s.Y - 40) < 1);
    }

    [Test]
    public void LockPositionChange_ResnapsReferencesAfterStabilising()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        var newLock = new GuidePoint(lockPos.X + 3, lockPos.Y - 2);
        // the star has not moved yet when the lock position changes (dither)
        var res = t.ProcessFrame(scene.Render(0, 0, 500), newLock, TrackerState.Guiding, T(21));
        res.Stabilizing.Should().BeTrue();
        bool resnapped = false;
        for (int i = 22; i < 40 && !resnapped; i++)
        {
            res = t.ProcessFrame(scene.Render(3, -2, 500 + i), newLock, TrackerState.Guiding, T(i));
            resnapped = res.Stars.Any(s => s.Status == TrackedStarStatus.Resnapped);
        }

        resnapped.Should().BeTrue();
        foreach (var gs in t.GuideStars.Skip(1))
            gs.ReferencePoint.Should().Be(gs.Position);
    }

    [Test]
    public void LostSecondary_IsSearchedAtPrimaryPlusOffset()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        int idx = FindIndex(t, 320.7, 60.3);
        var res = t.ProcessFrame(scene.Render(0, 0, 700, omit: new HashSet<int> { 2 }), lockPos, TrackerState.Guiding, T(21));
        res.Stars.Single(s => s.Index == idx).Status.Should().Be(TrackedStarStatus.Lost);
        t.GuideStars[idx].WasLost.Should().BeTrue();
        res = t.ProcessFrame(scene.Render(0.2, 0.1, 701), lockPos, TrackerState.Guiding, T(22));
        res.Stars.Single(s => s.Index == idx).Status.Should().BeOneOf(TrackedStarStatus.Used, TrackedStarStatus.Miss);
        t.GuideStars[idx].WasLost.Should().BeFalse();
    }

    [Test]
    public void PrimaryDropout_IsEstimatedFromSecondaries()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        var res = t.ProcessFrame(scene.Render(0.8, -0.6, 800, omit: new HashSet<int> { 0 }), lockPos, TrackerState.Guiding, T(21));
        res.Outcome.Should().Be(TrackerOutcome.Estimated);
        res.IsEstimated.Should().BeTrue();
        res.StarFound.Should().BeTrue();
        res.ErrorCode.Should().NotBe(StarFindResult.Ok);
        res.CameraOffset.X.Should().BeApproximately(0.8, 0.15);
        res.CameraOffset.Y.Should().BeApproximately(-0.6, 0.15);
        res.Stars.Count(s => s.Status == TrackedStarStatus.FallbackUsed).Should().BeGreaterThanOrEqualTo(3);
        res.Stars[0].Status.Should().Be(TrackedStarStatus.PrimaryLost);

        // primary back: normal tracking resumes, searching at the estimated position
        res = t.ProcessFrame(scene.Render(0.9, -0.5, 801), lockPos, TrackerState.Guiding, T(22));
        res.Outcome.Should().Be(TrackerOutcome.Found, "an estimated frame does not arm the jump filter");
        res.PrimaryOnlyOffset.X.Should().BeApproximately(0.9, 0.2);
    }

    [Test]
    public void PrimaryDropout_FallbackDisabled_ReportsLost()
    {
        var t = NewTracker(new MultiStarOptions { PrimaryDropoutFallback = false });
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        var res = t.ProcessFrame(scene.Render(0.8, -0.6, 800, omit: new HashSet<int> { 0 }), lockPos, TrackerState.Guiding, T(21));
        res.Outcome.Should().Be(TrackerOutcome.Lost);
        res.HasOffset.Should().BeFalse();
        res.StarFound.Should().BeFalse();
        res.Status.Should().StartWith("Star lost");
    }

    [Test]
    public void PrimaryDropout_TooFewOrDisagreeingSecondaries_ReportsLost()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = SelectAndGuide(t, scene, 20);
        // only two secondaries visible
        var omit = new HashSet<int> { 0, 3, 4, 5, 6 };
        t.ProcessFrame(scene.Render(0.2, 0.1, 900, omit: omit), lockPos, TrackerState.Guiding, T(21)).Outcome.Should().Be(TrackerOutcome.Lost);

        var t2 = NewTracker();
        var lock2 = SelectAndGuide(t2, scene, 20);
        var chaos = new Dictionary<int, (double, double)> { [1] = (2.5, 0), [2] = (-2.5, 1), [3] = (0, 3), [4] = (-1, -3), [5] = (3, 3), [6] = (-3, 2) };
        t2.ProcessFrame(scene.Render(0, 0, 901, chaos, new HashSet<int> { 0 }), lock2, TrackerState.Guiding, T(21)).Outcome.Should()
            .Be(TrackerOutcome.Lost);
    }

    [Test]
    public void PrimaryDropout_NotUsedWhileNotGuiding()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = t.AutoSelect(scene.Render(0, 0, 1)).LockPosition;
        t.ProcessFrame(scene.Render(0, 0, 2, omit: new HashSet<int> { 0 }), lockPos, TrackerState.Looping, T(1)).Outcome.Should()
            .Be(TrackerOutcome.Lost);
    }

    [Test]
    public void SaturatedPrimary_ConfiguredAsLost_UsesFallback()
    {
        var scene = new Scene();
        var t = NewTracker(new MultiStarOptions { SaturatedPrimaryIsLost = true, MassChangeThresholdEnabled = false },
            new StarFinderOptions { SaturationAdu = 20000 });
        var lockPos = SelectAndGuide(t, scene, 20);
        var res = t.ProcessFrame(scene.Render(0.1, 0.1, 950, fluxScale: new Dictionary<int, double> { [0] = 30 }), lockPos, TrackerState.Guiding,
            T(21));
        res.ErrorCode.Should().Be(StarFindResult.Saturated);
        res.Outcome.Should().Be(TrackerOutcome.Estimated);

        var t2 = NewTracker(new MultiStarOptions { MassChangeThresholdEnabled = false }, new StarFinderOptions { SaturationAdu = 20000 });
        var lock2 = SelectAndGuide(t2, scene, 20);
        var res2 = t2.ProcessFrame(scene.Render(0.1, 0.1, 950, fluxScale: new Dictionary<int, double> { [0] = 30 }), lock2, TrackerState.Guiding,
            T(21));
        res2.Outcome.Should().Be(TrackerOutcome.Found, "PHD2 treats saturated stars as found");
        res2.ErrorCode.Should().Be(StarFindResult.Saturated);
    }

    [Test]
    public void MassChange_IsDetected()
    {
        var scene = new Scene();
        var t = NewTracker(new MultiStarOptions { PrimaryDropoutFallback = false });
        var lockPos = SelectAndGuide(t, scene, 10);
        var res = t.ProcessFrame(scene.Render(0, 0, 960, fluxScale: new Dictionary<int, double> { [0] = 0.3 }), lockPos, TrackerState.Guiding, T(11));
        res.Outcome.Should().Be(TrackerOutcome.Lost);
        res.ErrorCode.Should().Be(StarFindResult.MassChange);
        res.Primary.Result.Should().Be(StarFindResult.MassChange);
        res.MassLimits.Should().NotBeNull();
        res.Status.Should().Be("Star lost - mass changed");
        t.IsLocked.Should().BeFalse();

        // with the fallback the frame is still usable
        var t2 = NewTracker();
        var lock2 = SelectAndGuide(t2, scene, 10);
        var res2 = t2.ProcessFrame(scene.Render(0, 0, 960, fluxScale: new Dictionary<int, double> { [0] = 0.3 }), lock2, TrackerState.Guiding, T(11));
        res2.ErrorCode.Should().Be(StarFindResult.MassChange);
        res2.Outcome.Should().Be(TrackerOutcome.Estimated);
    }

    [Test]
    public void TolerateJumps_RejectsLargeJumpThenRecoversAfterFiveSeconds()
    {
        var scene = new Scene();
        var t = NewTracker(new MultiStarOptions { TolerateJumpsEnabled = true, TolerateJumpsThreshold = 4.0, MultiStarEnabled = false });
        var lockPos = SelectAndGuide(t, scene, 15);
        var jump = t.ProcessFrame(scene.Render(6, 0, 970), lockPos, TrackerState.Guiding, T(16));
        jump.Outcome.Should().Be(TrackerOutcome.JumpRejected);
        jump.HasOffset.Should().BeFalse();
        t.ProcessFrame(scene.Render(6, 0, 971), lockPos, TrackerState.Guiding, T(16).AddSeconds(2)).Outcome.Should().Be(TrackerOutcome.JumpRejected);
        t.ProcessFrame(scene.Render(6, 0, 972), lockPos, TrackerState.Guiding, T(16).AddSeconds(6)).Outcome.Should().Be(TrackerOutcome.Found);

        // disabled: jumps are accepted
        var t2 = NewTracker(new MultiStarOptions { MultiStarEnabled = false });
        var lock2 = SelectAndGuide(t2, scene, 15);
        t2.ProcessFrame(scene.Render(6, 0, 970), lock2, TrackerState.Guiding, T(16)).Outcome.Should().Be(TrackerOutcome.Found);
    }

    [Test]
    public void SubframeMode_SingleStar()
    {
        var scene = new Scene();
        var t = NewTracker(new MultiStarOptions { UseSubframes = true });
        var sel = t.AutoSelect(scene.Render(0, 0, 1));
        sel.Success.Should().BeTrue();
        t.GuideStars.Should().HaveCount(1);
        var box = t.GetBoundingBox(BoundingBoxState.Guiding, sel.LockPosition, 400, 300);
        box.Width.Should().Be(31);
        box.Contains(sel.LockPosition.X, sel.LockPosition.Y).Should().BeTrue();
        var f = scene.Render(0.5, 0.5, 2);
        f.Subframe = box;
        var res = t.ProcessFrame(f, sel.LockPosition, TrackerState.Guiding, T(1));
        res.Outcome.Should().Be(TrackerOutcome.Found);
        res.CameraOffset.X.Should().BeApproximately(0.5, 0.1);
        t.GetBoundingBox(BoundingBoxState.Other, sel.LockPosition, 400, 300).IsEmpty.Should().BeTrue();
    }

    [Test]
    public void SingleStarMode_NeverRefines()
    {
        var t = NewTracker(new MultiStarOptions { MultiStarEnabled = false });
        var results = new List<MultiStarFrameResult>();
        SelectAndGuide(t, new Scene(), 20, results);
        t.GuideStars.Should().HaveCount(1);
        results.Should().OnlyContain(r => !r.Refined && r.StarsUsed == 1);
    }

    [Test]
    public void Initialize_FromExternalAutoFind()
    {
        var scene = new Scene();
        var frame = scene.Render(0, 0, 1);
        var found = GuideStar.AutoFind(frame, 0, 15, IntRect.Empty, 12, new StarFinderOptions());
        var t = NewTracker();
        var sel = t.Initialize(frame, found);
        sel.Success.Should().BeTrue();
        t.GuideStars.Count.Should().Be(found.Count);
        t.ProcessFrame(scene.Render(0.3, 0.3, 2), sel.LockPosition, TrackerState.Looping, T(1)).Outcome.Should().Be(TrackerOutcome.Found);
    }

    [Test]
    public void SelectStarAt_ManualSingleStar()
    {
        var scene = new Scene();
        var frame = scene.Render(0, 0, 1);
        var t = NewTracker();
        t.AutoSelect(frame);
        t.SelectStarAt(frame, new GuidePoint(81, 71)).Should().BeTrue();
        t.PrimaryStar.X.Should().BeApproximately(80.2, 0.2);
        t.GuideStars.Should().HaveCount(1);
    }

    [Test]
    public void SelectStar_MakesTheChosenStarPrimaryAndFindsSecondariesAroundIt()
    {
        var scene = new Scene();
        var frame = scene.Render(0, 0, 1);
        var t = NewTracker();
        t.AutoSelect(frame).Primary.Position.X.Should().BeApproximately(200.3, 0.2, "AutoFind picks the brightest star");

        // a tap a few px off the star
        var r = t.SelectStar(frame, new GuidePoint(84, 74));

        r.Success.Should().BeTrue();
        r.Primary.Position.X.Should().BeApproximately(80.2, 0.2);
        r.Primary.Position.Y.Should().BeApproximately(70.9, 0.2);
        t.PrimaryStar.X.Should().BeApproximately(80.2, 0.2);
        r.SecondaryStars.Should().Be(t.GuideStars.Count - 1).And.BeGreaterThan(2);
        t.GuideStars[0].X.Should().BeApproximately(80.2, 0.2);
        foreach (var gs in t.GuideStars.Skip(1))
        {
            (gs.Position - t.PrimaryStar.Position).Distance().Should().BeGreaterThan(15, "the primary is not also a secondary");
            var expected = t.PrimaryStar.Position + gs.OffsetFromPrimary;
            expected.X.Should().BeApproximately(gs.X, 0.01);
            expected.Y.Should().BeApproximately(gs.Y, 0.01);
        }

        t.GuideStars.Skip(1).Should().Contain(gs => Math.Abs(gs.X - 200.3) < 0.5, "the previous primary is now a secondary");
        t.ProcessFrame(scene.Render(0.3, 0.2, 2), r.Primary.Position, TrackerState.Looping, T(1)).Primary.Position.X
            .Should().BeApproximately(80.5, 0.3);
    }

    [Test]
    public void SelectStar_SingleStarMode_HasNoSecondaries()
    {
        var scene = new Scene();
        var t = NewTracker(new MultiStarOptions { MultiStarEnabled = false });
        var r = t.SelectStar(scene.Render(0, 0, 1), new GuidePoint(81, 71));
        r.Success.Should().BeTrue();
        r.SecondaryStars.Should().Be(0);
        t.GuideStars.Should().HaveCount(1);
    }

    [Test]
    public void SelectStar_WithoutAStarThere_KeepsTheSelection()
    {
        var scene = new Scene();
        var frame = scene.Render(0, 0, 1);
        var t = NewTracker();
        t.AutoSelect(frame);
        int stars = t.GuideStars.Count;

        t.SelectStar(frame, new GuidePoint(370, 170)).Error.Should().Be(StarSelectionError.NoStar);
        t.SelectStar(frame, new GuidePoint(-5, 170)).Error.Should().Be(StarSelectionError.NoStar);

        t.PrimaryStar.X.Should().BeApproximately(200.3, 0.2);
        t.GuideStars.Should().HaveCount(stars);
    }

    [Test]
    public void SelectStar_TooCloseToTheEdge_IsRejected()
    {
        var scene = new Scene();
        scene.Stars.Add((9.5, 150.5, 40000));
        var frame = scene.Render(0, 0, 1);
        var t = NewTracker();
        t.AutoSelect(frame);

        var r = t.SelectStar(frame, new GuidePoint(10, 150));

        r.Error.Should().Be(StarSelectionError.NearEdge);
        r.Primary.Position.X.Should().BeApproximately(9.5, 0.5, "the star was found, only too close to the edge");
        t.PrimaryStar.X.Should().BeApproximately(200.3, 0.2);
    }

    [Test]
    public void RefreshSecondaryStars_ReplacesSecondariesThatBelongToAnotherField()
    {
        // the primary changed to a star with other neighbours: same primary position, the rest of the field differs
        var scene = new Scene();
        var other = new Scene();
        other.Stars.RemoveRange(1, other.Stars.Count - 1);
        other.Stars.AddRange([(50.5, 40.2, 50000), (350.4, 200.7, 45000), (120.3, 180.9, 40000), (280.6, 270.1, 35000), (60.2, 270.4, 30000)]);
        var t = NewTracker();
        var sel = t.AutoSelect(scene.Render(0, 0, 1));
        int before = t.GuideStars.Count - 1;
        before.Should().Be(6);
        for (int i = 1; i <= 8; i++)
        {
            var res = t.ProcessFrame(other.Render(0, 0, 10 + i), sel.LockPosition, TrackerState.Guiding, T(i));
            res.Outcome.Should().Be(TrackerOutcome.Found);
        }

        var frame = other.Render(0, 0, 30);
        t.ProcessFrame(frame, sel.LockPosition, TrackerState.Guiding, T(9));
        var r = t.RefreshSecondaryStars(frame, force: false);

        r.Should().Be(new SecondaryRefreshResult(6, 5, true));
        t.GuideStars.Should().HaveCount(6);
        t.GuideStars[0].X.Should().BeApproximately(200.3, 0.3);
        foreach (var (x, y, _) in other.Stars.Skip(1))
            t.GuideStars.Skip(1).Should().ContainSingle(gs => Math.Abs(gs.X - x) < 0.5 && Math.Abs(gs.Y - y) < 0.5);
        t.IsStabilizing.Should().BeTrue("the new reference points are re-snapped once the primary is back at the lock position");
    }

    [Test]
    public void RefreshSecondaryStars_KeepsSecondariesThatAreStillThere()
    {
        var scene = new Scene();
        var frame = scene.Render(0, 0, 1);
        var t = NewTracker();
        t.AutoSelect(frame);
        var stars = t.GuideStars.ToList();
        t.ProcessFrame(scene.Render(0.2, 0.1, 2), t.PrimaryStar.Position, TrackerState.Looping, T(1));

        t.RefreshSecondaryStars(scene.Render(0.2, 0.1, 2), force: false).Replaced.Should().BeFalse();

        t.GuideStars.Should().Equal(stars);
    }

    [Test]
    public void RefreshSecondaryStars_KeepsTheListWhenFewStarsAreFound()
    {
        // clouds: only the primary and one secondary are visible
        var scene = new Scene();
        var t = NewTracker();
        t.AutoSelect(scene.Render(0, 0, 1));
        var stars = t.GuideStars.ToList();
        var cloudy = scene.Render(0, 0, 2, omit: new HashSet<int> { 2, 3, 4, 5, 6 });
        t.ProcessFrame(cloudy, t.PrimaryStar.Position, TrackerState.Looping, T(1));

        var r = t.RefreshSecondaryStars(cloudy, force: true);

        r.Should().Be(new SecondaryRefreshResult(6, 1, false));
        t.GuideStars.Should().Equal(stars);
    }

    [Test]
    public void DistanceAverages_FollowPhd2Smoothing()
    {
        var t = NewTracker();
        var scene = new Scene();
        var lockPos = t.AutoSelect(scene.Render(0, 0, 1)).LockPosition;
        t.ProcessFrame(scene.Render(1, 0, 2), lockPos, TrackerState.Looping, T(1));
        t.Distances.FrameCount.Should().Be(1);
        t.Distances.CurrentError(false, T(1).ToUnixTimeMilliseconds()).Should().BeApproximately(1, 0.1);
        t.Distances.CurrentError(false, T(20).ToUnixTimeMilliseconds()).Should().Be(DistanceAverager.LargeDistance, "stale after 20 s");
        for (int i = 2; i < 6; i++)
            t.ProcessFrame(scene.Render(0, 0, 2 + i), lockPos, TrackerState.Guiding, T(i));
        t.Distances.FrameCount.Should().Be(5);
    }

    private static int FindIndex(MultiStarTracker t, double x, double y)
    {
        for (int i = 0; i < t.GuideStars.Count; i++)
            if (Math.Abs(t.GuideStars[i].ReferencePoint.X - x) < 2 && Math.Abs(t.GuideStars[i].ReferencePoint.Y - y) < 2)
                return i;
        throw new AssertionException($"star near {x},{y} not in list");
    }
}
