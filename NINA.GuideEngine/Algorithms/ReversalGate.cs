// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// Decides whether a Dec offset justifies a pulse that first has to take up the gear's play (a reversal, or a gear floating
/// inside its play): such a pulse costs the play it crosses, so it is made only when the offset on that side is clearly
/// more than the frame-to-frame noise.
/// </summary>
/// <remarks>
/// <para>Per frame the offset is standardised by the noise, z = e / σ, and a one-sided CUSUM per side accumulates the
/// evidence: S⁺ = max(0, S⁺ + z − k), S⁻ = max(0, S⁻ − z − k) (Page 1954, "Continuous inspection schemes", Biometrika
/// 41). A reversal towards South (positive offset) is allowed once S⁺ ≥ h, or at once when |z| ≥ <see
/// cref="ImmediateSigmas"/>. With pure noise the sums drift down by k per frame and reach h about once in 7000 frames
/// per side (Siegmund's approximation of the average run length, k = 0.5, h = 7); a real offset of 1σ is detected in
/// about 14 frames, one of 2σ in about 5. Each sum is capped at h, so that the evidence of a past offset (a dither)
/// lasts a frame after it is gone, not until k has worn it down: after a dither on the OnStepX of 2026-10-18 the
/// uncapped sum reached 55σ and reversals of 0.04 px went through with the whole play as compensation. The defaults
/// were chosen in closed-loop simulations of a mount like the OnStepX of the 2026-10-12 guide log
/// (docs/notes/PLAY-MODEL-BACKLASH.md).</para>
/// <para>What the gate filters is the noise: a pulse it holds would only have moved the gear inside its play, so
/// holding it does not move the star; a real offset or drift builds up the evidence and gets through.</para>
/// <para>σ is learned from the frame-to-frame changes of the offset minus the corrections the model expects to have
/// acted: σ² ≈ E[(Δe)²] / 2 for independent frames, an exponentially weighted mean with each change clipped at <see
/// cref="ClipSigmas"/> σ so that a dither or a jump does not inflate it. A frame after a correction of more than <see
/// cref="UncertainCorrectionSigmas"/> σ is left out: its change measures how well the size of the correction is known,
/// not the seeing. Nor does a change from or to an offset of more than <see cref="LargeOffsetSigmas"/> σ: the star's
/// way back after a dither runs one way frame after frame, and as wander it brought the noise down to its minimum (0.04
/// px gave z = 2 after a 3.6 px dither). Replaying PHD2 guide logs of 2026-09/10 whose compensated reversals had not
/// moved the star, each such frame raised the clipped mean a little, and it settled at about 5–7 × the real
/// noise.</para>
/// <para>z is taken against σ² − (½ − <see cref="WanderShare"/>) q, the frame-to-frame noise without most of the wander
/// q (<see cref="NoiseComponents"/>): a wander is the star really moving, which the guiding has to follow, not a noise
/// to sit out. Against the whole σ² the gate held back a Dec that wandered like a random walk until it had gone far; on
/// the Sky Simulator session of 2026-10-18 (0.12 px of random walk a frame against 0.06 px of seeing) the Dec RMS was
/// 0.19 px instead of 0.16. Without wander (or a negative one, as an over-correcting loop makes) it is σ² as
/// before.</para>
/// </remarks>
public sealed class ReversalGate
{
    /// <summary>Default CUSUM allowance k, in noise σ per frame.</summary>
    public const double DefaultAllowance = 0.5;

    /// <summary>Default CUSUM decision threshold h, in noise σ.</summary>
    public const double DefaultThreshold = 7.0;

    /// <summary>CUSUM allowance k (σ per frame): offsets below it never add evidence; about half the smallest offset worth correcting.</summary>
    public double Allowance { get; init; } = DefaultAllowance;

    /// <summary>CUSUM decision threshold h (σ): higher means fewer false alarms and later detection.</summary>
    public double Threshold { get; init; } = DefaultThreshold;

    /// <summary>An offset this many σ away reverses at once.</summary>
    public const double ImmediateSigmas = 4.0;

    /// <summary>Weight of a new frame in the noise estimate (a memory of about 20 frames).</summary>
    public const double NoiseWeight = 0.05;

    /// <summary>Frame-to-frame changes are clipped at this many σ before they enter the noise estimate.</summary>
    public const double ClipSigmas = 4.0;

    /// <summary>A frame after an expected correction of more than this many σ does not enter the noise estimate.</summary>
    public const double UncertainCorrectionSigmas = 4.0;

    /// <summary>
    /// A change from or to an offset of more than this many σ (a dither, a jump) does not enter the noise estimate. Not
    /// lower: at 4–8 σ the noise of a mount without backlash was underestimated after its dithers and the closed-loop Dec
    /// RMS rose by 5 % (<c>AMountWithoutBacklashLosesNothing</c>).
    /// </summary>
    public const double LargeOffsetSigmas = 12.0;

    /// <summary>
    /// Share of the wander q that the gate still counts as noise (½ would be the whole frame-to-frame noise). Not 0: the
    /// split of the noise into white and wander comes from a few dozen frames and is uncertain, and in the closed-loop
    /// simulations 0 cost a little where there is a hard play or a drift (docs/notes/PLAY-MODEL-BACKLASH.md).
    /// </summary>
    public const double WanderShare = 0.1;

    /// <summary>Frames before the noise estimate is used; until then every reversal is allowed.</summary>
    public const int WarmupFrames = 5;

    /// <summary>Smallest noise σ used (px), so a perfectly steady star still needs a real offset.</summary>
    public const double MinNoisePx = 0.02;

    private double? lastOffset;
    private double noiseVariance;
    private int noiseFrames;
    private double sumSouth;
    private double sumNorth;
    private double lastZ;
    private double? lastChange;
    private double twoFrameVariance;
    private int twoFrameCount;

    /// <summary>Frame-to-frame noise σ of the Dec offset (px); 0 until the first frame pair.</summary>
    public double NoiseSigmaPx => noiseFrames > 0 ? Math.Max(MinNoisePx, Math.Sqrt(noiseVariance)) : 0;

    /// <summary>
    /// The noise split into a white part σ_w² (px², the seeing and centroid of each frame) and a wander q (px² per frame, a
    /// random walk), from the variances of the open-loop changes over one and two frames: V₁ = 2σ_w² + q, V₂ = 2σ_w² + 2q.
    /// On the 2026-10-12 log the Dec offset wandered with a lag-1 autocorrelation of 0.84, so the frames of a take-up
    /// window are far from independent; the play model's learning uses both parts.
    /// </summary>
    public (double WhitePx2, double WanderPx2PerFrame) NoiseComponents
    {
        get
        {
            if (noiseFrames == 0)
            {
                return (0, 0);
            }

            if (twoFrameCount == 0)
            {
                return (Math.Max(MinNoisePx * MinNoisePx, noiseVariance), 0);
            }

            double wander = Math.Max(0, 2 * (twoFrameVariance - noiseVariance));
            double white = Math.Max(MinNoisePx * MinNoisePx, 2 * noiseVariance - twoFrameVariance);
            return (white, wander);
        }
    }

    /// <summary>True once the noise estimate is used.</summary>
    public bool IsWarm => noiseFrames >= WarmupFrames;

    /// <summary>CUSUM evidence for a South (positive offset) correction, in σ.</summary>
    public double SouthEvidence => sumSouth;

    /// <summary>CUSUM evidence for a North (negative offset) correction, in σ.</summary>
    public double NorthEvidence => sumNorth;

    /// <summary>
    /// One frame's raw Dec offset (mount px; positive is corrected by a South pulse) and the correction (px, same sign
    /// convention) the pulses since the previous frame are expected to have applied.
    /// </summary>
    public void Observe(double offsetPx, double expectedCorrectionPx)
    {
        if (!double.IsFinite(offsetPx))
        {
            return;
        }

        double sigma = Math.Max(NoiseSigmaPx, MinNoisePx);
        bool largeOffset = Math.Abs(offsetPx) > LargeOffsetSigmas * sigma || lastOffset is { } before && Math.Abs(before) > LargeOffsetSigmas * sigma;
        if (noiseFrames > 0 && (Math.Abs(expectedCorrectionPx) > UncertainCorrectionSigmas * sigma || largeOffset))
        {
            // nor does the two-frame change that would include it
            lastChange = null;
        }
        else if (lastOffset is { } previous && double.IsFinite(expectedCorrectionPx))
        {
            double change = offsetPx - previous + expectedCorrectionPx;
            double v = 0.5 * change * change;
            if (noiseFrames == 0)
            {
                noiseVariance = v;
            }
            else
            {
                double clip = ClipSigmas * ClipSigmas * Math.Max(noiseVariance, MinNoisePx * MinNoisePx);
                noiseVariance = (1 - NoiseWeight) * noiseVariance + NoiseWeight * Math.Min(v, clip);
            }

            noiseFrames++;

            if (lastChange is { } previousChange)
            {
                double two = change + previousChange;
                double v2 = 0.5 * two * two;
                double clip2 = ClipSigmas * ClipSigmas * Math.Max(twoFrameVariance, 2 * MinNoisePx * MinNoisePx);
                twoFrameVariance = twoFrameCount == 0 ? v2 : (1 - NoiseWeight) * twoFrameVariance + NoiseWeight * Math.Min(v2, clip2);
                twoFrameCount++;
            }

            lastChange = change;
        }

        lastOffset = offsetPx;
        if (!IsWarm)
        {
            return;
        }

        double variance = NoiseSigmaPx * NoiseSigmaPx - (0.5 - WanderShare) * NoiseComponents.WanderPx2PerFrame;
        lastZ = offsetPx / Math.Sqrt(Math.Max(MinNoisePx * MinNoisePx, variance));
        // capped at h: past the threshold more evidence decides nothing, and a dither (z ≈ 30) would otherwise keep the sum
        // above h for some 100 frames after the star is back, letting every reversal that way through
        sumSouth = Math.Clamp(sumSouth + lastZ - Allowance, 0, Threshold);
        sumNorth = Math.Clamp(sumNorth - lastZ - Allowance, 0, Threshold);
    }

    /// <summary>
    /// Whether the last observed offset justifies a reversal towards <paramref name="sign"/> (+1 South, −1 North).
    /// </summary>
    public bool Allows(int sign)
    {
        if (!IsWarm || sign == 0)
        {
            return true;
        }

        if (sign * lastZ >= ImmediateSigmas)
        {
            return true;
        }

        return (sign > 0 ? sumSouth : sumNorth) >= Threshold;
    }

    /// <summary>The evidence starts over (the gear or the lock position changed); the noise estimate is kept.</summary>
    public void ResetEvidence()
    {
        sumSouth = 0;
        sumNorth = 0;
        lastZ = 0;
        lastOffset = null;
        lastChange = null;
    }
}
