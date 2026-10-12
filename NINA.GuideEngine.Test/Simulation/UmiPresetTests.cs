// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.Guiding;

namespace NINA.GuideEngine.Test.Simulation;

/// <summary>
/// The UMi presets against what the guide logs of those mounts show (docs/notes/UMI-STRAIN-WAVE.md, the replay data in
/// Data/umi): the seeing's size and its correlation from frame to frame, the main lines of the RA curve, and the guide
/// star's SNR.
/// </summary>
[TestFixture]
public class UmiPresetTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A preset and what was measured on its mount's nights: exposure and frame interval; the part of the open-loop motion
    /// under 20 s on the sky (σ RA, σ Dec, lag-1..3 autocorrelation of RA, whose rebuilt motion does not depend on a pulse
    /// effect as Dec's does); the guide star's SNR; the RA lines (RA-axis arcsec)
    /// a three-hour fit should find, with their bounds.
    /// </summary>
    public sealed record Case(
        SimulatorScenario Scenario,
        double ExposureSec,
        double CycleSec,
        double RaSigma,
        double DecSigma,
        double Ac1,
        double Ac2,
        double Ac3,
        double SnrMin,
        double SnrMax,
        IReadOnlyList<(double PeriodSec, double Min, double Max)> Lines)
    {
        public override string ToString() => Scenario.Name;
    }

    private const double Turn = 86164.0905;

    public static IEnumerable<Case> Cases() =>
    [
        // the 200 and 202 lines are unresolved in three hours: the fit at 430.8 s sees their sum or difference
        new(SimulatorScenario.UMi20S, 0.5, 0.87, 0.55, 0.46, 0.50, 0.28, 0.14, 100, 140,
            [(Turn / 200, 3.0, 6.0), (Turn / 300, 1.6, 2.2), (Turn / 100, 1.2, 1.8), (106.4, 0.8, 1.2)]),
        new(SimulatorScenario.UMi20SHeavy, 0.5, 0.87, 0.51, 0.45, 0.50, 0.30, 0.18, 60, 85,
            [(Turn / 200, 2.1, 6.1), (Turn / 300, 3.5, 4.5), (Turn / 100, 1.2, 1.8), (106.4, 0.55, 0.95)]),
        new(SimulatorScenario.UMi17R, 1.0, 1.35, 0.41, 0.44, 0.48, 0.27, 0.11, 110, 190,
            [(856.5, 9.7, 11.7), (856.5 / 2, 5.2, 6.8), (Turn / 300, 2.0, 3.0), (106.8, 0.5, 1.0)]),
    ];

    // the open-loop position a guide camera would see, frame by frame: the mount's pointing error at mid-exposure plus the
    // seeing averaged over the exposure (sky arcsec)
    private static (double[] Ra, double[] Dec) OpenLoop(Case c, double hours)
    {
        var sim = new Simulator(c.Scenario);
        int star = sim.BrightestStarIndex();
        int n = (int)(hours * 3600 / c.CycleSec);
        var ra = new double[n];
        var dec = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t0 = i * c.CycleSec, t1 = t0 + c.ExposureSec;
            var p = sim.Mount.GetPointingError((t0 + t1) / 2) + sim.Sky.SeeingOffset(star, t0, t1);
            ra[i] = p.Ra;
            dec[i] = p.Dec;
        }

        return (ra, dec);
    }

    // the part under 20 s (the position minus its 20 s running mean), with the ends left out
    private static double[] HighPass(double[] x, double cycleSec)
    {
        int k = (int)Math.Round(20 / cycleSec) | 1, h = k / 2;
        var r = new List<double>();
        for (int i = h; i < x.Length - h; i++)
        {
            double m = 0;
            for (int j = i - h; j <= i + h; j++)
            {
                m += x[j];
            }

            r.Add(x[i] - m / k);
        }

        return [.. r];
    }

    private static double Sigma(double[] x)
    {
        double m = x.Average();
        return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / x.Length);
    }

    private static double Autocorrelation(double[] x, int lag)
    {
        double m = x.Average(), c0 = 0, c = 0;
        for (int i = 0; i < x.Length; i++)
        {
            c0 += (x[i] - m) * (x[i] - m);
            if (i >= lag)
            {
                c += (x[i] - m) * (x[i - lag] - m);
            }
        }

        return c / c0;
    }

    [TestCaseSource(nameof(Cases))]
    public void The_seeing_is_as_large_and_as_correlated_as_measured(Case c)
    {
        var (ra, dec) = OpenLoop(c, 2);
        var hr = HighPass(ra, c.CycleSec);
        var hd = HighPass(dec, c.CycleSec);
        double Ac(int lag) => Autocorrelation(hr, lag);
        TestContext.Out.WriteLine(string.Create(Inv,
            $"{c}: σ RA {Sigma(hr):F2}″ (measured {c.RaSigma:F2}), σ Dec {Sigma(hd):F2}″ ({c.DecSigma:F2}), lag 1/2/3 {Ac(1):F2}/{Ac(2):F2}/{Ac(3):F2} ({c.Ac1:F2}/{c.Ac2:F2}/{c.Ac3:F2})"));

        // Dec carries no periodic error: its part under 20 s is the seeing alone
        Sigma(hd).Should().BeApproximately(c.DecSigma, 0.15 * c.DecSigma);
        Sigma(hr).Should().BeApproximately(c.RaSigma, 0.25 * c.RaSigma);
        // the presets share one sky, fitted to the 0.87 s nights; its cells make the correlation fall off linearly, so at
        // the 17R's 1.35 s frames it is short of the measured tail at lags 2 and 3 (0.16 / −0.04 against 0.27 / 0.11)
        Ac(1).Should().BeApproximately(c.Ac1, 0.1);
        Ac(2).Should().BeApproximately(c.Ac2, 0.15);
        Ac(3).Should().BeApproximately(c.Ac3, 0.16);
    }

    [TestCaseSource(nameof(Cases))]
    public void The_main_lines_of_the_RA_curve_are_the_measured_ones(Case c)
    {
        // the mount alone, three hours: least-squares amplitudes (RA-axis arcsec) at the lines, with level and drift
        var sim = new Simulator(c.Scenario);
        double cos = Math.Cos(c.Scenario.Mount.DeclinationDeg * Math.PI / 180);
        var t = Enumerable.Range(0, 3 * 3600 / 2).Select(i => i * 2.0).ToArray();
        var y = t.Select(s => sim.Mount.PeriodicErrorAt(s) / cos).ToArray();
        var amplitudes = Fit(t, y, c.Lines.Select(l => l.PeriodSec).ToArray());
        TestContext.Out.WriteLine(c + ": " + string.Join(", ", c.Lines.Zip(amplitudes, (l, a) => string.Create(Inv, $"{l.PeriodSec:F1} s ±{a:F2}″"))));
        for (int i = 0; i < c.Lines.Count; i++)
        {
            amplitudes[i].Should().BeInRange(c.Lines[i].Min, c.Lines[i].Max, $"the line at {c.Lines[i].PeriodSec:F1} s");
        }
    }

    [TestCaseSource(nameof(Cases))]
    public async Task The_guide_star_has_the_logged_SNR(Case c)
    {
        var snr = new List<double>();
        var hysteresis = new AlgorithmSettings(GuideAlgorithmKind.Hysteresis);
        await ClosedLoopTests.GuidedRun(c.Scenario, hysteresis, new AlgorithmSettings(GuideAlgorithmKind.ResistSwitch), e =>
        {
            if (e is GuideStepEvent { Snr: > 0 } g)
            {
                snr.Add(g.Snr);
            }
        }, s => s with { ExposureMs = c.ExposureSec * 1000 }, TimeSpan.FromMinutes(15));
        snr.Should().NotBeEmpty($"guide steps with a star ({c}: {Probe(c)})");
        double median = snr.Order().ElementAt(snr.Count / 2);
        TestContext.Out.WriteLine(string.Create(Inv, $"{c}: median SNR {median:F0} (logged {c.SnrMin:F0}–{c.SnrMax:F0})"));
        median.Should().BeInRange(0.8 * c.SnrMin, 1.2 * c.SnrMax);
    }

    // the brightest star of a first frame: peak and saturation, for a failing SNR test
    private static string Probe(Case c)
    {
        var sim = new Simulator(c.Scenario);
        var frame = sim.CaptureAsync(c.ExposureSec * 1000).GetAwaiter().GetResult();
        int i = sim.BrightestStarIndex();
        var s = sim.LastFrameTruth!.Stars[i];
        int peak = 0;
        for (int y = (int)s.Y - 6; y <= (int)s.Y + 6; y++)
        {
            for (int x = (int)s.X - 6; x <= (int)s.X + 6; x++)
            {
                peak = Math.Max(peak, frame.Pixels[y * frame.Width + x]);
            }
        }

        return string.Create(Inv, $"brightest star peak {peak} ADU of {c.Scenario.Camera.MaxAdu}");
    }

    private static double[] Fit(double[] t, double[] y, double[] periods)
    {
        int p = 2 + 2 * periods.Length;
        var ata = new double[p, p];
        var aty = new double[p];
        var row = new double[p];
        for (int i = 0; i < t.Length; i++)
        {
            row[0] = 1;
            row[1] = t[i] / 1000;
            for (int k = 0; k < periods.Length; k++)
            {
                row[2 + 2 * k] = Math.Cos(2 * Math.PI * t[i] / periods[k]);
                row[3 + 2 * k] = Math.Sin(2 * Math.PI * t[i] / periods[k]);
            }

            for (int a = 0; a < p; a++)
            {
                aty[a] += row[a] * y[i];
                for (int b = 0; b < p; b++)
                {
                    ata[a, b] += row[a] * row[b];
                }
            }
        }

        var x = Solve(ata, aty);
        return periods.Select((_, k) => Math.Sqrt(x[2 + 2 * k] * x[2 + 2 * k] + x[3 + 2 * k] * x[3 + 2 * k])).ToArray();
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++)
            {
                if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c]))
                {
                    piv = r;
                }
            }

            for (int k = 0; k < n; k++)
            {
                (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
            }

            (x[c], x[piv]) = (x[piv], x[c]);
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int k = c; k < n; k++)
                {
                    m[r, k] -= f * m[c, k];
                }

                x[r] -= f * x[c];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            for (int k = r + 1; k < n; k++)
            {
                x[r] -= m[r, k] * x[k];
            }

            x[r] /= m[r, r];
        }

        return x;
    }

    [Explicit("diagnostic: guides the presets in the simulator with the PHD2 settings of the logged nights and with Predictive")]
    [TestCaseSource(nameof(Cases))]
    public async Task Guiding_the_presets(Case c)
    {
        var classic = (new AlgorithmSettings(GuideAlgorithmKind.Hysteresis, new Dictionary<string, double> { ["aggression"] = 0.9, ["hysteresis"] = 0.1, ["minMove"] = 0.1 }),
            new AlgorithmSettings(GuideAlgorithmKind.ResistSwitch, new Dictionary<string, double> { ["aggression"] = 0.9, ["minMove"] = 0.1, ["fastSwitch"] = 1 }));
        var predictive = (new AlgorithmSettings(GuideAlgorithmKind.Predictive, new Dictionary<string, double> { ["pace"] = 0.5, ["periodicError"] = 1 }),
            new AlgorithmSettings(GuideAlgorithmKind.Predictive, new Dictionary<string, double> { ["pace"] = 0.5 }));
        foreach (var (name, (ra, dec)) in new[] { ("PHD2 settings of the nights", classic), ("Predictive, pace 0.5", predictive) })
        {
            var r = await ClosedLoopTests.GuidedRun(c.Scenario, ra, dec, null, s => s with { ExposureMs = c.ExposureSec * 1000 }, TimeSpan.FromMinutes(40));
            TestContext.Out.WriteLine(string.Create(Inv, $"{c}, {name}: true RMS {r.Rms:F2}″ (RA {r.RaRms:F2}″, Dec {r.DecRms:F2}″); {r.Diagnostics}"));
        }
    }
}
