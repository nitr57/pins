// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// One guide cycle, ms, from the start of one capture to the start of the next.
/// </summary>
/// <param name="CycleMs">The whole cycle.</param>
/// <param name="ExposureMs">The exposure that was asked for.</param>
/// <param name="CameraMs">The capture beyond the exposure: starting it, reading out and transferring the frame. The
/// engine sees only when the frame arrives, not when the exposure ended.</param>
/// <param name="ProcessingMs">Frame arrived to frame processed (preprocessing, stars, algorithms).</param>
/// <param name="FrameToPulseMs">Frame arrived to the first pulse handed to the guide output; null without pulses.
/// The command still has to reach the mount (about 9 ms on a 9600 baud serial line).</param>
/// <param name="PulseMs">Pulses handed over to the output reporting them done (0 without pulses).</param>
/// <param name="OtherMs">The rest of the cycle: events, logs, the mount check before the next capture.</param>
public sealed record CycleTimes(
    double CycleMs,
    double ExposureMs,
    double CameraMs,
    double ProcessingMs,
    double? FrameToPulseMs,
    double PulseMs,
    double OtherMs);

/// <summary>The guide loop's timing: the last cycle, medians of recent cycles and the frame rate they give.</summary>
/// <param name="Last">The last complete cycle.</param>
/// <param name="Median">Medians over the last <see cref="Cycles"/> cycles, each field on its own;
/// <see cref="CycleTimes.FrameToPulseMs"/> and <see cref="CycleTimes.PulseMs"/> over the cycles that sent pulses.</param>
/// <param name="FramesPerSecond">1000 / median cycle.</param>
/// <param name="Cycles">How many cycles the medians cover.</param>
public sealed record GuideTiming(CycleTimes Last, CycleTimes Median, double FramesPerSecond, int Cycles);

/// <summary>
/// Measures the guide loop from timestamps the loop sets: a cycle is complete when the next capture starts. A cycle
/// broken off (a failed capture, a pause, the loop stopping) is dropped, so it never shows as one long cycle.
/// </summary>
internal sealed class CycleTimer
{
    /// <summary>Cycles the medians cover.</summary>
    public const int Window = 20;

    private readonly object sync = new();
    private readonly Queue<CycleTimes> recent = new();
    private DateTimeOffset? captureStart;
    private DateTimeOffset? frameReady;
    private DateTimeOffset? processed;
    private DateTimeOffset? pulsesStart;
    private DateTimeOffset? pulsesDone;
    private double exposureMs;
    private GuideTiming? timing;

    public GuideTiming? Timing
    {
        get
        {
            lock (sync)
            {
                return timing;
            }
        }
    }

    /// <summary>A capture starts: completes the cycle before it, if it got as far as a frame.</summary>
    public void CaptureStarting(DateTimeOffset now, double requestedExposureMs)
    {
        lock (sync)
        {
            if (captureStart is { } start && frameReady is { } ready)
            {
                Complete(start, ready, now);
            }

            captureStart = now;
            exposureMs = requestedExposureMs;
            frameReady = processed = pulsesStart = pulsesDone = null;
        }
    }

    public void FrameReady(DateTimeOffset now) => Mark(ref frameReady, now);

    public void Processed(DateTimeOffset now) => Mark(ref processed, now);

    public void PulsesStarting(DateTimeOffset now) => Mark(ref pulsesStart, now);

    public void PulsesDone(DateTimeOffset now) => Mark(ref pulsesDone, now);

    /// <summary>Drops the cycle in progress; the last timing stays.</summary>
    public void Discard()
    {
        lock (sync)
        {
            captureStart = frameReady = processed = pulsesStart = pulsesDone = null;
        }
    }

    private void Mark(ref DateTimeOffset? field, DateTimeOffset now)
    {
        lock (sync)
        {
            if (captureStart is not null)
            {
                field = now;
            }
        }
    }

    // Caller holds sync.
    private void Complete(DateTimeOffset start, DateTimeOffset ready, DateTimeOffset next)
    {
        var processedAt = processed ?? ready;
        double cycle = Ms(next - start);
        double camera = Math.Max(0, Ms(ready - start) - exposureMs);
        double processing = Ms(processedAt - ready);
        double? frameToPulse = pulsesStart is { } ps ? Ms(ps - ready) : null;
        double pulse = pulsesStart is { } p && pulsesDone is { } pd ? Ms(pd - p) : 0;
        var lastMark = pulsesDone ?? pulsesStart ?? processedAt;
        double other = Ms(next - lastMark);
        var times = new CycleTimes(cycle, exposureMs, camera, processing, frameToPulse, pulse, other);

        recent.Enqueue(times);
        while (recent.Count > Window)
        {
            recent.Dequeue();
        }

        var median = new CycleTimes(
            Median(recent.Select(t => t.CycleMs)),
            Median(recent.Select(t => t.ExposureMs)),
            Median(recent.Select(t => t.CameraMs)),
            Median(recent.Select(t => t.ProcessingMs)),
            recent.Any(t => t.FrameToPulseMs is not null) ? Median(recent.Where(t => t.FrameToPulseMs is not null).Select(t => t.FrameToPulseMs!.Value)) : null,
            Median(recent.Where(t => t.FrameToPulseMs is not null).Select(t => t.PulseMs).DefaultIfEmpty(0)),
            Median(recent.Select(t => t.OtherMs)));
        timing = new GuideTiming(times, median, median.CycleMs > 0 ? 1000.0 / median.CycleMs : 0, recent.Count);
    }

    private static double Ms(TimeSpan span) => span.TotalMilliseconds;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
