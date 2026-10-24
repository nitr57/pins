// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.Simulation;
using static NINA.GuideEngine.Test.Incidents.IncidentRecorderTests;

namespace NINA.GuideEngine.Test.Benchmarks;

/// <summary>What the flight recorder costs while guiding: buffer size and time per frame.</summary>
[TestFixture]
[NonParallelizable]
[Category("Benchmark")]
public class IncidentRecorderBenchmarks
{
    /// <summary>Cost targets of the flight recorder (docs/INCIDENTS.md §6): buffer below 60 MB while guiding, a few ms per frame (1936×1216, 1 s exposures).</summary>
    [Test]
    [Explicit("cost measurement; prints the buffer size and the time per frame")]
    public async Task Cost_of_recording_at_1_s_exposures()
    {
        using var h = new Harness(SimulatorScenario.GoodMount, s => s with { ExposureMs = 1000 });
        long peak = 0;
        h.OnEvent = e =>
        {
            if (e is GuideStepEvent)
            {
                peak = Math.Max(peak, h.Guider.IncidentRecorder!.BufferedBytes);
            }
        };
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));
        (await h.Incidents()).Should().BeEmpty();

        // the recorder alone on rendered frames (the guide loop does the same per frame)
        var frames = Enumerable.Range(0, 8).Select(i => h.Sim.Camera.Render(1000 + i, 1000, 1, default, 100000 + i)).ToList();
        var recorder = h.Guider.IncidentRecorder!;
        var template = h.LastGuidingRecord ?? throw new InvalidOperationException("no guide frame recorded");
        var info = new IncidentFrameInfo(default, null, h.Guider.PixelScale, 15);
        var times = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            var record = template with { Frame = 1_000_000 + i, Time = template.Time.AddSeconds(i + 1) };
            var sw = Stopwatch.StartNew();
            recorder.OnFrame(record, frames[i % frames.Count], info);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        var steady = times.Skip(20).OrderBy(t => t).ToList();
        double mean = steady.Average(), p95 = steady[(int)(steady.Count * 0.95)];
        TestContext.Out.WriteLine($"buffer while guiding: peak {peak / 1e6:F1} MB, now {recorder.BufferedBytes / 1e6:F1} MB; recorder per frame: mean {mean:F2} ms, p95 {p95:F2} ms, context binning {IncidentRecorder.ContextBinning(1936, 1216)}");
        peak.Should().BeLessThan(60_000_000);
        mean.Should().BeLessThan(5);
    }
}
