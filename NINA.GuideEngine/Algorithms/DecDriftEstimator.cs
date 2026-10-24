// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Algorithms;

/// <summary>A drift rate with its standard error.</summary>
/// <param name="PxPerSec">
/// Drift of the open-loop position, mount px per second: positive when the offset grows positive, i.e. the mount drifts
/// the way a North pulse moves it and South pulses correct it.
/// </param>
/// <param name="SigmaPxPerSec">Standard error of <paramref name="PxPerSec"/>.</param>
/// <param name="SpanSec">Guide time the estimate covers (the sum of its segments).</param>
/// <param name="Samples">Guide frames in it.</param>
/// <param name="NoisePx">Measurement noise of a single frame (seeing and centroid), σ in px.</param>
internal readonly record struct DriftEstimate(double PxPerSec, double SigmaPxPerSec, double SpanSec, int Samples, double NoisePx = 0)
{
    /// <summary>|drift| in standard errors (infinite for an exact estimate of a drift other than 0).</summary>
    public double Sigmas => SigmaPxPerSec > 0 ? Math.Abs(PxPerSec) / SigmaPxPerSec : PxPerSec != 0 ? double.PositiveInfinity : 0;
}

/// <summary>
/// Dec drift of the open-loop position: the measured Dec offset plus the motion of every correction that went out, fitted
/// with a straight line over a sliding window. Guiding hides the drift in the offsets; the open-loop position shows the
/// mount's motion as if it had not been guided.
/// </summary>
/// <remarks>
/// The corrections pass through a dead-band model whose dead band (backlash) is learned from the smoothness of the
/// open-loop motion; the fit has one level per segment between jumps, and its uncertainty includes the mount's wander,
/// learned over the session. The derivation is in docs/ALGORITHMS.md, "Dec drift estimator".
/// </remarks>
internal sealed class DecDriftEstimator
{
    /// <summary>Length of the sliding window, seconds (longer with long frames, see <see cref="WindowFrames"/>).</summary>
    public const double WindowSec = 300;

    /// <summary>
    /// The window covers at least this many frame intervals (the median in the window): with <see cref="WindowSec"/> alone,
    /// frames longer than 15 s would never give <see cref="MinSamples"/>.
    /// </summary>
    public const int WindowFrames = 25;

    /// <summary>Guide time needed before there is an estimate, seconds.</summary>
    public const double MinSpanSec = 120;

    /// <summary>Guide frames needed before there is an estimate.</summary>
    public const int MinSamples = 20;

    /// <summary>Length of the successive slopes whose change measures the mount's wander, seconds.</summary>
    public const double WanderLagSec = 60;

    /// <summary>Memory of the wander measurement, seconds.</summary>
    public const double WanderMemorySec = 1800;

    /// <summary>Prior of the wander: one noise σ per this many seconds (q = σ²/this).</summary>
    public const double PriorWanderSec = 60;

    /// <summary>Weight of the prior in independent wander measurements (each covers 2 × <see cref="WanderLagSec"/>).</summary>
    public const double PriorWanderWeight = 3;

    /// <summary>Longest wait for the mount to follow a reversed Dec direction (see <see cref="GuardReversals"/>), seconds.</summary>
    public const double TakeUpTimeoutSec = 120;

    /// <summary>The Dec dead bands (backlash, mount px) the open-loop position is reconstructed with.</summary>
    public static readonly IReadOnlyList<double> BacklashCandidatesPx = [0, 0.5, 1, 1.5, 2, 3, 4, 6, 8, 11, 16, 22];

    /// <summary>
    /// A jump: the open-loop position changed between two frames by more than this many σ of the frame-to-frame noise
    /// with every dead band.
    /// </summary>
    public const double JumpSigmas = 5;

    // Huber weights beyond this many robust σ of the residuals, and the reweighting passes
    private const double HuberK = 2.0;
    private const int Iterations = 3;

    // 1 / Φ⁻¹(0.75): median absolute deviation → σ of a normal distribution
    private const double MadToSigma = 1.4826;

    // a slope change beyond this many σ of its expectation counts only up to there in the wander (a bump, a gust)
    private const double WanderClipSigmas = 4;

    // the frame noise is known after this many frames (a count: the weight of a long frame in its sums is small), well
    // enough to tell jumps after this many
    private const int MinNoiseFrames = 5;
    private const int JumpNoiseFrames = 30;

    // the median of fewer frame-to-frame steps of the residuals is no estimate of the noise
    private const int MinNoiseSteps = 5;

    // per-segment sums on the stack up to this many segments (a window rarely has more than a few)
    private const int MaxStackSegments = 64;

    /// <summary>
    /// A dead band is used when its slope changes sum to less than this share of those without one, and the largest dead
    /// band's to at least <see cref="BacklashMinimumDepth"/> × its own (a minimum, not a plateau).
    /// </summary>
    public const double BacklashEvidence = 0.5;

    /// <summary>See <see cref="BacklashEvidence"/>.</summary>
    public const double BacklashMinimumDepth = 1.2;

    // another dead band replaces the one in use once its sum is this much smaller
    private const double BacklashSwitchMargin = 0.9;

    private readonly List<Sample> samples = [];

    // per dead band: the open-loop position change of the corrections since the reset, the play of the gears inside the
    // dead band (−b/2 … +b/2), and the squared slope changes over single frames and over the wander lag (exponential memory)
    private readonly double[] openLoop = new double[BacklashCandidatesPx.Count];
    private readonly double[] play = new double[BacklashCandidatesPx.Count];
    private readonly double[] frameChanges = new double[BacklashCandidatesPx.Count];
    private readonly double[] slopeChanges = new double[BacklashCandidatesPx.Count];
    private int backlash;

    private int segment;
    private bool newSegment = true;
    private DateTimeOffset? epoch;

    // session sums (exponential memory): the coefficients of q and σ² in the expectations of the squared slope changes over
    // the lag and (σ² only) over single frames, and the guide time the lagged ones cover
    private double wanderWalk;
    private double wanderNoise;
    private double wanderSeconds;
    private double noiseNoise;
    private int noiseFrames;

    // backlash take-up after a reversal of the Dec corrections: the sign of the new corrections (0 = none) and since when
    private int lastCorrectionSign;
    private int takeUpSign;
    private double? takeUpSince;

    /// <summary>
    /// True while one Dec direction is guided: a reversal of the Dec corrections (a new direction, the safety valve,
    /// settling after a dither) first takes up the backlash, so the corrections that went out overstate the motion until
    /// the mount follows. The frames are left out until the offset has crossed the lock position (the new direction moved
    /// the star) or <see cref="TakeUpTimeoutSec"/> passed, then a new segment starts. While both directions are guided the
    /// reversals are too frequent for that; the dead-band model has to cover them.
    /// </summary>
    public bool GuardReversals { get; set; }

    /// <summary>
    /// True while the dead band is in doubt: both directions are guided again after one only (the gears may sit anywhere in
    /// their play). Any smoother dead band than the one in use then makes the drift uncertain by the difference it makes;
    /// otherwise only a minimum that is not yet clear does.
    /// </summary>
    public bool DoubtBacklash { get; set; }

    /// <summary>True while frames are left out because the mount takes up backlash after a reversal.</summary>
    public bool TakingUpBacklash => takeUpSign != 0;

    /// <summary>The Dec dead band (mount px) the open-loop position is reconstructed with.</summary>
    public double BacklashPx => BacklashCandidatesPx[backlash];

    /// <summary>Guide frames in the window.</summary>
    public int Count => samples.Count;

    /// <summary>
    /// Length of the window now, seconds: <see cref="WindowSec"/>, or <see cref="WindowFrames"/> frame intervals when that
    /// is longer.
    /// </summary>
    public double WindowLengthSec => Math.Max(WindowSec, WindowFrames * MedianIntervalSec());

    /// <summary>Jumps of the open-loop position since the reset (see <see cref="JumpSigmas"/>).</summary>
    public int Jumps { get; private set; }

    /// <summary>Forgets everything (a new guiding session, a large slew, a meridian flip).</summary>
    public void Reset()
    {
        samples.Clear();
        Array.Clear(openLoop);
        Array.Clear(play);
        Array.Clear(frameChanges);
        Array.Clear(slopeChanges);
        backlash = 0;
        segment = 0;
        newSegment = true;
        epoch = null;
        wanderWalk = wanderNoise = wanderSeconds = 0;
        noiseNoise = 0;
        noiseFrames = 0;
        lastCorrectionSign = 0;
        takeUpSign = 0;
        takeUpSince = null;
        Jumps = 0;
    }

    /// <summary>
    /// The mount's wander as a random-walk rate q (px²/s): measured over the session, shrunk towards the prior of one σ of
    /// the frame noise per <see cref="PriorWanderSec"/> while there is little data. The frame noise is the one measured
    /// over the session; <paramref name="noiseVariance"/> (px²) stands in for it only until there is one.
    /// </summary>
    public double WanderRate(double noiseVariance)
    {
        double noise = noiseNoise > 0 ? frameChanges[backlash] / noiseNoise : noiseVariance;
        double prior = noise / PriorWanderSec;
        double measured = wanderWalk > 0 ? Math.Max(0, (slopeChanges[backlash] - noise * wanderNoise) / wanderWalk) : 0;
        double weight = wanderSeconds / (2 * WanderLagSec);
        return (PriorWanderWeight * prior + weight * measured) / (PriorWanderWeight + weight);
    }

    /// <summary>A correction that went out (mount px, the amount by which a positive offset is reduced).</summary>
    public void CorrectionApplied(double px)
    {
        if (!double.IsFinite(px))
        {
            return;
        }

        for (int k = 0; k < openLoop.Length; k++)
        {
            // the motor turns by px; the mount follows once the play reached the side of the dead band
            double half = BacklashCandidatesPx[k] / 2;
            double next = Math.Clamp(play[k] + px, -half, half);
            openLoop[k] += px - (next - play[k]);
            play[k] = next;
        }

        int sign = Math.Sign(px);
        if (sign == 0)
        {
            return;
        }

        if (GuardReversals && lastCorrectionSign != 0 && sign != lastCorrectionSign)
        {
            takeUpSign = sign;
            takeUpSince = null;
        }

        lastCorrectionSign = sign;
    }

    /// <summary>The open-loop position may have jumped: the next frame starts a new segment (the drift is kept).</summary>
    public void Break() => newSegment = true;

    /// <summary>One measured Dec offset (mount px) at <paramref name="time"/>, before the correction of that frame went out.</summary>
    public void Add(DateTimeOffset time, double offsetPx)
    {
        if (!double.IsFinite(offsetPx))
        {
            return;
        }

        epoch ??= time;
        double t = (time - epoch.Value).TotalSeconds;
        if (samples.Count > 0 && t <= samples[^1].T)
        {
            // the clock went back (or stood still): keep the segments ordered in time
            newSegment = true;
            samples.Clear();
        }

        if (takeUpSign != 0)
        {
            // South corrections (positive) reduce positive offsets: the mount follows once they pushed the offset across 0
            takeUpSince ??= t;
            bool followed = takeUpSign > 0 ? offsetPx <= 0 : offsetPx >= 0;
            if (!followed && t - takeUpSince.Value < TakeUpTimeoutSec)
            {
                return;
            }

            takeUpSign = 0;
            takeUpSince = null;
            newSegment = true;
        }

        double dt = samples.Count > 0 ? t - samples[^1].T : 0;
        var positions = new double[openLoop.Length];
        for (int k = 0; k < positions.Length; k++)
        {
            positions[k] = offsetPx + openLoop[k];
        }

        if (!newSegment && samples.Count > 0 && IsJump(positions, samples[^1].P))
        {
            newSegment = true;
            Jumps++;
        }

        if (newSegment)
        {
            segment++;
            newSegment = false;
        }

        samples.Add(new Sample(t, positions, segment));
        LearnWander(dt);
        double window = WindowLengthSec;
        int old = 0;
        while (old < samples.Count && samples[old].T < t - window)
        {
            old++;
        }

        samples.RemoveRange(0, old);
    }

    /// <summary>The drift over the window, null without enough data (<see cref="MinSpanSec"/>, <see cref="MinSamples"/>).</summary>
    public DriftEstimate? Estimate()
    {
        int n = samples.Count;
        if (n < MinSamples)
        {
            return null;
        }

        var segments = Segments();
        double span = segments.Sum(s => samples[s.End - 1].T - samples[s.Start].T);
        if (span < MinSpanSec)
        {
            return null;
        }

        var residuals = new double[n];
        double slope = RobustSlope(backlash, segments, residuals);
        if (!double.IsFinite(slope))
        {
            return null;
        }

        double variance = SlopeVariance(segments, residuals, out double noise);

        // another dead band that may be the right one: the drift is uncertain by the difference it makes
        int doubt = DoubtBacklash ? SmoothestBacklash() : PreferredBacklash();
        if (doubt != backlash && RobustSlope(doubt, segments, new double[n]) is var other && double.IsFinite(other))
        {
            variance += (other - slope) * (other - slope);
        }

        double sigma = Math.Sqrt(variance);
        return double.IsFinite(sigma) ? new DriftEstimate(slope, sigma, span, n, noise) : null;
    }

    // Huber-weighted slope of the open-loop position with dead band b; fills the residuals, NaN without a time span
    private double RobustSlope(int b, List<(int Start, int End)> segments, double[] residuals)
    {
        int n = samples.Count;
        var weights = new double[n];
        Array.Fill(weights, 1.0);
        var abs = new double[n];
        double slope = Fit(b, segments, weights, residuals);
        for (int pass = 0; pass < Iterations && double.IsFinite(slope); pass++)
        {
            for (int i = 0; i < n; i++)
            {
                abs[i] = Math.Abs(residuals[i]);
            }

            double cut = HuberK * MadToSigma * Median(abs, n);
            if (cut <= 0)
            {
                break;
            }

            for (int i = 0; i < n; i++)
            {
                weights[i] = abs[i] <= cut ? 1.0 : cut / abs[i];
            }

            slope = Fit(b, segments, weights, residuals);
        }

        return slope;
    }

    // contiguous index ranges [Start, End) of the segments in the window
    private List<(int Start, int End)> Segments()
    {
        var ranges = new List<(int, int)>();
        int start = 0;
        for (int i = 1; i <= samples.Count; i++)
        {
            if (i == samples.Count || samples[i].Segment != samples[start].Segment)
            {
                ranges.Add((start, i));
                start = i;
            }
        }

        return ranges;
    }

    // weighted least-squares slope with one level per segment for dead band b; fills the residuals, NaN without a time span
    private double Fit(int b, List<(int Start, int End)> segments, double[] w, double[] residuals)
    {
        double P(int i) => samples[i].P[b];

        Span<double> meanT = segments.Count <= MaxStackSegments ? stackalloc double[segments.Count] : new double[segments.Count];
        Span<double> meanP = segments.Count <= MaxStackSegments ? stackalloc double[segments.Count] : new double[segments.Count];
        double sxx = 0;
        double sxy = 0;
        for (int k = 0; k < segments.Count; k++)
        {
            var (start, end) = segments[k];
            double sw = 0, st = 0, sp = 0;
            for (int i = start; i < end; i++)
            {
                sw += w[i];
                st += w[i] * samples[i].T;
                sp += w[i] * P(i);
            }

            meanT[k] = st / sw;
            meanP[k] = sp / sw;
            for (int i = start; i < end; i++)
            {
                double x = samples[i].T - meanT[k];
                sxx += w[i] * x * x;
                sxy += w[i] * x * (P(i) - meanP[k]);
            }
        }

        if (sxx <= 0)
        {
            return double.NaN;
        }

        double slope = sxy / sxx;
        for (int k = 0; k < segments.Count; k++)
        {
            var (start, end) = segments[k];
            for (int i = start; i < end; i++)
            {
                residuals[i] = P(i) - meanP[k] - slope * (samples[i].T - meanT[k]);
            }
        }

        return slope;
    }

    // Var(slope) = σ²/Σ(t − t̄)² + 1.2·q·Σ_k (Sxx_k/Sxx)²/T_k (per segment: independent levels and walks)
    private double SlopeVariance(List<(int Start, int End)> segments, double[] residuals, out double noiseSigma)
    {
        noiseSigma = double.NaN;
        var steps = new List<double>(samples.Count);
        foreach (var (start, end) in segments)
        {
            for (int i = start + 1; i < end; i++)
            {
                steps.Add(Math.Abs(residuals[i] - residuals[i - 1]));
            }
        }

        if (steps.Count < MinNoiseSteps)
        {
            return double.NaN;
        }

        // successive residuals differ by two independent measurement errors (and a negligible bit of wander)
        double noise = MadToSigma * Median(steps.ToArray(), steps.Count);
        double noiseVariance = noise * noise / 2;
        noiseSigma = Math.Sqrt(noiseVariance);

        double sxx = 0;
        var segmentSxx = new double[segments.Count];
        for (int k = 0; k < segments.Count; k++)
        {
            var (start, end) = segments[k];
            double mean = 0;
            for (int i = start; i < end; i++)
            {
                mean += samples[i].T;
            }

            mean /= end - start;
            for (int i = start; i < end; i++)
            {
                double x = samples[i].T - mean;
                segmentSxx[k] += x * x;
            }

            sxx += segmentSxx[k];
        }

        if (sxx <= 0)
        {
            return double.NaN;
        }

        double walkRate = WanderRate(noiseVariance);
        double walkTerm = 0;
        for (int k = 0; k < segments.Count; k++)
        {
            var (start, end) = segments[k];
            double t = samples[end - 1].T - samples[start].T;
            if (t > 0)
            {
                double share = segmentSxx[k] / sxx;
                walkTerm += share * share * 1.2 * walkRate / t;
            }
        }

        return noiseVariance / sxx + walkTerm;
    }

    // the newest sample's slope change against the previous interval, over single frames (noise; the dead band) and over
    // the wander lag, for every dead band
    private void LearnWander(double dt)
    {
        double decay = dt > 0 ? Math.Exp(-dt / WanderMemorySec) : 1;
        for (int b = 0; b < slopeChanges.Length; b++)
        {
            frameChanges[b] *= decay;
            slopeChanges[b] *= decay;
        }

        wanderWalk *= decay;
        wanderNoise *= decay;
        wanderSeconds *= decay;
        noiseNoise *= decay;

        int i = samples.Count - 1;
        double noise = noiseFrames >= MinNoiseFrames && noiseNoise > 0 ? frameChanges[backlash] / noiseNoise : double.NaN;
        if (i >= 2 && samples[i - 2].Segment == samples[i].Segment)
        {
            for (int b = 0; b < frameChanges.Length; b++)
            {
                if (SlopeChange(i, i - 1, i - 2, b) is { } frame)
                {
                    frameChanges[b] += Clip(frame.Squared, noise * frame.Noise);
                    noiseNoise += b == 0 ? frame.Noise : 0;
                }
            }

            noiseFrames++;
        }

        int j = LatestAtOrBefore(samples[i].T - WanderLagSec, i);
        int k = j >= 0 ? LatestAtOrBefore(samples[j].T - WanderLagSec, j) : -1;
        if (k < 0 || samples[k].Segment != samples[i].Segment
            || samples[i].T - samples[j].T > 1.5 * WanderLagSec || samples[j].T - samples[k].T > 1.5 * WanderLagSec)
        {
            return;
        }

        double rate = double.IsNaN(noise) ? double.NaN : WanderRate(noise);
        for (int b = 0; b < slopeChanges.Length; b++)
        {
            if (SlopeChange(i, j, k, b) is { } lagged)
            {
                slopeChanges[b] += Clip(lagged.Squared, rate * lagged.Walk + noise * lagged.Noise);
                if (b == 0)
                {
                    wanderWalk += lagged.Walk;
                    wanderNoise += lagged.Noise;
                    wanderSeconds += dt;
                }
            }
        }

        SelectBacklash();
    }

    // the dead band with the smoothest open-loop motion (the smallest sum of slope changes)
    private int SmoothestBacklash()
    {
        int best = 0;
        for (int b = 1; b < slopeChanges.Length; b++)
        {
            if (slopeChanges[b] < slopeChanges[best])
            {
                best = b;
            }
        }

        return best;
    }

    // the smoothest dead band when that is a minimum (larger ones are rougher again), else none
    private int PreferredBacklash()
    {
        int best = SmoothestBacklash();
        return best > 0 && slopeChanges[^1] >= BacklashMinimumDepth * slopeChanges[best] ? best : 0;
    }

    // the preferred dead band once it is also clearly smoother than none (see BacklashEvidence), else none
    private void SelectBacklash()
    {
        int preferred = PreferredBacklash();
        int next = preferred > 0 && slopeChanges[preferred] < BacklashEvidence * slopeChanges[0] ? preferred : 0;
        if (next != backlash && (next == 0 || backlash == 0 || slopeChanges[next] < BacklashSwitchMargin * slopeChanges[backlash]))
        {
            backlash = next;
        }
    }

    // a change between two frames that no dead band explains (see JumpSigmas)
    private bool IsJump(double[] positions, double[] previous)
    {
        if (noiseFrames < JumpNoiseFrames)
        {
            return false;
        }

        double limit = JumpSigmas * Math.Sqrt(2 * frameChanges[backlash] / noiseNoise);
        for (int b = 0; b < positions.Length; b++)
        {
            if (Math.Abs(positions[b] - previous[b]) <= limit)
            {
                return false;
            }
        }

        return true;
    }

    private static double Clip(double squared, double expected) =>
        double.IsFinite(expected) && expected > 0 ? Math.Min(squared, WanderClipSigmas * WanderClipSigmas * expected) : squared;

    // (p_i − p_j)/τ₁ − (p_j − p_k)/τ₂ squared for dead band b, with the coefficients of q and σ² in its expectation
    private (double Squared, double Walk, double Noise)? SlopeChange(int i, int j, int k, int b)
    {
        double t1 = samples[i].T - samples[j].T;
        double t2 = samples[j].T - samples[k].T;
        if (t1 <= 0 || t2 <= 0)
        {
            return null;
        }

        var pi = samples[i].P;
        var pj = samples[j].P;
        var pk = samples[k].P;
        double change = (pi[b] - pj[b]) / t1 - (pj[b] - pk[b]) / t2;
        double a = 1 / t1;
        double c = 1 / t2;
        return (change * change, a + c, a * a + (a + c) * (a + c) + c * c);
    }

    // the median time between successive frames in the window, 0 with fewer than two
    private double MedianIntervalSec()
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var intervals = new double[samples.Count - 1];
        for (int i = 1; i < samples.Count; i++)
        {
            intervals[i - 1] = samples[i].T - samples[i - 1].T;
        }

        return Median(intervals, intervals.Length);
    }

    // index of the latest sample before `before` with a time at or before t, −1 when none
    private int LatestAtOrBefore(double t, int before)
    {
        int lo = 0;
        int hi = before - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].T <= t)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private static double Median(double[] values, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var copy = values.AsSpan(0, count).ToArray();
        Array.Sort(copy);
        return count % 2 == 1 ? copy[count / 2] : 0.5 * (copy[count / 2 - 1] + copy[count / 2]);
    }

    // the open-loop position of the frame for every dead band of BacklashCandidatesPx
    private readonly record struct Sample(double T, double[] P, int Segment);
}
