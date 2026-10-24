// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>One axis's pulse response as estimated from the dithers so far.</summary>
/// <param name="Effect">Share of a calibrated pulse that moves the star: 1 = as calibrated.</param>
/// <param name="EffectSigma">Standard error of <paramref name="Effect"/>.</param>
/// <param name="ReversalLossPx">
/// Dec only: star motion lost on average at a reversal of the pulse direction (px), null on RA and when it can't be told.
/// A lower bound of the backlash: while the guiding reverses on noise the gear often turns inside the backlash, and a
/// reversal then loses less than all of it. It is estimated to keep the effect unbiased, and not used otherwise.
/// </param>
/// <param name="ReversalLossSigmaPx">Standard error of <paramref name="ReversalLossPx"/>.</param>
/// <param name="Windows">Dither windows behind the estimate (older ones count less).</param>
public sealed record PulseResponseEstimate(double Effect, double EffectSigma, double? ReversalLossPx, double? ReversalLossSigmaPx, double Windows);

/// <summary>The pulse model's values in use and the estimates behind them.</summary>
/// <param name="RaEffect">RA effect in use: RA pulses are sized with the calibrated rate times this.</param>
/// <param name="DecEffect">Dec effect in use.</param>
/// <param name="Ra">RA estimate; null before the first estimate.</param>
/// <param name="Dec">Dec estimate; null before the first estimate.</param>
public sealed record PulseModelValues(double RaEffect, double DecEffect, PulseResponseEstimate? Ra, PulseResponseEstimate? Dec)
{
    /// <summary>Pulses as calibrated.</summary>
    public static PulseModelValues Neutral { get; } = new(1.0, 1.0, null, null);

    public bool IsNeutral => RaEffect == 1.0 && DecEffect == 1.0;
}

/// <summary>
/// Running sums of one axis's dither windows for the two-stage least squares of <see cref="PulseModel"/>: k × k
/// matrices row-major, k = 2 on RA (pulses, time) and 4 on Dec (pulses, reversals, start, time).
/// </summary>
public sealed record PulseResponseSums
{
    public double[] ZX { get; init; } = [];

    public double[] ZZ { get; init; } = [];

    public double[] Zy { get; init; } = [];

    public double[] XX { get; init; } = [];

    public double[] Xy { get; init; } = [];

    public double YY { get; init; }

    /// <summary>Windows behind the sums, each weighted by the forgetting since.</summary>
    public double Weight { get; init; }
}

/// <summary>
/// What a pulse model learned, for the host to store between sessions. It holds only with the calibration it was learned
/// with: the effects are relative to that calibration's rates.
/// </summary>
public sealed record PulseModelState
{
    public DateTimeOffset CalibrationTimestamp { get; init; }

    public DateTimeOffset LearnedAt { get; init; }

    public PulseResponseSums? Ra { get; init; }

    public PulseResponseSums? Dec { get; init; }
}

/// <summary>
/// What a guide pulse really moves, learned from dithers (docs/notes/DEC-PULSE-MODEL.md): per axis the effect g, the share of
/// a calibrated pulse that moves the star.
/// </summary>
/// <remarks>
/// <para>The pulses after a dither move the star to the new lock position, but they also react to wander and seeing, so a
/// regression of the star's motion on them is biased (closed-loop identification, Forssell &amp; Ljung 1999). The offset d
/// a dither creates is exogenous: it is the instrument. Per axis, over the first frame after a dither and the next
/// <see cref="WindowFrames"/>:</para>
/// <code>z₀ − z_m = g·Σ s·t·R − b·(signed reversals) − a·sign d + c·Δt</code>
/// <para>with s, t the direction and length of each pulse as sent, R the calibrated rate, and the instruments d, whether the
/// first pulse reverses the last one before the dither (known before the dither), sign d and Δt. On Dec, b and a keep the
/// backlash out of the effect: b is the loss at a reversal of a gear that is engaged, a what the first pulse loses on
/// average when the gear sat inside the backlash. Neither is compensated (see <see cref="PulseResponseEstimate"/>); RA
/// has no backlash below sidereal guide rates and fits g and c only. The sums are forgotten by <see cref="Forgetting"/> per
/// window.</para>
/// <para>The effect in use is cautious: at the upper end of its uncertainty (less lengthening), neutral until the estimate
/// is good enough (<see cref="MinWindows"/>, <see cref="MaxEffectSigma"/>), and it changes in steps of
/// <see cref="EffectStep"/>. Directions: +1 is West on RA and South on Dec (the pulse that reduces a positive offset), −1
/// East and North.</para>
/// </remarks>
public sealed class PulseModel
{
    /// <summary>Frames measured after the first frame of a dither window.</summary>
    public const int WindowFrames = 8;

    /// <summary>Weight left to the older windows when a window is added (a memory of about 50 dithers).</summary>
    public const double Forgetting = 0.98;

    /// <summary>Windows needed before an estimate is used.</summary>
    public const double MinWindows = 10;

    /// <summary>Largest standard error of an effect that is used.</summary>
    public const double MaxEffectSigma = 0.1;

    /// <summary>Smallest effect in use: pulses at most 1.5× longer.</summary>
    public const double MinEffect = 1.0 / 1.5;

    /// <summary>Largest effect in use: pulses at most 1.5× shorter.</summary>
    public const double MaxEffect = 1.5;

    /// <summary>Smallest change of an effect in use.</summary>
    public const double EffectStep = 0.02;

    private readonly AxisLearner ra = new(withReversals: false);
    private readonly AxisLearner dec = new(withReversals: true);
    private int lastDecDirection;

    /// <summary>The calibration the effects are relative to (its timestamp); null before <see cref="UseCalibration"/>.</summary>
    public DateTimeOffset? CalibrationTimestamp { get; private set; }

    public PulseModelValues Values { get; private set; } = PulseModelValues.Neutral;

    /// <summary>
    /// Sets the calibration the effects are relative to. A different calibration than before starts over, from
    /// <paramref name="stored"/> when that was learned with this calibration. Returns true when the model started over.
    /// </summary>
    public bool UseCalibration(DateTimeOffset calibrationTimestamp, PulseModelState? stored)
    {
        if (CalibrationTimestamp is { } current && SameCalibration(current, calibrationTimestamp))
        {
            return false;
        }

        CalibrationTimestamp = calibrationTimestamp;
        lastDecDirection = 0;
        ra.Reset();
        dec.Reset();
        if (stored is not null && SameCalibration(stored.CalibrationTimestamp, calibrationTimestamp))
        {
            ra.Load(stored.Ra);
            dec.Load(stored.Dec);
        }

        Values = PulseModelValues.Neutral;
        UpdateValues();
        return true;
    }

    /// <summary>
    /// A dither moved the lock position: <paramref name="raOffsetPx"/>, <paramref name="decOffsetPx"/> are the offsets it
    /// creates (mount px, the sign of the offsets measured afterwards). Opens a window on each axis it moved.
    /// </summary>
    public void DitherStarted(double raOffsetPx, double decOffsetPx)
    {
        if (CalibrationTimestamp is null)
        {
            return;
        }

        ra.Open(raOffsetPx, 0);
        dec.Open(decOffsetPx, lastDecDirection);
    }

    /// <summary>
    /// A guiding frame measured the offsets (mount px). Returns true when it completed a window (the state to store
    /// changed); <paramref name="valuesChanged"/> tells whether the values in use changed with it.
    /// </summary>
    public bool FrameMeasured(double raPx, double decPx, DateTimeOffset now, out bool valuesChanged)
    {
        bool learned = ra.Measured(raPx, now) | dec.Measured(decPx, now);
        valuesChanged = learned && UpdateValues();
        return learned;
    }

    /// <summary>The pulses that went out after the last measured frame, with the calibrated rates (px/ms).</summary>
    public void PulsesSent(IEnumerable<PulseCommand> pulses, double xRate, double yRate)
    {
        foreach (var p in pulses)
        {
            if (p.DurationMs <= 0)
            {
                continue;
            }

            switch (p.Direction)
            {
                case GuideDirection.West:
                case GuideDirection.East:
                    ra.Pulse(p.Direction == GuideDirection.West ? 1 : -1, p.DurationMs * xRate);
                    break;
                default:
                    int direction = p.Direction == GuideDirection.South ? 1 : -1;
                    dec.Pulse(direction, p.DurationMs * yRate);
                    lastDecDirection = direction;
                    break;
            }
        }
    }

    /// <summary>
    /// Something other than the guide pulses may have moved the star or the lock position: open windows are dropped. With
    /// <paramref name="mountMoved"/> the mount may also have moved its Dec gear (slew, calibration, a test pulse), so the
    /// last Dec direction is forgotten.
    /// </summary>
    public void Interrupt(bool mountMoved)
    {
        ra.Drop();
        dec.Drop();
        if (mountMoved)
        {
            lastDecDirection = 0;
        }
    }

    /// <summary>What was learned so far, for the host to store; null before <see cref="UseCalibration"/>.</summary>
    public PulseModelState? State(DateTimeOffset now) => CalibrationTimestamp is { } cal
        ? new PulseModelState { CalibrationTimestamp = cal, LearnedAt = now, Ra = ra.Sums(), Dec = dec.Sums() }
        : null;

    // the stored timestamp went through JSON: compare to the second
    private static bool SameCalibration(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalSeconds) < 1.0;

    private bool UpdateValues()
    {
        var raEstimate = ra.Estimate();
        var decEstimate = dec.Estimate();
        var old = Values;
        double raEffect = Next(old.RaEffect, EffectTarget(raEstimate));
        double decEffect = Next(old.DecEffect, EffectTarget(decEstimate));
        Values = new PulseModelValues(raEffect, decEffect, raEstimate, decEstimate);
        return raEffect != old.RaEffect || decEffect != old.DecEffect;
    }

    // an effect in use moves in steps, and back to neutral as soon as its estimate no longer qualifies
    private static double Next(double current, double target) =>
        target == 1.0 || Math.Abs(target - current) >= EffectStep ? target : current;

    // the upper end of the effect's uncertainty: an effect that is too high leaves some error for the next frame, one that
    // is too low overshoots with every pulse
    private static double EffectTarget(PulseResponseEstimate? e) =>
        e is { } est && est.Windows >= MinWindows && est.EffectSigma <= MaxEffectSigma && double.IsFinite(est.Effect)
            ? Math.Clamp(est.Effect + est.EffectSigma, MinEffect, MaxEffect)
            : 1.0;

    /// <summary>One axis's open window and running sums.</summary>
    private sealed class AxisLearner(bool withReversals)
    {
        private readonly int k = withReversals ? 4 : 2;
        private double[] zx = [];
        private double[] zz = [];
        private double[] zy = [];
        private double[] xx = [];
        private double[] xy = [];
        private double yy;
        private double weight;
        private Window? window;

        public void Reset()
        {
            zx = new double[k * k];
            zz = new double[k * k];
            zy = new double[k];
            xx = new double[k * k];
            xy = new double[k];
            yy = 0;
            weight = 0;
            window = null;
        }

        public void Load(PulseResponseSums? s)
        {
            if (s is null || s.ZX.Length != k * k || s.ZZ.Length != k * k || s.XX.Length != k * k || s.Zy.Length != k || s.Xy.Length != k
                || !(s.Weight > 0) || !double.IsFinite(s.Weight) || !double.IsFinite(s.YY)
                || s.ZX.Concat(s.ZZ).Concat(s.XX).Concat(s.Zy).Concat(s.Xy).Any(v => !double.IsFinite(v)))
            {
                return;
            }

            zx = [.. s.ZX];
            zz = [.. s.ZZ];
            zy = [.. s.Zy];
            xx = [.. s.XX];
            xy = [.. s.Xy];
            yy = s.YY;
            weight = s.Weight;
        }

        public PulseResponseSums Sums() => new() { ZX = [.. zx], ZZ = [.. zz], Zy = [.. zy], XX = [.. xx], Xy = [.. xy], YY = yy, Weight = weight };

        public void Open(double offsetPx, int lastDirection)
        {
            if (offsetPx == 0 || !double.IsFinite(offsetPx))
            {
                window = null;
                return;
            }

            int first = Math.Sign(offsetPx);
            window = new Window
            {
                Offset = offsetPx,
                FirstReversal = lastDirection != 0 && first != lastDirection ? first : 0,
                Direction = lastDirection,
            };
        }

        public void Drop() => window = null;

        public void Pulse(int direction, double calibratedPx)
        {
            if (window is not { Frames: >= 0 } w)
            {
                return;
            }

            w.Commanded += direction * calibratedPx;
            if (w.Direction != 0 && direction != w.Direction)
            {
                w.Reversals += direction;
            }

            w.Direction = direction;
        }

        // true when the frame completed the window
        public bool Measured(double z, DateTimeOffset now)
        {
            if (window is not { } w)
            {
                return false;
            }

            if (!double.IsFinite(z))
            {
                window = null;
                return false;
            }

            if (w.Frames < 0)
            {
                w.Frames = 0;
                w.Z0 = z;
                w.T0 = now;
                return false;
            }

            if (++w.Frames < WindowFrames)
            {
                return false;
            }

            window = null;
            double dt = (now - w.T0).TotalSeconds;
            if (!(dt > 0))
            {
                return false;
            }

            double start = Math.Sign(w.Offset);
            double[] x = withReversals ? [w.Commanded, w.Reversals, start, dt] : [w.Commanded, dt];
            double[] zi = withReversals ? [w.Offset, w.FirstReversal, start, dt] : [w.Offset, dt];
            Add(x, zi, w.Z0 - z);
            return true;
        }

        private void Add(double[] x, double[] z, double y)
        {
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++)
                {
                    zx[i * k + j] = Forgetting * zx[i * k + j] + z[i] * x[j];
                    zz[i * k + j] = Forgetting * zz[i * k + j] + z[i] * z[j];
                    xx[i * k + j] = Forgetting * xx[i * k + j] + x[i] * x[j];
                }

                zy[i] = Forgetting * zy[i] + z[i] * y;
                xy[i] = Forgetting * xy[i] + x[i] * y;
            }

            yy = Forgetting * yy + y * y;
            weight = Forgetting * weight + 1;
        }

        // Dec without reversals before its dithers (no last direction known) can't tell the reversal loss: the effect alone then
        public PulseResponseEstimate? Estimate()
        {
            if (!withReversals)
            {
                return Fit([0, 1]) is { } ra ? new PulseResponseEstimate(ra.Beta[0], ra.Sigma[0], null, null, weight) : null;
            }

            if (Fit([0, 1, 2, 3]) is { } full)
            {
                return new PulseResponseEstimate(full.Beta[0], full.Sigma[0], -full.Beta[1], full.Sigma[1], weight);
            }

            return Fit([0, 2, 3]) is { } reduced ? new PulseResponseEstimate(reduced.Beta[0], reduced.Sigma[0], null, null, weight) : null;
        }

        // exactly identified two-stage least squares on the regressors and instruments idx: β = (Z'X)⁻¹ Z'y and
        // Cov = s²·(Z'X)⁻¹ Z'Z (Z'X)⁻ᵀ, with s² from the residuals
        private (double[] Beta, double[] Sigma)? Fit(int[] idx)
        {
            int n = idx.Length;
            if (weight <= n)
            {
                return null;
            }

            double Sub(double[] m, int i, int j) => m[idx[i] * k + idx[j]];
            var zxs = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    zxs[i * n + j] = Sub(zx, i, j);
                }
            }

            if (Invert(zxs, n) is not { } a)
            {
                return null;
            }

            var beta = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    beta[i] += a[i * n + j] * zy[idx[j]];
                }
            }

            double rss = yy;
            for (int i = 0; i < n; i++)
            {
                rss -= 2 * beta[i] * xy[idx[i]];
                for (int j = 0; j < n; j++)
                {
                    rss += beta[i] * Sub(xx, i, j) * beta[j];
                }
            }

            double s2 = Math.Max(rss, 0) / (weight - n);
            var sigma = new double[n];
            for (int r = 0; r < n; r++)
            {
                double v = 0;
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        v += a[r * n + i] * Sub(zz, i, j) * a[r * n + j];
                    }
                }

                sigma[r] = Math.Sqrt(Math.Max(s2 * v, 0));
            }

            return beta.Concat(sigma).All(double.IsFinite) ? (beta, sigma) : null;
        }

        // Gauss-Jordan with partial pivoting; null when (nearly) singular
        private static double[]? Invert(double[] m, int n)
        {
            var a = (double[])m.Clone();
            var inv = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                inv[i * n + i] = 1;
            }

            double scale = m.Max(Math.Abs);
            if (!(scale > 0) || !double.IsFinite(scale))
            {
                return null;
            }

            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++)
                {
                    if (Math.Abs(a[r * n + c]) > Math.Abs(a[p * n + c]))
                    {
                        p = r;
                    }
                }

                if (Math.Abs(a[p * n + c]) < 1e-10 * scale)
                {
                    return null;
                }

                if (p != c)
                {
                    for (int j = 0; j < n; j++)
                    {
                        (a[c * n + j], a[p * n + j]) = (a[p * n + j], a[c * n + j]);
                        (inv[c * n + j], inv[p * n + j]) = (inv[p * n + j], inv[c * n + j]);
                    }
                }

                double d = a[c * n + c];
                for (int j = 0; j < n; j++)
                {
                    a[c * n + j] /= d;
                    inv[c * n + j] /= d;
                }

                for (int r = 0; r < n; r++)
                {
                    if (r == c)
                    {
                        continue;
                    }

                    double f = a[r * n + c];
                    for (int j = 0; j < n; j++)
                    {
                        a[r * n + j] -= f * a[c * n + j];
                        inv[r * n + j] -= f * inv[c * n + j];
                    }
                }
            }

            return inv;
        }

        private sealed class Window
        {
            public double Offset;
            public double FirstReversal;
            public int Direction;
            public int Frames = -1;
            public double Z0;
            public DateTimeOffset T0;
            public double Commanded;
            public double Reversals;
        }
    }
}
