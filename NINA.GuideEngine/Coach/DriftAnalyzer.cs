// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Coach;

/// <summary>One drift sample: seconds since the start and mount-axis offsets (px).</summary>
public readonly record struct DriftSample(double T, double RaPx, double DecPx, double Snr);

/// <summary>Result of <see cref="DriftAnalyzer.Analyze"/> in px and arcsec.</summary>
public sealed record DriftAnalysis
{
    public int Samples { get; init; }

    public double DurationSeconds { get; init; }

    public double FrameIntervalSeconds { get; init; }

    public double? SnrAvg { get; init; }

    /// <summary>High-frequency (seeing) RMS per axis, px.</summary>
    public double SeeingRaPx { get; init; }

    public double SeeingDecPx { get; init; }

    public double SeeingRaArcsec { get; init; }

    public double SeeingDecArcsec { get; init; }

    public double SeeingTotalArcsec { get; init; }

    /// <summary>Peak-to-peak of the smoothed RA motion, arcsec.</summary>
    public double RaPeakToPeakArcsec { get; init; }

    /// <summary>Maximum RA drift rate of the smoothed motion (or of the fitted sinusoid + drift), arcsec/s.</summary>
    public double RaMaxRateArcsecPerSec { get; init; }

    /// <summary>Linear drift per axis, px/s (mount axes; used to correct pulse measurements).</summary>
    public double RaDriftPxPerSec { get; init; }

    public double DecDriftPxPerSec { get; init; }

    public double RaDriftArcsecPerMin { get; init; }

    public double DecDriftArcsecPerMin { get; init; }

    /// <summary>Fitted periodic error period; null when the run covers less than 1.2 periods or no sinusoid fits.</summary>
    public double? PeriodSeconds { get; init; }

    /// <summary>Half peak-to-peak PE amplitude on the sky (fitted sinusoid, else half peak-to-peak of the detrended smoothed RA), arcsec.</summary>
    public double PeriodicAmplitudeArcsec { get; init; }

    public double? PeriodicPhaseRad { get; init; }

    /// <summary>
    /// Offset of the fitted model relative to the first sample (arcsec). With a period, the RA samples (relative to the first
    /// one) follow Ra(T) = offset + drift·T + amplitude·sin(2πT/period + phase), drift = <see cref="RaDriftArcsecPerMin"/>/60.
    /// </summary>
    public double? PeriodicOffsetArcsec { get; init; }

    public double PolarAlignmentErrorArcmin { get; init; }

    public bool DeclinationAssumed { get; init; }

    /// <summary>Longest exposure during which the RA drift stays below the RA seeing RMS; null without RA drift.</summary>
    public double? DriftLimitingExposureSeconds { get; init; }

    /// <summary>Share of frame-to-frame jumps above 4 σ of the high-frequency jitter, 0..100.</summary>
    public double GustPercent { get; init; }

    /// <summary>Min-move suggestion from the seeing, px.</summary>
    public double MinMoveRaPx { get; init; }

    public double MinMoveDecPx { get; init; }
}

/// <summary>
/// Analysis of the Guiding Coach drift measurement (guiding output off): seeing vs mount error. Own design (docs/COACH.md
/// §2.2), with the polar alignment formula and min-move multipliers taken from PHD2's guiding assistant.
/// </summary>
public static class DriftAnalyzer
{
    /// <summary>Polar alignment error factor (Barrett): arcmin per (arcsec/min of Dec drift) at the equator.</summary>
    public const double PolarAlignmentFactor = 3.8197;

    public const double MinPeriodSeconds = 60;

    public const double MaxPeriodSeconds = 1200;

    /// <summary>A period is only reported when the run covers at least this many periods.</summary>
    public const double MinPeriodsCovered = 1.2;

    /// <summary>Samples needed for an analysis.</summary>
    public const int MinSamples = 12;

    /// <summary>Analyses the samples (time-ordered). Returns null with fewer than <see cref="MinSamples"/> samples.</summary>
    public static DriftAnalysis? Analyze(IReadOnlyList<DriftSample> samples, double exposureSeconds, double pixelScale, double? declinationDeg,
        double smartMinMovePx = 0.2)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < MinSamples)
        {
            return null;
        }

        double scale = pixelScale > 0 ? pixelScale : 1.0;
        int n = samples.Count;
        var t = samples.Select(s => s.T).ToArray();
        var ra = samples.Select(s => s.RaPx).ToArray();
        var dec = samples.Select(s => s.DecPx).ToArray();
        double duration = t[^1] - t[0];
        double interval = Median(Enumerable.Range(1, n - 1).Select(i => t[i] - t[i - 1]).ToArray());
        if (!(interval > 0))
        {
            interval = Math.Max(exposureSeconds, 0.1);
        }

        // high-frequency (seeing) motion: residual about a centred moving average spanning max(6 s, 3 exposures);
        // unlike a single-pole high-pass this has no phase lag, removes drift exactly and leaks little PE
        double window = Math.Max(6.0, 3.0 * exposureSeconds);
        int half = Math.Max(2, (int)Math.Ceiling(window / (2 * interval)));
        half = Math.Min(half, (n - 1) / 4);
        half = Math.Max(1, half);
        var smoothRa = MovingAverage(ra, half);
        var smoothDec = MovingAverage(dec, half);
        double seeingRa = HighFrequencyRms(ra, smoothRa, half);
        double seeingDec = HighFrequencyRms(dec, smoothDec, half);

        var (lineSlope, raIntercept) = LinearFit(t, ra);
        double raSlope = lineSlope;
        var (decSlope, _) = LinearFit(t, dec);

        // RA peak-to-peak of the smoothed motion (PHD2 uses a low-pass for the same purpose)
        var validSmoothRa = Enumerable.Range(half, n - 2 * half).Select(i => smoothRa[i]).ToArray();
        double raP2P = validSmoothRa.Length > 0 ? (validSmoothRa.Max() - validSmoothRa.Min()) * scale : 0;

        // periodic error: least-squares sinusoid + line, scanned over periods the run covers ≥ 1.2 times
        var pe = FitPeriodicError(t, ra, duration, seeingRa);
        double peAmplitudePx;
        double maxRatePxPerSec;
        if (pe is { } fit)
        {
            peAmplitudePx = fit.Amplitude;
            maxRatePxPerSec = MaxSinusoidRate(fit);

            // the drift of the PE model, so the published parameters reproduce the samples exactly
            raSlope = fit.Slope;
        }
        else
        {
            var detrended = validSmoothRa.Select((v, k) => v - (raSlope * t[k + half] + raIntercept)).ToArray();
            peAmplitudePx = detrended.Length > 0 ? (detrended.Max() - detrended.Min()) / 2 : 0;
            maxRatePxPerSec = SmoothedMaxRate(t, smoothRa, half, window);
        }

        double seeingRaArcsec = seeingRa * scale;
        double maxRateArcsec = maxRatePxPerSec * scale;
        double decDriftArcsecPerMin = decSlope * 60 * scale;
        bool decAssumed = declinationDeg is null;
        double cosDec = Math.Max(0.05, Math.Cos((declinationDeg ?? 0) * Math.PI / 180));
        double pae = PolarAlignmentFactor * Math.Abs(decDriftArcsecPerMin) / cosDec;

        double seeingTotalPx = Math.Sqrt(seeingRa * seeingRa + seeingDec * seeingDec);
        double gust = GustFraction(ra, dec, smoothRa, smoothDec, half, seeingTotalPx);

        // min-move from the seeing (PHD2 guiding assistant multipliers: 20 % / 10 % activity targets; RA 65 % of it)
        double multiplier = scale < 1.5 ? 1.28 : 1.65;
        double decMinMove = Math.Max(0.1, CeilTo(seeingDec * multiplier, 0.05));
        double raMinMove = Math.Max(0.1, CeilTo(seeingRa * multiplier * 0.65, 0.05));
        if (decMinMove * scale > 1.25)
        {
            // PHD2's sanity check: a min-move above 1.25″ is not credible, fall back to the smart default
            decMinMove = smartMinMovePx;
            raMinMove = Math.Max(0.1, smartMinMovePx * 0.65);
        }

        return new DriftAnalysis
        {
            Samples = n,
            DurationSeconds = duration,
            FrameIntervalSeconds = interval,
            SnrAvg = samples.Any(s => s.Snr > 0) ? samples.Where(s => s.Snr > 0).Average(s => s.Snr) : null,
            SeeingRaPx = seeingRa,
            SeeingDecPx = seeingDec,
            SeeingRaArcsec = seeingRaArcsec,
            SeeingDecArcsec = seeingDec * scale,
            SeeingTotalArcsec = seeingTotalPx * scale,
            RaPeakToPeakArcsec = raP2P,
            RaMaxRateArcsecPerSec = maxRateArcsec,
            RaDriftPxPerSec = raSlope,
            DecDriftPxPerSec = decSlope,
            RaDriftArcsecPerMin = raSlope * 60 * scale,
            DecDriftArcsecPerMin = decDriftArcsecPerMin,
            PeriodSeconds = pe?.Period,
            PeriodicAmplitudeArcsec = peAmplitudePx * scale,
            PeriodicPhaseRad = pe?.Phase,
            PeriodicOffsetArcsec = pe is { } f2 ? (f2.Intercept - ra[0]) * scale : null,
            PolarAlignmentErrorArcmin = pae,
            DeclinationAssumed = decAssumed,
            DriftLimitingExposureSeconds = maxRateArcsec > 1e-6 ? seeingRaArcsec / maxRateArcsec : null,
            GustPercent = gust * 100,
            MinMoveRaPx = Math.Round(raMinMove, 3),
            MinMoveDecPx = Math.Round(decMinMove, 3),
        };
    }

    /// <summary>Fitted RA periodic error (px): x = Intercept + Slope·t + Amplitude·sin(2πt/Period + Phase), t as given (seconds).</summary>
    public sealed record PeriodicFit(double Period, double Amplitude, double Phase, double Slope, double Intercept, double VarianceExplained);

    /// <summary>
    /// Least-squares scan for a sinusoid (plus line) with periods between 60 s and min(1200 s, duration / 1.2). Returns null
    /// when the run is too short, the sinusoid explains little of the detrended variance, or the best period lies at the
    /// upper end of the scan (the true period is probably longer than the run can resolve).
    /// </summary>
    public static PeriodicFit? FitPeriodicError(IReadOnlyList<double> t, IReadOnlyList<double> x, double duration, double noisePx)
    {
        double maxPeriod = Math.Min(MaxPeriodSeconds, duration / MinPeriodsCovered);
        if (maxPeriod < MinPeriodSeconds || t.Count < MinSamples)
        {
            return null;
        }

        var tt = t.ToArray();
        var xx = x.ToArray();
        double sseLine = LineSse(tt, xx);

        const int grid = 160;
        double logMin = Math.Log(MinPeriodSeconds), logMax = Math.Log(maxPeriod);
        double bestP = 0, bestSse = double.MaxValue;
        for (int i = 0; i < grid; i++)
        {
            double p = Math.Exp(logMin + (logMax - logMin) * i / (grid - 1));
            double sse = SinusoidSse(tt, xx, p, out _);
            if (sse < bestSse)
            {
                bestSse = sse;
                bestP = p;
            }
        }

        // refine around the best grid point (golden section)
        double step = Math.Exp((logMax - logMin) / (grid - 1));
        double lo = Math.Max(MinPeriodSeconds, bestP / step), hi = Math.Min(maxPeriod, bestP * step);
        const double g = 0.6180339887498949;
        double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
        double fa = SinusoidSse(tt, xx, a, out _), fb = SinusoidSse(tt, xx, b, out _);
        for (int k = 0; k < 40 && hi - lo > 1e-3; k++)
        {
            if (fa < fb)
            {
                hi = b;
                b = a;
                fb = fa;
                a = hi - g * (hi - lo);
                fa = SinusoidSse(tt, xx, a, out _);
            }
            else
            {
                lo = a;
                a = b;
                fa = fb;
                b = lo + g * (hi - lo);
                fb = SinusoidSse(tt, xx, b, out _);
            }
        }

        double period = fa < fb ? a : b;
        double finalSse = SinusoidSse(tt, xx, period, out var c);
        if (finalSse > bestSse)
        {
            period = bestP;
            finalSse = SinusoidSse(tt, xx, period, out c);
        }

        double explained = sseLine > 0 ? 1 - finalSse / sseLine : 0;
        double amplitude = Math.Sqrt(c[2] * c[2] + c[3] * c[3]);
        double residualRms = Math.Sqrt(finalSse / Math.Max(1, tt.Length - 4));

        // significance: the sinusoid must explain most of the non-linear motion and stand clearly above the noise,
        // and must not sit at the long end of the scan
        double sigma = Math.Max(residualRms, noisePx);
        if (explained < 0.5 || amplitude < 4 * sigma * Math.Sqrt(2.0 / tt.Length) || period > 0.95 * maxPeriod)
        {
            return null;
        }

        // model: c2·sin(ωt) + c3·cos(ωt) = A·sin(ωt + φ)
        double phase = Math.Atan2(c[3], c[2]);
        return new PeriodicFit(period, amplitude, phase, c[1], c[0], explained);
    }

    /// <summary>High-frequency RMS: residual about a centred (2h+1)-point moving average, corrected for the average's own noise share.</summary>
    internal static double HighFrequencyRms(double[] x, double[] smooth, int half)
    {
        int count = 0;
        double sum = 0;
        for (int i = half; i < x.Length - half; i++)
        {
            double r = x[i] - smooth[i];
            sum += r * r;
            count++;
        }

        if (count == 0)
        {
            return 0;
        }

        int window = 2 * half + 1;
        return Math.Sqrt(sum / count * window / (window - 1.0));
    }

    internal static double[] MovingAverage(double[] x, int half)
    {
        var result = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(x.Length - 1, i + half);
            double s = 0;
            for (int k = a; k <= b; k++)
            {
                s += x[k];
            }

            result[i] = s / (b - a + 1);
        }

        return result;
    }

    internal static (double Slope, double Intercept) LinearFit(IReadOnlyList<double> t, IReadOnlyList<double> y)
    {
        int n = t.Count;
        double mt = t.Average(), my = y.Average();
        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            sxy += (t[i] - mt) * (y[i] - my);
            sxx += (t[i] - mt) * (t[i] - mt);
        }

        double slope = sxx > 0 ? sxy / sxx : 0;
        return (slope, my - slope * mt);
    }

    private static double LineSse(double[] t, double[] x)
    {
        var (s, i0) = LinearFit(t, x);
        return t.Select((v, k) => x[k] - (s * v + i0)).Sum(r => r * r);
    }

    /// <summary>Least squares x ≈ c0 + c1·t + c2·sin(ωt) + c3·cos(ωt); returns the SSE.</summary>
    private static double SinusoidSse(double[] t, double[] x, double period, out double[] c)
    {
        double w = 2 * Math.PI / period;
        var ata = new double[4, 4];
        var atb = new double[4];
        var row = new double[4];
        for (int i = 0; i < t.Length; i++)
        {
            row[0] = 1;
            row[1] = t[i];
            row[2] = Math.Sin(w * t[i]);
            row[3] = Math.Cos(w * t[i]);
            for (int a = 0; a < 4; a++)
            {
                atb[a] += row[a] * x[i];
                for (int b = 0; b < 4; b++)
                {
                    ata[a, b] += row[a] * row[b];
                }
            }
        }

        c = Solve(ata, atb) ?? [0, 0, 0, 0];
        double sse = 0;
        for (int i = 0; i < t.Length; i++)
        {
            double r = x[i] - (c[0] + c[1] * t[i] + c[2] * Math.Sin(w * t[i]) + c[3] * Math.Cos(w * t[i]));
            sse += r * r;
        }

        return sse;
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return null;
            }

            if (pivot != col)
            {
                for (int k = 0; k < n; k++)
                {
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                }

                (v[col], v[pivot]) = (v[pivot], v[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int k = col; k < n; k++)
                {
                    m[r, k] -= f * m[col, k];
                }

                v[r] -= f * v[col];
            }
        }

        var xres = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = v[r];
            for (int k = r + 1; k < n; k++)
            {
                s -= m[r, k] * xres[k];
            }

            xres[r] = s / m[r, r];
        }

        return xres;
    }

    private static double MaxSinusoidRate(PeriodicFit fit)
    {
        double w = 2 * Math.PI / fit.Period;
        return Math.Abs(fit.Slope) + fit.Amplitude * w;
    }

    /// <summary>Max rate of the smoothed series over a baseline long enough to average the seeing out.</summary>
    private static double SmoothedMaxRate(double[] t, double[] smooth, int half, double window)
    {
        double baseline = Math.Max(20.0, 2 * window);
        double max = 0;
        int n = t.Length;
        for (int i = half; i < n - half; i++)
        {
            int j = i;
            while (j < n - half - 1 && t[j] - t[i] < baseline)
            {
                j++;
            }

            double dt = t[j] - t[i];
            if (dt < baseline * 0.8)
            {
                break;
            }

            max = Math.Max(max, Math.Abs(smooth[j] - smooth[i]) / dt);
        }

        return max;
    }

    private static double GustFraction(double[] ra, double[] dec, double[] smoothRa, double[] smoothDec, int half, double seeingTotalPx)
    {
        if (!(seeingTotalPx > 0) || ra.Length < 3)
        {
            return 0;
        }

        int jumps = 0, count = 0;
        for (int i = 1; i < ra.Length; i++)
        {
            // frame-to-frame change with the slow motion (drift, PE) removed
            double dr = (ra[i] - smoothRa[i]) - (ra[i - 1] - smoothRa[i - 1]);
            double dd = (dec[i] - smoothDec[i]) - (dec[i - 1] - smoothDec[i - 1]);
            if (Math.Sqrt(dr * dr + dd * dd) > 4 * seeingTotalPx)
            {
                jumps++;
            }

            count++;
        }

        return count > 0 ? (double)jumps / count : 0;
    }

    private static double CeilTo(double v, double unit) => Math.Ceiling(v / unit - 1e-9) * unit;

    private static double Median(double[] v)
    {
        if (v.Length == 0)
        {
            return 0;
        }

        Array.Sort(v);
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
