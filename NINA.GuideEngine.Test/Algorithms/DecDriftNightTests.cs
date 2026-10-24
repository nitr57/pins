// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// Dec guide mode Drift on real nights of one EQMod mount (Data/dec-drift-*.csv, from its guide logs): the frames, pulses
/// and dithers are replayed into <see cref="DecDriftEstimator"/> the way the guider feeds it, and
/// <see cref="DecDirectionPolicy"/> tells which direction Drift would have picked. The mount drifts about +1″/min in Dec
/// (corrected by South pulses) at 3.11″/px, about 0.005 px/s.
/// </summary>
[TestFixture]
public class DecDriftNightTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 20, 13, 33, TimeSpan.Zero);

    [Test]
    public void Tonight_in_auto_the_one_sided_drift_shows_within_minutes()
    {
        // 22:43-23:43 with Auto and Predictive: 37 % of the Dec pulses reversed, and 10 ms reversals left the motor running
        var replay = Night.Load("dec-drift-night-2026-09-26.csv").Replay();
        Print(replay);

        var first = replay.Frames.First(f => f.Drift is { } d && d.Sigmas >= DecDirectionPolicy.SignificanceSigmas);
        first.Minutes.Should().BeLessThan(5);
        first.Drift!.Value.PxPerSec.Should().BeInRange(0.003, 0.009, "about +1″/min, corrected by South pulses");
        replay.PickedMinutes(DecGuideDirection.South).Should().BeLessThan(5);
        replay.Notes.Should().NotContain(n => n.Note.Message.Contains("North only"));

        var estimates = replay.Frames.Where(f => f.Drift is not null).Select(f => f.Drift!.Value.PxPerSec).Order().ToList();
        estimates.Count(d => d > 0).Should().BeGreaterThan(estimates.Count * 95 / 100, "the drift is one-sided all hour");
        estimates[estimates.Count / 2].Should().BeInRange(0.004, 0.008);
        replay.Estimator.Jumps.Should().BeGreaterThan(5, "the motor runs after very short reversals are no drift");
        replay.Estimator.BacklashPx.Should().BeLessThanOrEqualTo(1, "about 200 ms (0.5 px) per reversal");
    }

    [Test]
    public void The_south_night_keeps_south()
    {
        // 25 Sept, the same mount and target guided South only: the drift is as clear, and nothing reverses
        var replay = Night.Load("dec-drift-south-2026-09-25.csv").Replay();
        Print(replay);

        replay.PickedMinutes(DecGuideDirection.South).Should().BeLessThan(5);
        replay.Notes.Where(n => n.Note.Kind == DecDirectionNoteKind.Switch).Should().ContainSingle("South all session");
        var estimates = replay.Frames.Where(f => f.Drift is not null).Select(f => f.Drift!.Value.PxPerSec).Order().ToList();
        estimates.Should().OnlyContain(d => d > 0);
        estimates[estimates.Count / 2].Should().BeInRange(0.004, 0.009);
        replay.Estimator.Jumps.Should().BeLessThanOrEqualTo(1, "a seeing outlier at most");
    }

    private static void Print(Replay replay)
    {
        foreach (var f in replay.Frames.Where((_, i) => i % 50 == 0))
        {
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{f.Minutes,5:F1} min | {(f.Drift is { } d ? $"{d.PxPerSec * 1000,6:F2} ± {d.SigmaPxPerSec * 1000:F2} mpx/s ({d.Sigmas,4:F1} σ, floor {DecDirectionPolicy.FloorPxPerSec(d) * 1000:F2})" : "-"),-52} | dead band {f.BacklashPx:0.#} px | {f.Direction}"));
        }

        var estimates = replay.Frames.Where(f => f.Drift is not null).Select(f => f.Drift!.Value.PxPerSec).Order().ToList();
        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"median drift {estimates[estimates.Count / 2] * 1000:F2} mpx/s, {replay.Estimator.Jumps} jumps, dead band {replay.Estimator.BacklashPx} px"));
        foreach (var n in replay.Notes.Where(n => n.Note.Kind != DecDirectionNoteKind.Summary))
        {
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n.Minutes:F1} min {n.Note.Kind}: {n.Note.Message}"));
        }
    }

    /// <summary>A replayed frame: the estimate and the direction after it.</summary>
    private sealed record Frame(double Minutes, DriftEstimate? Drift, double BacklashPx, DecGuideDirection Direction);

    private sealed record Replay(List<Frame> Frames, List<(double Minutes, DecDirectionNote Note)> Notes, DecDriftEstimator Estimator)
    {
        /// <summary>When <paramref name="direction"/> was first picked (minutes), infinity when never.</summary>
        public double PickedMinutes(DecGuideDirection direction) =>
            Frames.FirstOrDefault(f => f.Direction == direction)?.Minutes ?? double.PositiveInfinity;
    }

    /// <summary>One guide-log row: the measured Dec offset (null when the star was lost), the Dec pulse sent after it.</summary>
    private sealed record Row(double T, double? DecPx, int PulseMs, bool Settling, double DitherPx);

    /// <summary>A guide-log extract: 3.11″/px, calibrated Dec rate 2.419 px/s.</summary>
    private sealed record Night(List<Row> Rows)
    {
        public const double PixelScale = 3.11;
        public const double DecRatePxPerMs = 2.419 / 1000;

        public static Night Load(string file)
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", file);
            var rows = File.ReadLines(path)
                .Where(l => l.Length > 0 && char.IsDigit(l[0]))
                .Select(l => l.Split(','))
                .Select(p => new Row(
                    double.Parse(p[0], CultureInfo.InvariantCulture),
                    p[1].Length > 0 ? double.Parse(p[1], CultureInfo.InvariantCulture) : null,
                    int.Parse(p[2], CultureInfo.InvariantCulture),
                    p[3] == "1",
                    double.Parse(p[4], CultureInfo.InvariantCulture)))
                .ToList();
            return new Night(rows);
        }

        /// <summary>
        /// Feeds the estimator as the guider does (dither, then the frame unless settling, then the pulse that went out),
        /// both directions guided (no reversal guard: the logs are Auto or South), and the policy every frame.
        /// </summary>
        public Replay Replay()
        {
            var estimator = new DecDriftEstimator();
            var policy = new DecDirectionPolicy();
            var frames = new List<Frame>();
            var notes = new List<(double, DecDirectionNote)>();
            double start = Rows[0].T;
            foreach (var row in Rows)
            {
                var time = T0.AddSeconds(row.T);
                double minutes = (row.T - start) / 60;
                if (row.DitherPx != 0 || row.DecPx is null)
                {
                    estimator.Break();
                }

                if (row.DecPx is not { } dec)
                {
                    continue;
                }

                if (!row.Settling)
                {
                    estimator.Add(time, dec);
                }

                var drift = estimator.Estimate();
                policy.Update(time, drift, dec, row.Settling, PixelScale);
                notes.AddRange(policy.TakeNotes().Select(n => (minutes, n)));
                frames.Add(new Frame(minutes, drift, estimator.BacklashPx, policy.Direction));
                if (row.PulseMs != 0)
                {
                    estimator.CorrectionApplied(row.PulseMs * DecRatePxPerMs);
                }
            }

            return new Replay(frames, notes, estimator);
        }
    }
}
