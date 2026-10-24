// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// The ProxiSky UMi nights (Data/umi): the data themselves, the periodic-error estimator on their open-loop RA, and the
/// guide algorithms in a closed loop driven by the mounts' rebuilt motion.
/// </summary>
[TestFixture]
public class UmiReplayTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// How far a calibrated pulse moves the star (Dec; RA is 1.0 within ±0.04 on all four), from the dithers of all nights by
    /// two-stage least squares with the dither as instrument (Data/umi/README.md).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, double> DecPulseEffect = new Dictionary<string, double>
    {
        ["UMi17R"] = 0.71,
        ["UMi17S"] = 0.66,
        ["UMi20S-1"] = 0.86,
        ["UMi20S-2"] = 0.90,
    };

    [Test]
    public void Every_night_loads_and_its_open_loop_motion_is_continuous_over_the_dithers()
    {
        var nights = UmiNight.All().ToList();
        nights.Should().HaveCount(13);
        nights.Select(n => n.Mount).Distinct().Should().BeEquivalentTo("UMi17R", "UMi17S", "UMi20S-1", "UMi20S-2");
        foreach (var night in nights)
        {
            night.Runs.Should().NotBeEmpty();
            var jumps = new List<double>();
            foreach (var run in night.Runs)
            {
                var ra = run.OpenLoop(ra: true);
                for (int i = 1; i < run.Frames.Count - 1; i++)
                {
                    if (run.Frames[i].Dither)
                    {
                        // the open-loop position does not see the dither: what the lock moved, the offset gave back
                        jumps.Add(Math.Abs(ra[i] - ra[i - 1]));
                    }
                }
            }

            if (jumps.Count > 0)
            {
                jumps.Order().ElementAt(jumps.Count / 2).Should().BeLessThan(1.0, $"{night.Mount} {night.Date}: median open-loop step at a dither");
            }
        }
    }

    [Explicit("diagnostic replay, prints what the periodic-error estimator makes of each night")]
    [TestCase(0)]
    [TestCase(100)]
    public void Periodic_error_estimator_on_the_nights(int teeth)
    {
        var o = TestContext.Out;
        foreach (var night in UmiNight.All())
        {
            var pe = new PeriodicErrorEstimator(teeth > 0 ? teeth : null);
            var all = night.Runs.SelectMany(r => r.Frames.Zip(r.OpenLoop(ra: true))).ToList();
            var diffs = all.Skip(1).Select((p, i) => Math.Abs(p.Second - all[i].Second)).Order().ToList();
            double seeing = Math.Pow(diffs[diffs.Count / 2] / 0.954, 2);
            bool first = true;
            foreach (var run in night.Runs)
            {
                if (!first)
                {
                    pe.StartSegment();
                }

                first = false;
                var y = run.OpenLoop(ra: true);
                for (int i = 0; i < run.Frames.Count; i++)
                {
                    var f = run.Frames[i];
                    var ctx = new PeriodicErrorContext
                    {
                        AxisHours = run.AxisHours(f),
                        DeclinationDeg = run.DeclinationDeg,
                        PixelScale = night.PixelScale,
                        PierSide = run.PierSide == "West" ? PierSide.West : PierSide.East,
                    };
                    pe.Observe(f.TimeSec, y[i], ctx, !f.Settling, seeing, 0.01 * seeing, 1e-10 * seeing);
                }
            }

            o.WriteLine($"=== {night.Mount} {night.Date} teeth={(teeth > 0 ? teeth.ToString(Inv) : "detect")}");
            foreach (var n in pe.TakeNotes().Where(n => n.Contains("detected") || n.Contains("stable") || n.Contains("worm")))
            {
                o.WriteLine("  " + n);
            }

            o.WriteLine($"  END: {pe.Describe()}");
        }
    }

    /// <summary>A guide algorithm set for both axes, with the pulse model and the pace of the Predictive algorithm.</summary>
    private sealed record Setup(string Name, Func<UmiRun, double, (IGuideAlgorithm Ra, IGuideAlgorithm Dec)> Create, bool PulseModel);

    private static IEnumerable<Setup> Setups()
    {
        double P(UmiRun r, string k, double fallback) =>
            double.TryParse(r.Meta.GetValueOrDefault(k, ""), NumberStyles.Float, Inv, out double v) ? v : fallback;

        yield return new Setup("Classic, PHD2 profile of the night", (r, ps) =>
        {
            var ra = new HysteresisAlgorithm();
            ra.TrySetParam("aggression", P(r, "ra_gain", 0.7));
            ra.TrySetParam("hysteresis", 0.1);
            ra.TrySetParam("minMove", P(r, "ra_min_move_px", 0.1));
            var dec = new ResistSwitchAlgorithm();
            dec.TrySetParam("aggression", P(r, "dec_aggression_pct", 100) / 100);
            dec.TrySetParam("minMove", P(r, "dec_min_move_px", 0.1));
            dec.TrySetParam("fastSwitch", 1);
            return (ra, dec);
        }, false);
        yield return new Setup("Classic, soft (RA 0.5, Dec 40 %, min move 0.3\")", (r, ps) =>
        {
            var ra = new HysteresisAlgorithm();
            ra.TrySetParam("aggression", 0.5);
            ra.TrySetParam("hysteresis", 0.1);
            ra.TrySetParam("minMove", 0.3 / ps);
            var dec = new ResistSwitchAlgorithm();
            dec.TrySetParam("aggression", 0.4);
            dec.TrySetParam("minMove", 0.3 / ps);
            dec.TrySetParam("fastSwitch", 1);
            return (ra, dec);
        }, false);
        foreach (double pace in new[] { 0.5, 1.0 })
        {
            foreach (bool pulseModel in new[] { false, true })
            {
                double p = pace;
                yield return new Setup($"Predictive, pace {p:F1}{(pulseModel ? ", pulse model" : "")}", (r, ps) =>
                {
                    var ra = new PredictiveAlgorithm(periodicError: false);
                    ra.TrySetParam("pace", p);
                    var dec = new PredictiveAlgorithm();
                    dec.TrySetParam("pace", p);
                    return (ra, dec);
                }, pulseModel);
            }
        }

        yield return new Setup("Predictive, pace 0.5, pulse model, PE detect", (r, ps) =>
        {
            var ra = new PredictiveAlgorithm(periodicError: true);
            ra.TrySetParam("pace", 0.5);
            var dec = new PredictiveAlgorithm();
            dec.TrySetParam("pace", 0.5);
            return (ra, dec);
        }, true);
    }

    /// <summary>
    /// Closed loop over a run's rebuilt motion. The open-loop position (seeing included, as the camera saw it) is what the
    /// mount does on its own; the algorithm sees it minus the lock position and the corrections it made, which reach the
    /// star with the axis's pulse effect, a share <paramref name="lateDec"/> / <paramref name="lateRa"/> a frame late. A dither
    /// moves the lock and is recentred at once (like a fast recenter). The true error is the 20 s mean of the open-loop
    /// position (the seeing averaged out) minus lock and corrections: what the long exposure sees.
    /// </summary>
    private static (double TrueRa, double TrueDec, double MeasRa, double MeasDec, int N) Simulate(UmiRun run, double ps, double decEffect,
        double lateRa, double lateDec, Setup setup)
    {
        var (raAlg, decAlg) = setup.Create(run, ps);
        raAlg.GuidingStarted();
        decAlg.GuidingStarted();
        var yRa = run.OpenLoop(ra: true);
        var yDec = run.OpenLoop(ra: false, decEffect);
        var tRa = Mean20(run, yRa);
        var tDec = Mean20(run, yDec);
        int n = run.Frames.Count;
        var cRa = new double[n + 2];
        var cDec = new double[n + 2];
        double lockRa = 0, lockDec = 0;
        double raMax = run.MaxRaMs / 1000 * run.RaRatePxPerSec, decMax = run.MaxDecMs / 1000 * run.DecRatePxPerSec;
        var start = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);
        int settleUntil = 10;
        double sTr = 0, sTd = 0, sMr = 0, sMd = 0, mTr = 0, mTd = 0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            var f = run.Frames[i];
            if (i > 0)
            {
                cRa[i] += cRa[i - 1];
                cDec[i] += cDec[i - 1];
            }

            if (f.Dither)
            {
                lockRa += f.DitherRaPx;
                lockDec += f.DitherDecPx;
                raAlg.GuidingDithered(f.DitherRaPx);
                decAlg.GuidingDithered(f.DitherDecPx);
                cRa[i] -= f.DitherRaPx;
                cDec[i] -= f.DitherDecPx;
                raAlg.DirectMoveApplied(-f.DitherRaPx);
                decAlg.DirectMoveApplied(-f.DitherDecPx);
                settleUntil = i + 10;
            }

            bool settling = i < settleUntil;
            foreach (var a in new[] { raAlg, decAlg })
            {
                if (a is PredictiveAlgorithm p)
                {
                    p.Settling = settling;
                }
            }

            if (raAlg is PredictiveAlgorithm pr)
            {
                pr.PeriodicContext = new PeriodicErrorContext
                {
                    AxisHours = run.AxisHours(f),
                    DeclinationDeg = run.DeclinationDeg,
                    PixelScale = ps,
                    PierSide = run.PierSide == "West" ? PierSide.West : PierSide.East,
                };
            }

            double eRa = yRa[i] - lockRa - cRa[i];
            double eDec = yDec[i] - lockDec - cDec[i];
            var time = start.AddSeconds(f.TimeSec);
            double wantRa = raAlg.Result(eRa, time);
            double wantDec = decAlg.Result(eDec, time);
            double sentRa = Send(wantRa, 1.0, raMax, run.RaRatePxPerSec, setup.PulseModel);
            double sentDec = Send(wantDec, decEffect, decMax, run.DecRatePxPerSec, setup.PulseModel);

            // the algorithm is told the correction that went out; with the pulse model, the motion it can expect
            raAlg.CorrectionApplied(sentRa);
            decAlg.CorrectionApplied(setup.PulseModel ? sentDec * decEffect : sentDec);
            double effRa = sentRa, effDec = sentDec * decEffect;
            cRa[i + 1] += (1 - lateRa) * effRa;
            cRa[i + 2] += lateRa * effRa;
            cDec[i + 1] += (1 - lateDec) * effDec;
            cDec[i + 2] += lateDec * effDec;

            if (!settling && !f.Settling && f.TimeSec - run.Frames[0].TimeSec > 120)
            {
                double tr = tRa[i] - lockRa - cRa[i], td = tDec[i] - lockDec - cDec[i];
                sTr += tr * tr;
                sTd += td * td;
                mTr += tr;
                mTd += td;
                sMr += eRa * eRa;
                sMd += eDec * eDec;
                count++;
            }
        }

        double c = Math.Max(count, 1);
        return (Math.Sqrt(sTr / c - (mTr / c) * (mTr / c)) * ps, Math.Sqrt(sTd / c - (mTd / c) * (mTd / c)) * ps,
            Math.Sqrt(sMr / c) * ps, Math.Sqrt(sMd / c) * ps, count);
    }

    // the pulse that goes out for a wanted correction (px at the calibrated rate): with the pulse model it is lengthened by
    // 1 / effect; limited to the max duration; under 20 ms rounded to 0 or 20 ms (MinPulseMs)
    private static double Send(double want, double effect, double maxPx, double ratePxPerSec, bool pulseModel)
    {
        double px = pulseModel ? want / effect : want;
        px = Math.Clamp(px, -maxPx, maxPx);
        double ms = Math.Abs(px) / ratePxPerSec * 1000;
        if (ms < 20)
        {
            px = ms < 10 ? 0 : Math.Sign(px) * 0.02 * ratePxPerSec;
        }

        return px;
    }

    private static double[] Mean20(UmiRun run, double[] y)
    {
        var m = new double[y.Length];
        int lo = 0, hi = 0;
        double sum = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double t = run.Frames[i].TimeSec;
            while (hi < y.Length && run.Frames[hi].TimeSec <= t + 10)
            {
                sum += y[hi++];
            }

            while (run.Frames[lo].TimeSec < t - 10)
            {
                sum -= y[lo++];
            }

            m[i] = sum / (hi - lo);
        }

        return m;
    }

    [Explicit("diagnostic closed-loop replay, prints the guiding each algorithm set would have reached on the mounts")]
    [TestCase(0.1, 0.35)]
    [TestCase(0.0, 0.0)]
    public void Closed_loop_on_the_mounts(double lateRa, double lateDec)
    {
        var o = TestContext.Out;
        var setups = Setups().ToList();
        o.WriteLine(string.Create(Inv, $"late share RA {lateRa:F2}, Dec {lateDec:F2}; true RMS (20 s mean) RA / Dec ″ on the sky, measured RMS RA / Dec ″, frame-weighted over runs ≥ 20 min"));
        foreach (string mount in new[] { "UMi20S-1", "UMi20S-2", "UMi17S", "UMi17R" })
        {
            var acc = setups.ToDictionary(s => s.Name, _ => new double[5]);
            foreach (var night in UmiNight.All(mount))
            {
                double ps = night.PixelScale;
                foreach (var run in night.Runs.Where(r => r.SpanSec >= 1200 && r.Frames.Max(f => Math.Abs(f.RaPx)) * ps < 20))
                {
                    foreach (var s in setups)
                    {
                        var (tr, td, mr, md, cnt) = Simulate(run, ps, DecPulseEffect[mount], lateRa, lateDec, s);
                        var a = acc[s.Name];
                        a[0] += tr * tr * cnt;
                        a[1] += td * td * cnt;
                        a[2] += mr * mr * cnt;
                        a[3] += md * md * cnt;
                        a[4] += cnt;
                    }
                }
            }

            o.WriteLine($"== {mount}");
            foreach (var s in setups)
            {
                var a = acc[s.Name];
                o.WriteLine(string.Create(Inv,
                    $"  {s.Name,-48} true {Math.Sqrt(a[0] / a[4]):F3} / {Math.Sqrt(a[1] / a[4]):F3}  total {Math.Sqrt((a[0] + a[1]) / a[4]):F3}   measured {Math.Sqrt(a[2] / a[4]):F3} / {Math.Sqrt(a[3] / a[4]):F3}"));
            }
        }
    }
}
