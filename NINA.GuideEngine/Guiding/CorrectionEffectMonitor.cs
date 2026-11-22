// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// What the <see cref="CorrectionEffectMonitor"/> measured over its window: the share of the calibrated move that the RA
/// corrections had (1 = as calibrated, 0 = none), its standard error, and how far the corrections should have moved the
/// star.
/// </summary>
public readonly record struct CorrectionEffect(double Effect, double Sigma, double ExpectedPx, double WindowSec, int Frames);

/// <summary>
/// Detects RA corrections that have no effect while the error stays small, which the <see cref="MountResponseMonitor"/>
/// can't see: it only judges pulses that should move the star by several pixels. A guide "star" that is fixed on the
/// sensor (a warm-pixel cluster, a glow, a reflection) or a mount that ignores pulses while nothing drifts gives such a
/// run. The star stays put whatever is sent, and a guide algorithm with a drift term (Predictive) takes the offset that
/// never goes away for drift and sends longer and longer pulses.
/// </summary>
/// <remarks>
/// The star's position alone can't tell such a run from a mount that drifts and is corrected exactly: in both the star
/// stays put. What tells them apart is the frame timing. A frame that starts late gives the drift more time to move the
/// star, whatever the guider sends, so the slope of the frame-to-frame position change against the time between the
/// exposures measures the real drift d. Over the window the star moved by d·T minus the effect of the corrections, so
/// the effect is g = (d·T − ΔP) / ΣC with ΔP the star's net move and ΣC the sum of the corrections (mount px as
/// calibrated). The regression also takes the correction before each frame, since pulses delay the next exposure, and
/// the change of the time between the capture request and the frame's arrival, since the camera may start the
/// exposure late (then the request times alone would underestimate the drift). Without timing jitter or with a noisy
/// star the effect is not measured precisely enough and nothing is judged; the check is made for a still "star" with
/// pulses that keep growing. RA only, like <see cref="MountResponseMonitor"/>: Dec pulses may be absorbed by backlash.
/// </remarks>
public sealed class CorrectionEffectMonitor(SafetySettings settings)
{
    // a pair longer than this (a stall, a clock step) breaks the chain
    private const double MaxPairSec = 60;

    // timing columns that vary less than this (s) carry no information
    private const double MinTimingSigmaSec = 1e-4;

    private readonly Queue<Pair> pairs = new();
    private Measurement? last;
    private double correctionSinceLast;
    private DateTimeOffset? lastWarning;
    private DateTimeOffset? belowSince;

    private readonly record struct Measurement(DateTimeOffset Start, double OverheadSec, double PositionPx);

    // between two measurements: the request interval, the change of the overhead, the correction sent and the star's move
    private readonly record struct Pair(DateTimeOffset End, double IntervalSec, double OverheadChangeSec, double CorrectionPx, double MovePx);

    /// <summary>Forgets the window and the time of the last warning (guiding started).</summary>
    public void Restart()
    {
        Reset();
        lastWarning = null;
    }

    /// <summary>Forgets the window (lock position moved, resume after a pause).</summary>
    public void Reset()
    {
        pairs.Clear();
        belowSince = null;
        Break();
    }

    /// <summary>The next measurement starts a new chain: something moved the star that the corrections don't account for.</summary>
    public void Break()
    {
        last = null;
        correctionSinceLast = 0;
    }

    /// <summary>
    /// A guide frame's RA offset (mount px). <paramref name="start"/> is when the exposure was requested (default when
    /// unknown), <paramref name="arrival"/> when the frame arrived.
    /// </summary>
    public void Measured(DateTimeOffset start, DateTimeOffset arrival, double exposureMs, double raOffsetPx)
    {
        if (!double.IsFinite(raOffsetPx))
        {
            Break();
            return;
        }

        // without a start time the arrival stands in for it (and the overhead column drops out)
        var s = start == default ? arrival : start;
        double overhead = start == default ? 0 : (arrival - start).TotalSeconds - exposureMs / 1000.0;
        var m = new Measurement(s, overhead, raOffsetPx);
        if (last is { } p)
        {
            double dt = (m.Start - p.Start).TotalSeconds;
            if (dt > 0 && dt <= MaxPairSec && double.IsFinite(overhead))
            {
                pairs.Enqueue(new Pair(m.Start, dt, m.OverheadSec - p.OverheadSec, correctionSinceLast, m.PositionPx - p.PositionPx));
            }
            else
            {
                pairs.Clear();
            }
        }

        last = m;
        correctionSinceLast = 0;
        while (pairs.Count > 0 && (m.Start - pairs.Peek().End).TotalSeconds > settings.NoEffectWindowSec)
        {
            pairs.Dequeue();
        }
    }

    /// <summary>The RA correction that went out after the last measurement (mount px, the amount a positive offset is reduced by).</summary>
    public void CorrectionApplied(double raPx)
    {
        if (last is not null && double.IsFinite(raPx))
        {
            correctionSinceLast += raPx;
        }
    }

    /// <summary>The effect measured over the window; null before there are enough frames or without timing jitter.</summary>
    public CorrectionEffect? Estimate()
    {
        int n = pairs.Count;
        if (n < Math.Max(settings.NoEffectMinFrames, 6))
        {
            return null;
        }

        double sumT = 0, sumO = 0, sumC = 0, sumY = 0;
        foreach (var p in pairs)
        {
            sumT += p.IntervalSec;
            sumO += p.OverheadChangeSec;
            sumC += p.CorrectionPx;
            sumY += p.MovePx;
        }

        if (sumT < settings.NoEffectWindowSec / 2)
        {
            return null;
        }

        // centred cross products of the columns (request interval, change of the overhead, correction) and the move
        double mT = sumT / n, mO = sumO / n, mC = sumC / n, mY = sumY / n;
        var xx = new double[3, 3];
        var xy = new double[3];
        double yy = 0;
        Span<double> x = stackalloc double[3];
        foreach (var p in pairs)
        {
            x[0] = p.IntervalSec - mT;
            x[1] = p.OverheadChangeSec - mO;
            x[2] = p.CorrectionPx - mC;
            double y = p.MovePx - mY;
            for (int i = 0; i < 3; i++)
            {
                xy[i] += x[i] * y;
                for (int j = 0; j < 3; j++)
                {
                    xx[i, j] += x[i] * x[j];
                }
            }

            yy += y * y;
        }

        if (Math.Sqrt(xx[0, 0] / n) < MinTimingSigmaSec)
        {
            return null;
        }

        // The request interval always, the overhead when it varies, the correction whenever it varies. The correction is
        // never left out to make the system solvable: without timing jitter the intervals vary only with the pulses that
        // delay the next exposure, and the interval alone would then take the pulses' effect for drift.
        bool useOverhead = Math.Sqrt(xx[1, 1] / n) >= MinTimingSigmaSec;
        bool useCorrection = xx[2, 2] > 0;
        if (!TrySolve(xx, xy, yy, n, useOverhead, useCorrection, out double drift, out double driftVar, out double residualVar)
            && !(useOverhead && TrySolve(xx, xy, yy, n, false, useCorrection, out drift, out driftVar, out residualVar)))
        {
            return null;
        }

        if (Math.Abs(sumC) < 1e-9)
        {
            return null;
        }

        // the net move telescopes the noise of the first and last frame: one residual's variance
        double effect = (drift * sumT - sumY) / sumC;
        double sigma = Math.Sqrt(driftVar * sumT * sumT + residualVar) / Math.Abs(sumC);
        return new CorrectionEffect(effect, sigma, sumC, sumT, n);
    }

    /// <summary>
    /// True when a warning is due: the corrections should have moved the star by <see cref="SafetySettings.NoEffectMinExpectedPx"/>,
    /// their measured effect has stayed clearly below <see cref="SafetySettings.NoEffectMaxFraction"/> for
    /// <see cref="SafetySettings.NoEffectHoldSec"/> (the window is judged every frame, so a single unlucky one would come
    /// up now and then over a night), and no warning came within <see cref="SafetySettings.NoEffectRepeatSec"/>.
    /// </summary>
    public bool Evaluate(DateTimeOffset now, out CorrectionEffect effect)
    {
        effect = default;
        if (Estimate() is not { } e)
        {
            belowSince = null;
            return false;
        }

        effect = e;
        bool noEffect = Math.Abs(e.ExpectedPx) >= settings.NoEffectMinExpectedPx
            && e.Sigma <= settings.NoEffectMaxSigma
            && e.Effect + settings.NoEffectSigmas * e.Sigma < settings.NoEffectMaxFraction;
        if (!noEffect)
        {
            belowSince = null;
            return false;
        }

        belowSince ??= now;
        if ((now - belowSince.Value).TotalSeconds < settings.NoEffectHoldSec
            || (lastWarning is { } w && (now - w).TotalSeconds < settings.NoEffectRepeatSec))
        {
            return false;
        }

        lastWarning = now;
        return true;
    }

    /// <summary>
    /// Least squares of the move on the chosen centred columns (column 0, the request interval, always). The drift is the
    /// coefficient of column 0; false when the system is singular or has too few degrees of freedom.
    /// </summary>
    private static bool TrySolve(double[,] xx, double[] xy, double yy, int n, bool useOverhead, bool useCorrection,
        out double drift, out double driftVar, out double residualVar)
    {
        drift = driftVar = residualVar = 0;
        var cols = new List<int> { 0 };
        if (useOverhead)
        {
            cols.Add(1);
        }

        if (useCorrection)
        {
            cols.Add(2);
        }

        int k = cols.Count;
        int dof = n - k - 1;
        if (dof < 3)
        {
            return false;
        }

        // Gauss-Jordan inverse of the k × k normal matrix
        var a = new double[k, 2 * k];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                a[i, j] = xx[cols[i], cols[j]];
            }

            a[i, k + i] = 1;
        }

        for (int c = 0; c < k; c++)
        {
            int pivot = c;
            for (int r = c + 1; r < k; r++)
            {
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                {
                    pivot = r;
                }
            }

            // relative to the column's own scale: collinear columns leave a pivot of rounding size
            if (Math.Abs(a[pivot, c]) <= 1e-9 * xx[cols[c], cols[c]])
            {
                return false;
            }

            if (pivot != c)
            {
                for (int j = 0; j < 2 * k; j++)
                {
                    (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
                }
            }

            double d = a[c, c];
            for (int j = 0; j < 2 * k; j++)
            {
                a[c, j] /= d;
            }

            for (int r = 0; r < k; r++)
            {
                if (r == c || a[r, c] == 0)
                {
                    continue;
                }

                double f = a[r, c];
                for (int j = 0; j < 2 * k; j++)
                {
                    a[r, j] -= f * a[c, j];
                }
            }
        }

        var beta = new double[k];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                beta[i] += a[i, k + j] * xy[cols[j]];
            }
        }

        double rss = yy;
        for (int i = 0; i < k; i++)
        {
            rss -= beta[i] * xy[cols[i]];
        }

        residualVar = Math.Max(rss, 0) / dof;
        drift = beta[0];
        driftVar = residualVar * a[0, k];
        return double.IsFinite(drift) && double.IsFinite(driftVar) && driftVar >= 0;
    }
}
