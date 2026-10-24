// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.MultiStar;

/// <summary>
/// Per-frame uncertainty of the measured guide-star position: how far the centroid is expected to be off the
/// star image's true position because of the noise in the frame, σ per axis in px. The seeing's image motion is not part
/// of it (the star image really moved).
/// </summary>
/// <remarks>
/// <para>One star: σ = HFD / SNR · (<see cref="CentroidTerm"/> + <see cref="ThresholdTerm"/> / SNR). The first term is the
/// centroid precision of a Gaussian PSF, σ_psf / SNR with σ_psf = HFD / (2√(2 ln 2)) (a Gaussian's HFD is its FWHM). The
/// second comes from PHD2's centroid, which only uses pixels above background + 3σ: on a faint star the pixels near that
/// threshold drop in and out from frame to frame, and the noise grows faster than 1/SNR. How well it matches the true
/// centroid error on simulated and synthetic stars: docs/ALGORITHMS.md, "Measurement uncertainty". SNR is PHD2's estimate
/// (<c>StarFinder</c>).</para>
/// <para>Several stars: the refined offset is the weighted mean of the primary's offset (weight 1) and the used
/// secondaries' displacements (weight: SNR ratio), so σ = √(Σ w²σ²) / Σ w. A primary estimated from secondaries is the
/// median of their estimates: √(π/2) · √(Σ σ²) / n from three stars on, the mean's √(Σ σ²) / n below.</para>
/// <para>A star without an SNR or HFD (not measured, or a peak-mode star without a size) makes the frame's σ unknown
/// (null): that says nothing about the frame, it does not make it worse.</para>
/// </remarks>
internal static class MeasurementUncertainty
{
    /// <summary>Gaussian-PSF centroid term: 1 / (2√(2 ln 2)).</summary>
    public const double CentroidTerm = 0.42;

    /// <summary>
    /// Threshold term of PHD2's centroid, fitted to the true centroid error of synthetic and simulated stars
    /// (<c>MeasurementUncertaintyTests.Matches_the_true_centroid_error_*</c> check the fit).
    /// </summary>
    public const double ThresholdTerm = 10;

    /// <summary>Upper bound of σ (a star barely above the noise).</summary>
    public const double MaxSigmaPx = 5;

    /// <summary>
    /// A frame's σ up to this many times the usual σ (the median of the recent frames) is the usual frame-to-frame scatter,
    /// not a noisier frame (for example a multi-star frame that guides on the primary alone). Used by the Predictive
    /// algorithm and the Coach's trial judge.
    /// </summary>
    public const double UsualSigmaSpread = 1.5;

    // a peak-mode position is the brightest pixel: at least the quantisation error of a uniform ±0.5 px
    private static readonly double PeakFloorPx = 1 / Math.Sqrt(12);

    private static readonly double MedianFactor = Math.Sqrt(Math.PI / 2);

    /// <summary>Centroid σ (px, per axis) of one star with the given HFD (px) and SNR; null without either (≤ 0 or not finite).</summary>
    public static double? StarSigmaPx(double hfd, double snr, StarFindMode mode = StarFindMode.Centroid)
    {
        if (!double.IsFinite(hfd) || !double.IsFinite(snr) || hfd <= 0 || snr <= 0)
        {
            return null;
        }

        double sigma = Math.Min(MaxSigmaPx, hfd / snr * (CentroidTerm + ThresholdTerm / snr));
        return mode == StarFindMode.Peak ? Math.Max(sigma, PeakFloorPx) : sigma;
    }

    /// <summary>
    /// σ (px, per axis) of the offset a tracked frame guides on: the primary alone, the refined multi-star offset, or the
    /// estimate from secondaries. Null when the frame has no star position or a star it used has no SNR or HFD.
    /// </summary>
    public static double? FrameSigmaPx(MultiStarFrameResult result, StarFindMode mode = StarFindMode.Centroid)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case TrackerOutcome.Found when result.Refined:
            {
                double weights = 0;
                double variance = 0;
                foreach (var s in result.Stars)
                {
                    if ((s.Status is TrackedStarStatus.Primary or TrackedStarStatus.Used) && s.Weight > 0 && double.IsFinite(s.Weight))
                    {
                        if (StarSigmaPx(s.Star.Hfd, s.Star.Snr, mode) is not { } sigma)
                        {
                            return null;
                        }

                        weights += s.Weight;
                        variance += s.Weight * s.Weight * sigma * sigma;
                    }
                }

                return weights > 0 ? Math.Min(MaxSigmaPx, Math.Sqrt(variance) / weights) : StarSigmaPx(result.Primary.Hfd, result.Primary.Snr, mode);
            }

            case TrackerOutcome.Found:
                return StarSigmaPx(result.Primary.Hfd, result.Primary.Snr, mode);
            case TrackerOutcome.Estimated:
            {
                int n = 0;
                double variance = 0;
                foreach (var s in result.Stars)
                {
                    if (s.Status == TrackedStarStatus.FallbackUsed)
                    {
                        if (StarSigmaPx(s.Star.Hfd, s.Star.Snr, mode) is not { } sigma)
                        {
                            return null;
                        }

                        variance += sigma * sigma;
                        n++;
                    }
                }

                // the median of one or two estimates is their mean
                return n > 0 ? Math.Min(MaxSigmaPx, (n >= 3 ? MedianFactor : 1) * Math.Sqrt(variance) / n) : null;
            }

            default:
                return null;
        }
    }
}
