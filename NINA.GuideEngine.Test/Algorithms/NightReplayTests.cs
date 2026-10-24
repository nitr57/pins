// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// Replays a real night (the measured offsets and the corrections that went out) into Predictive on both axes. The
/// filters only estimate, so the replay shows what they learn from that night; it says nothing about guiding with them.
/// </summary>
[TestFixture]
public class NightReplayTests
{
    private sealed record Row(int Frame, double TimeSec, bool Settling, bool Dither, double RaPx, double DecPx, double RaCorrPx, double DecCorrPx);

    private static List<Row> Load(string name)
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", name);
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("frame", StringComparison.Ordinal))
            {
                continue;
            }

            var f = line.Split(',');
            double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            rows.Add(new Row(int.Parse(f[0], CultureInfo.InvariantCulture), D(1), f[2] == "1", f[3] == "1", D(4), D(5), D(6), D(7)));
        }

        return rows;
    }

    /// <summary>2026-09-27, EQMod rig, 3.11″/px, 2.5 s exposures; Predictive on both axes until about frame 650.</summary>
    [Explicit("diagnostic replay, prints what each axis learns")]
    [Test]
    public void Night20260927()
    {
        var rows = Load("night-2026-09-27.csv");
        var start = new DateTimeOffset(2026, 9, 27, 17, 56, 13, TimeSpan.Zero);
        var ra = new PredictiveAlgorithm();
        var dec = new PredictiveAlgorithm();
        ra.GuidingStarted();
        dec.GuidingStarted();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Dither)
            {
                ra.GuidingDithered(0);
                dec.GuidingDithered(0);
            }

            ra.Settling = dec.Settling = r.Settling;
            var t = start + TimeSpan.FromSeconds(r.TimeSec);
            ra.Result(r.RaPx, t);
            dec.Result(r.DecPx, t);
            ra.CorrectionApplied(r.RaCorrPx);
            dec.CorrectionApplied(r.DecCorrPx);
            if (i % 60 == 59 || i == rows.Count - 1)
            {
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"frame {r.Frame,4} {t.ToOffset(TimeSpan.FromHours(2)):HH:mm}  RA {Describe(ra)}  |  Dec {Describe(dec)}"));
            }
        }

        static string Describe(PredictiveAlgorithm a) => string.Create(CultureInfo.InvariantCulture,
            $"wander {a.State.WanderRatio,5:G3} gain {a.State.Gain:F2} seeing {a.SeeingPx * 3.11:F2}″ mount {a.WanderPx * 3.11:F2}″");
    }
}
