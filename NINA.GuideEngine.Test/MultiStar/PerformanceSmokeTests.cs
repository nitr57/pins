// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.MultiStar;

/// <summary>
/// Timing smoke test on a full 1936x1216 frame. Only logs timings (bounds are very generous so the
/// test is not flaky); the Pi 5 budget is &lt; 150 ms per frame and &lt; 300 ms for AutoFind.
/// </summary>
[TestFixture]
[Category("Performance")]
public class PerformanceSmokeTests
{
    [Test]
    public void FullFrame_PerFrameProcessingAndAutoFind_Timing()
    {
        const int w = 1936, h = 1216;
        var r = new StarFieldRenderer(w, h) { Seed = 77, Background = 300, ReadNoise = 8 };
        var rng = new Random(5);
        for (int i = 0; i < 40; i++)
            r.AddStar(60 + rng.NextDouble() * (w - 120), 60 + rng.NextDouble() * (h - 120), 5000 + rng.NextDouble() * 80000, 2.5 + rng.NextDouble());
        for (int i = 0; i < 200; i++)
            r.HotPixels.Add((rng.Next(w), rng.Next(h), (ushort)rng.Next(5000, 60000)));
        var raw = r.Render();
        // unique frames with a small common drift so the secondaries are really measured (identical
        // repeated frames would make secondaries look like hot pixels: zero displacement)
        var drifted = Enumerable.Range(0, 12).Select(k => (0.15 * Math.Sin(k * 0.9), 0.12 * Math.Cos(k * 1.7))).Select((d, k) =>
        {
            var rr = new StarFieldRenderer(w, h) { Seed = 90 + k, Background = 300, ReadNoise = 8 };
            foreach (var st in r.Stars) rr.Stars.Add(st with { X = st.X + d.Item1, Y = st.Y + d.Item2 });
            rr.HotPixels.AddRange(r.HotPixels);
            return rr.Render();
        }).ToArray();

        var darkR = new StarFieldRenderer(w, h) { Seed = 78, Background = 0, ReadNoise = 8 };
        var dark = darkR.Render();
        var lib = new DarkLibrary();
        lib.Add(dark);
        var pre = new FramePreprocessor { Darks = lib, NoiseReduction = NoiseReduction.Median3x3 };

        // AutoFind (includes warm-up of the JIT on first call)
        var frame = pre.Process(raw.Clone());
        var sw = Stopwatch.StartNew();
        var tracker = new MultiStarTracker(new StarFinderOptions(), new MultiStarOptions());
        var sel = tracker.AutoSelect(frame);
        long autoFindFirst = sw.ElapsedMilliseconds;
        sel.Success.Should().BeTrue();
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            sw.Restart();
            GuideStar.AutoFind(frame, 0, 15, IntRect.Empty, 12, new StarFinderOptions());
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        double autoFind = times.Min();

        // per frame: preprocessing + tracking (guiding, multi-star)
        var frames = drifted.Select(d => d.Clone()).ToArray();
        var preTimes = new List<double>();
        var trackTimes = new List<double>();
        var usedCounts = new List<int>();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < drifted.Length; i++)
        {
            var f = frames[i % frames.Length];
            Array.Copy(drifted[i % drifted.Length].Pixels, f.Pixels, f.Pixels.Length);
            f.Pedestal = 0;
            sw.Restart();
            var pf = pre.Process(f);
            preTimes.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            var res = tracker.ProcessFrame(pf, sel.LockPosition, TrackerState.Guiding, t0.AddSeconds(2 * i));
            trackTimes.Add(sw.Elapsed.TotalMilliseconds);
            res.StarFound.Should().BeTrue();
            usedCounts.Add(res.StarsUsed);
        }

        double preMed = preTimes.Order().ElementAt(preTimes.Count / 2);
        double trackMed = trackTimes.Order().ElementAt(trackTimes.Count / 2);
        TestContext.Out.WriteLine($"stars in list: {tracker.GuideStars.Count}, max stars used per frame: {usedCounts.Max()}");
        TestContext.Out.WriteLine($"AutoSelect first call: {autoFindFirst} ms, AutoFind best of 5: {autoFind:F1} ms");
        TestContext.Out.WriteLine(
            $"preprocess (dark + median3) median: {preMed:F1} ms, tracking median: {trackMed:F2} ms (max {trackTimes.Max():F2}), total {preMed + trackMed:F1} ms");

        (preMed + trackMed).Should().BeLessThan(2000);
        autoFind.Should().BeLessThan(10000);
    }
}
