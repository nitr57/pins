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
/// exposure late (then the request times alone would underestimate the drift).
/// <para>
/// Each correction follows the measured offset and so carries that frame's measurement noise, which the next move
/// carries with the opposite sign. Plain least squares then biases the correction's coefficient and, through its
/// correlation with the timing (a late start moves the star before it is measured), the drift: the effect came out
/// 20 % too high with 0.015 px of noise and late starts. So the coefficients come from two-stage least squares, with
/// instruments that this noise doesn't reach: the overhead of the earlier frame (the request interval without the
/// pulses and the processing), the change of the overhead, and the correction and request interval of the pair
/// before.
/// </para>
/// Times are seconds of a monotonic clock: a step of the system clock would be taken for a long interval with no move.
/// Without timing jitter or with a noisy star the effect is not measured precisely enough and nothing is judged; the
/// check is made for a still "star" with pulses that keep growing. RA only, like <see cref="MountResponseMonitor"/>: Dec
/// pulses may be absorbed by backlash.
/// </remarks>
public sealed class CorrectionEffectMonitor(SafetySettings settings)
{
    // a pair longer than this (a stall) breaks the chain
    private const double MaxPairSec = 60;

    // timing columns that vary less than this (s) carry no information
    private const double MinTimingSigmaSec = 1e-4;

    // columns of the cross-product matrix: regressors, instruments, and the move
    private const int Interval = 0, OverheadChange = 1, Correction = 2, OverheadBefore = 3, CorrectionBefore = 4, IntervalBefore = 5, Move = 6, Columns = 7;

    private readonly Queue<Pair> pairs = new();
    private Measurement? last;
    private Pair? lastPair;
    private double correctionSinceLast;
    private double? lastWarningSec;
    private double? belowSinceSec;

    private readonly record struct Measurement(double RequestSec, double OverheadSec, double PositionPx);

    // between two measurements: the request interval, the change of the overhead, the correction sent and the star's
    // move, with the overhead of the earlier frame and the correction and interval of the pair before as instruments
    private readonly record struct Pair(double EndSec, double IntervalSec, double OverheadChangeSec, double CorrectionPx, double MovePx,
        double OverheadBeforeSec, double CorrectionBeforePx, double IntervalBeforeSec);

    /// <summary>Forgets the window and the time of the last warning (guiding started).</summary>
    public void Restart()
    {
        Reset();
        lastWarningSec = null;
    }

    /// <summary>Forgets the window (lock position moved, resume after a pause).</summary>
    public void Reset()
    {
        pairs.Clear();
        belowSinceSec = null;
        Break();
    }

    /// <summary>The next measurement starts a new chain: something moved the star that the corrections don't account for.</summary>
    public void Break()
    {
        last = null;
        lastPair = null;
        correctionSinceLast = 0;
    }

    /// <summary>
    /// A guide frame's RA offset (mount px). <paramref name="requestSec"/> is when the exposure was requested,
    /// <paramref name="arrivalSec"/> when the frame arrived, both in seconds of a monotonic clock.
    /// </summary>
    public void Measured(double requestSec, double arrivalSec, double exposureMs, double raOffsetPx)
    {
        double overhead = arrivalSec - requestSec - exposureMs / 1000.0;
        if (!double.IsFinite(raOffsetPx) || !double.IsFinite(overhead))
        {
            Break();
            return;
        }

        var m = new Measurement(requestSec, overhead, raOffsetPx);
        if (last is { } p)
        {
            double dt = m.RequestSec - p.RequestSec;
            if (dt > 0 && dt <= MaxPairSec)
            {
                var pair = new Pair(m.RequestSec, dt, m.OverheadSec - p.OverheadSec, correctionSinceLast, m.PositionPx - p.PositionPx,
                    p.OverheadSec, lastPair?.CorrectionPx ?? 0, lastPair?.IntervalSec ?? 0);

                // the first pair of a chain has no pair before it to take the instruments from
                if (lastPair is not null)
                {
                    pairs.Enqueue(pair);
                }

                lastPair = pair;
            }
            else
            {
                pairs.Clear();
                lastPair = null;
            }
        }

        last = m;
        correctionSinceLast = 0;
        while (pairs.Count > 0 && m.RequestSec - pairs.Peek().EndSec > settings.NoEffectWindowSec)
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
        if (n < Math.Max(settings.NoEffectMinFrames, 8))
        {
            return null;
        }

        Span<double> sum = stackalloc double[Columns];
        foreach (var p in pairs)
        {
            Fill(p, sum, add: true);
        }

        double sumT = sum[Interval], sumC = sum[Correction], sumY = sum[Move];
        if (sumT < settings.NoEffectWindowSec / 2 || Math.Abs(sumC) < 1e-9)
        {
            return null;
        }

        // centred cross products of all columns
        Span<double> mean = stackalloc double[Columns];
        for (int i = 0; i < Columns; i++)
        {
            mean[i] = sum[i] / n;
        }

        var s = new double[Columns, Columns];
        Span<double> v = stackalloc double[Columns];
        foreach (var p in pairs)
        {
            Fill(p, v, add: false);
            for (int i = 0; i < Columns; i++)
            {
                v[i] -= mean[i];
            }

            for (int i = 0; i < Columns; i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    s[i, j] += v[i] * v[j];
                }
            }
        }

        if (Math.Sqrt(s[Interval, Interval] / n) < MinTimingSigmaSec || s[Correction, Correction] <= 0)
        {
            return null;
        }

        // The overhead columns when the overhead varies. The correction is always a regressor: without timing jitter the
        // intervals vary only with the pulses that delay the next exposure, and the interval alone would then take the
        // pulses' effect for drift.
        bool useOverhead = Math.Sqrt(s[OverheadChange, OverheadChange] / n) >= MinTimingSigmaSec;
        if (!TrySolve(s, n, useOverhead, out double drift, out double driftVar, out double residualVar)
            && !(useOverhead && TrySolve(s, n, false, out drift, out driftVar, out residualVar)))
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
    /// <paramref name="nowSec"/> is on the monotonic clock of <see cref="Measured"/>.
    /// </summary>
    public bool Evaluate(double nowSec, out CorrectionEffect effect)
    {
        effect = default;
        if (Estimate() is not { } e)
        {
            belowSinceSec = null;
            return false;
        }

        effect = e;
        bool noEffect = Math.Abs(e.ExpectedPx) >= settings.NoEffectMinExpectedPx
            && e.Sigma <= settings.NoEffectMaxSigma
            && e.Effect + settings.NoEffectSigmas * e.Sigma < settings.NoEffectMaxFraction;
        if (!noEffect)
        {
            belowSinceSec = null;
            return false;
        }

        belowSinceSec ??= nowSec;
        if (nowSec - belowSinceSec.Value < settings.NoEffectHoldSec
            || (lastWarningSec is { } w && nowSec - w < settings.NoEffectRepeatSec))
        {
            return false;
        }

        lastWarningSec = nowSec;
        return true;
    }

    private static void Fill(Pair p, Span<double> v, bool add)
    {
        Span<double> x = [p.IntervalSec, p.OverheadChangeSec, p.CorrectionPx, p.OverheadBeforeSec, p.CorrectionBeforePx, p.IntervalBeforeSec, p.MovePx];
        for (int i = 0; i < Columns; i++)
        {
            v[i] = add ? v[i] + x[i] : x[i];
        }
    }

    /// <summary>
    /// Two-stage least squares of the move on the regressors (request interval, change of the overhead, correction) with
    /// the instruments (overhead of the earlier frame, change of the overhead, correction and interval of the pair before),
    /// from the centred cross products <paramref name="s"/>. Without <paramref name="useOverhead"/> the two overhead
    /// columns drop out. The drift is the coefficient of the request interval; false when the system is singular or has
    /// too few degrees of freedom.
    /// </summary>
    private static bool TrySolve(double[,] s, int n, bool useOverhead, out double drift, out double driftVar, out double residualVar)
    {
        drift = driftVar = residualVar = 0;
        int[] x = useOverhead ? [Interval, OverheadChange, Correction] : [Interval, Correction];
        int[] z = useOverhead ? [OverheadBefore, OverheadChange, CorrectionBefore, IntervalBefore] : [CorrectionBefore, IntervalBefore];
        int k = x.Length, m = z.Length;
        int dof = n - k - 1;
        if (dof < 3)
        {
            return false;
        }

        // A = Sxz Szz⁻¹ Szx, b = Sxz Szz⁻¹ Szy
        var szz = new double[m, m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
            {
                szz[i, j] = s[z[i], z[j]];
            }
        }

        if (!TryInvert(szz))
        {
            return false;
        }

        // W = Szz⁻¹ Szx (m × k)
        var w = new double[m, k];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < k; j++)
            {
                for (int l = 0; l < m; l++)
                {
                    w[i, j] += szz[i, l] * s[z[l], x[j]];
                }
            }
        }

        var a = new double[k, k];
        var rhs = new double[k];
        for (int i = 0; i < k; i++)
        {
            for (int l = 0; l < m; l++)
            {
                for (int j = 0; j < k; j++)
                {
                    a[i, j] += w[l, i] * s[z[l], x[j]];
                }

                rhs[i] += w[l, i] * s[z[l], Move];
            }
        }

        if (!TryInvert(a))
        {
            return false;
        }

        var beta = new double[k];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                beta[i] += a[i, j] * rhs[j];
            }
        }

        // residuals of the move with the regressors themselves, not their first-stage fit
        double rss = s[Move, Move];
        for (int i = 0; i < k; i++)
        {
            rss -= 2 * beta[i] * s[x[i], Move];
            for (int j = 0; j < k; j++)
            {
                rss += beta[i] * beta[j] * s[x[i], x[j]];
            }
        }

        residualVar = Math.Max(rss, 0) / dof;
        drift = beta[0];
        driftVar = residualVar * a[0, 0];
        return double.IsFinite(drift) && double.IsFinite(driftVar) && driftVar >= 0;
    }

    /// <summary>Gauss-Jordan inverse in place; false when the matrix is singular relative to its own diagonal.</summary>
    private static bool TryInvert(double[,] matrix)
    {
        int k = matrix.GetLength(0);
        var a = new double[k, 2 * k];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                a[i, j] = matrix[i, j];
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
            if (!(Math.Abs(a[pivot, c]) > 1e-9 * Math.Abs(matrix[c, c])))
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

        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                matrix[i, j] = a[i, k + j];
            }
        }

        return true;
    }
}
