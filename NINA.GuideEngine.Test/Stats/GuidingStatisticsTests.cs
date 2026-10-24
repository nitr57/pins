// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Stats;

namespace NINA.GuideEngine.Test.Stats;

[TestFixture]
public class GuidingStatisticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    private static GuideStepSample S(int i, double ra, double dec, bool excluded = false, double raCorr = 0, double decCorr = 0,
        int raMs = 0, int decMs = 0, double? snr = null, int? stars = null) => new()
        {
            Timestamp = T0.AddSeconds(2 * i),
            RaPx = ra,
            DecPx = dec,
            RaCorrectionPx = raCorr,
            DecCorrectionPx = decCorr,
            RaDurationMs = raMs,
            DecDurationMs = decMs,
            Snr = snr,
            StarCount = stars,
            Excluded = excluded,
        };

    [Test]
    public void RmsIsPopulationSigmaAboutTheMean()
    {
        var gs = new GuidingStatistics(100, pixelScale: 2.0);
        gs.Start(T0);
        double[] ra = [1, -1, 1, -1];
        double[] dec = [3, 3, 5, 5]; // mean 4, sigma 1
        for (int i = 0; i < 4; i++)
            gs.Add(S(i, ra[i], dec[i]));
        var w = gs.GetSnapshot(T0.AddSeconds(10)).Window;
        w.RmsRaPx.Should().BeApproximately(1.0, 1e-12);
        w.RmsDecPx.Should().BeApproximately(1.0, 1e-12);
        w.RmsTotalPx.Should().BeApproximately(Math.Sqrt(2), 1e-12);
        w.RmsTotalArcsec.Should().BeApproximately(2 * Math.Sqrt(2), 1e-12);
        w.PeakRaPx.Should().Be(1);
        w.PeakDecPx.Should().Be(5);
        w.PeakDecArcsec.Should().Be(10);
    }

    [Test]
    public void ExcludedFramesAreLeftOutOfRmsButCounted()
    {
        var gs = new GuidingStatistics();
        gs.Add(S(0, 1, 0));
        gs.Add(S(1, -1, 0));
        gs.Add(S(2, 50, 50, excluded: true));
        var snap = gs.GetSnapshot(T0.AddSeconds(4));
        snap.Window.Frames.Should().Be(3);
        snap.Window.IncludedFrames.Should().Be(2);
        snap.Window.RmsRaPx.Should().BeApproximately(1.0, 1e-12);
        snap.Window.PeakRaPx.Should().Be(1);
        snap.Session.RmsRaPx.Should().BeApproximately(1.0, 1e-12);
        snap.FrameCount.Should().Be(3);
    }

    [Test]
    public void WindowKeepsLastFramesWhileSessionAccumulates()
    {
        var gs = new GuidingStatistics(windowSize: 3);
        double[] ra = [10, -10, 1, -1, 1];
        for (int i = 0; i < ra.Length; i++)
            gs.Add(S(i, ra[i], 0));
        var snap = gs.GetSnapshot(T0.AddSeconds(10));
        snap.Window.IncludedFrames.Should().Be(3);
        snap.Window.PeakRaPx.Should().Be(1);
        snap.Window.RmsRaPx.Should().BeApproximately(Math.Sqrt(8.0 / 9.0), 1e-12); // [1,-1,1]
        snap.Session.IncludedFrames.Should().Be(5);
        snap.Session.PeakRaPx.Should().Be(10);
        gs.ChangeWindowSize(2);
        gs.GetSnapshot(T0).Window.IncludedFrames.Should().Be(2);
    }

    [Test]
    public void DriftIsReconstructedFromOffsetsAndCorrections()
    {
        // True Dec drift 0.5 px/min; each 2 s frame shows the drift since the last frame and it is fully corrected.
        double perFrame = 0.5 / 30.0;
        var gs = new GuidingStatistics(100, pixelScale: 2.0) { DeclinationDeg = 60 };
        gs.Start(T0);
        for (int i = 0; i < 60; i++)
            gs.Add(S(i, 0.1, perFrame, decCorr: perFrame, raCorr: 0.1 - 0.05));
        var w = gs.GetSnapshot(T0.AddMinutes(2)).Window;
        w.DecDriftPxPerMin.Should().BeApproximately(0.5, 1e-9);
        w.DecDriftArcsecPerMin.Should().BeApproximately(1.0, 1e-9);
        w.RaDriftPxPerMin.Should().BeApproximately(0.05 * 30, 1e-9);
        w.PolarAlignmentErrorArcmin.Should().BeApproximately(3.8197 * 0.5 * 2.0 / 0.5, 1e-9);
        w.PolarAlignmentDecAssumed.Should().BeFalse();
    }

    [Test]
    public void PolarAlignmentAssumesDecZeroWhenUnknown()
    {
        var gs = new GuidingStatistics(100, pixelScale: 1.5);
        for (int i = 0; i < 31; i++)
            gs.Add(S(i, 0, 0.02, decCorr: 0.02));
        var w = gs.GetSnapshot(T0.AddMinutes(1)).Window;
        w.DecDriftPxPerMin.Should().BeApproximately(0.6, 1e-9);
        w.PolarAlignmentErrorArcmin.Should().BeApproximately(3.8197 * 0.6 * 1.5, 1e-9);
        w.PolarAlignmentDecAssumed.Should().BeTrue();
    }

    [Test]
    public void DitherLockJumpDoesNotCreateDrift()
    {
        var gs = new GuidingStatistics();
        int i = 0;
        for (; i < 20; i++)
            gs.Add(S(i, 0, 0));
        gs.Add(S(i++, 5, 5, excluded: true, raCorr: 2.5, decCorr: 2.5)); // dither: lock moved, recenter
        gs.Add(S(i++, 2.5, 2.5, excluded: true, raCorr: 2.5, decCorr: 2.5));
        for (; i < 40; i++)
            gs.Add(S(i, 0, 0));
        var snap = gs.GetSnapshot(T0.AddMinutes(2));
        snap.Window.DecDriftPxPerMin.Should().BeApproximately(0, 1e-12);
        snap.Session.RaDriftPxPerMin.Should().BeApproximately(0, 1e-12);
    }

    [Test]
    public void DriftIsNullWithoutEnoughData()
    {
        var gs = new GuidingStatistics();
        gs.Add(S(0, 1, 1));
        var w = gs.GetSnapshot(T0).Window;
        w.DecDriftPxPerMin.Should().BeNull();
        w.PolarAlignmentErrorArcmin.Should().BeNull();
    }

    [Test]
    public void OscillationIndexAndPhd2Thresholds()
    {
        var alternating = new GuidingStatistics();
        for (int i = 0; i < 10; i++)
            alternating.Add(S(i, i % 2 == 0 ? 0.5 : -0.5, 0));
        var a = alternating.GetSnapshot(T0).Window;
        a.OscillationIndex.Should().Be(1.0);
        a.OscillationAlert.Should().BeTrue();

        var sameSide = new GuidingStatistics();
        for (int i = 0; i < 10; i++)
            sameSide.Add(S(i, 0.5, 0));
        sameSide.GetSnapshot(T0).Window.OscillationIndex.Should().Be(0.0);
        sameSide.GetSnapshot(T0).Window.OscillationAlert.Should().BeTrue();

        var healthy = new GuidingStatistics();
        double[] ra = [0.3, 0.2, -0.1, -0.2, 0.1, 0.3, -0.2, -0.1, 0.2]; // 4 same-side pairs of 8
        for (int i = 0; i < ra.Length; i++)
            healthy.Add(S(i, ra[i], 0));
        var h = healthy.GetSnapshot(T0).Window;
        h.OscillationIndex.Should().BeApproximately(0.5, 1e-12);
        h.OscillationAlert.Should().BeFalse();
        h.Should().BeEquivalentTo(healthy.GetSnapshot(T0).Session, o => o.Including(x => x.OscillationIndex));
    }

    [Test]
    public void DutySnrAndStarCount()
    {
        var gs = new GuidingStatistics();
        gs.Add(S(0, 0.1, 0.1, raMs: 100, snr: 20, stars: 5));
        gs.Add(S(1, 0.1, 0.1, raMs: 100, decMs: 50, snr: 10, stars: 7));
        gs.Add(S(2, 0.1, 0.1, snr: 30, stars: 6));
        gs.Add(S(3, 0.1, 0.1, raMs: 100, excluded: true, snr: 1));
        var w = gs.GetSnapshot(T0).Window;
        w.RaDutyPercent.Should().BeApproximately(200.0 / 3, 1e-9);
        w.DecDutyPercent.Should().BeApproximately(100.0 / 3, 1e-9);
        w.SnrMin.Should().Be(10);
        w.SnrAvg.Should().Be(20);
        w.SnrLast.Should().Be(30);
        w.StarCountAvg.Should().Be(6);
    }

    [Test]
    public void ElapsedStarLostAndRestart()
    {
        var gs = new GuidingStatistics();
        gs.Start(T0);
        gs.Add(S(1, 0, 0));
        gs.AddStarLost(T0.AddSeconds(4));
        var snap = gs.GetSnapshot(T0.AddSeconds(90));
        snap.Elapsed.Should().Be(TimeSpan.FromSeconds(90));
        snap.StarLostCount.Should().Be(1);
        snap.FrameCount.Should().Be(1);
        gs.Start(T0.AddHours(1));
        gs.GetSnapshot(T0.AddHours(1)).FrameCount.Should().Be(0);
    }

    [Test]
    public void LimitCountsIncludeAllFrames()
    {
        var gs = new GuidingStatistics();
        gs.Add(S(0, 5, 0) with { RaLimited = true });
        gs.Add(S(1, 5, 0, excluded: true) with { DecLimited = true });
        var w = gs.GetSnapshot(T0).Window;
        w.RaLimitedCount.Should().Be(1);
        w.DecLimitedCount.Should().Be(1);
    }
}
