// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.Coach;

/// <summary>Primary star measurement in one camera-check frame.</summary>
public readonly record struct CameraFrameMeasurement(double T, bool Found, double X, double Y, double Snr, double Hfd, bool Saturated);

/// <summary>One exposure × gain combination to measure.</summary>
public readonly record struct CameraCombination(double ExposureSeconds, int? Gain);

/// <summary>Camera check (settings finder) evaluation: per-combination metrics, feasibility and the recommendation.</summary>
public static class CameraCheckAnalyzer
{
    /// <summary>Minimum SNR of a feasible combination.</summary>
    public const double MinSnr = 15;

    /// <summary>Candidates within this factor of the lowest jitter compete on star count, then exposure.</summary>
    public const double JitterTolerance = 1.10;

    public const string ReasonSaturated = "saturated";
    public const string ReasonLowSnr = "lowSnr";
    public const string ReasonNoStar = "noStar";

    /// <summary>Evaluates the frames of one combination.</summary>
    /// <param name="stars">Usable stars found by AutoFind on the first frame (0 = none).</param>
    public static CoachCameraResult Evaluate(CameraCombination c, IReadOnlyList<CameraFrameMeasurement> frames, int stars, double pixelScale)
    {
        var found = frames.Where(f => f.Found).ToList();
        bool saturated = found.Any(f => f.Saturated);
        double? snr = found.Count > 0 ? found.Average(f => f.Snr) : null;
        double? hfd = found.Count > 0 ? found.Average(f => f.Hfd) : null;
        double? jitter = found.Count >= 3 ? DetrendedJitter(found) : null;
        string? reason = stars == 0 || found.Count < 3 ? ReasonNoStar
            : saturated ? ReasonSaturated
            : snr < MinSnr ? ReasonLowSnr
            : null;
        double scale = pixelScale > 0 ? pixelScale : 1.0;
        return new CoachCameraResult
        {
            ExposureSeconds = c.ExposureSeconds,
            Gain = c.Gain ?? -1,
            Frames = frames.Count,
            Snr = Round(snr, 1),
            Hfd = Round(hfd, 2),
            Stars = stars,
            Saturated = saturated,
            JitterPx = Round(jitter, 3),
            JitterArcsec = Round(jitter * scale, 3),
            Feasible = reason is null,
            Reason = reason,
        };
    }

    /// <summary>
    /// Recommended combination: lowest jitter among the feasible ones; within 10 % of it prefer more stars, then the shorter
    /// exposure (then the lower gain, then the earlier combination). Null when none is feasible.
    /// </summary>
    public static CoachCameraResult? SelectRecommended(IReadOnlyList<CoachCameraResult> results)
    {
        var feasible = results.Where(r => r.Feasible && r.JitterArcsec is not null).ToList();
        if (feasible.Count == 0)
        {
            return null;
        }

        double best = feasible.Min(r => r.JitterArcsec!.Value);
        return feasible
            .Select((r, i) => (r, i))
            .Where(x => x.r.JitterArcsec!.Value <= best * JitterTolerance + 1e-9)
            .OrderByDescending(x => x.r.Stars)
            .ThenBy(x => x.r.ExposureSeconds)
            .ThenBy(x => x.r.Gain)
            .ThenBy(x => x.i)
            .First().r;
    }

    /// <summary>One-sided confidence a camera change needs, before the correction for picking the best of several.</summary>
    public const double ChangeConfidence = 0.95;

    /// <summary>
    /// The recommendation replaces the current settings only when that is justified: the current combination was not
    /// measured or is not feasible, it has fewer than 3 usable stars and the recommendation at least 3, or the recommendation's
    /// jitter is significantly lower (<see cref="IsSignificantlySteadier"/>). Otherwise the current combination stays: with a
    /// few frames per combination the jitter is dominated by seeing, which changes between the combinations.
    /// </summary>
    /// <returns>The combination to use, null when none is feasible.</returns>
    public static CoachCameraResult? Recommend(IReadOnlyList<CoachCameraResult> results, CoachCameraResult? current)
    {
        var candidate = SelectRecommended(results);
        if (candidate is null || current is null || ReferenceEquals(candidate, current) || !current.Feasible || current.JitterPx is null)
        {
            return candidate;
        }

        if (current.Stars < 3 && candidate.Stars >= 3)
        {
            return candidate;
        }

        int alternatives = Math.Max(1, results.Count(r => r.Feasible && r.JitterPx is not null) - 1);
        return IsSignificantlySteadier(candidate, current, alternatives) ? candidate : current;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>'s jitter is significantly lower than <paramref name="current"/>'s:
    /// ln(σ²cur/σ²cand) &gt; z·√(2/νcur + 2/νcand), the normal approximation of the variance-ratio test, with ν = 2·(frames − 2)
    /// (x and y after the drift fit) and z the one-sided quantile of <see cref="ChangeConfidence"/>, Bonferroni-corrected for the
    /// <paramref name="alternatives"/> the candidate was picked from.
    /// </summary>
    public static bool IsSignificantlySteadier(CoachCameraResult candidate, CoachCameraResult current, int alternatives)
    {
        if (candidate.JitterPx is not { } jc || current.JitterPx is not { } ju || !(ju > 0))
        {
            return false;
        }

        if (!(jc > 0))
        {
            return true;
        }

        double nuCandidate = 2.0 * Math.Max(1, candidate.Frames - 2);
        double nuCurrent = 2.0 * Math.Max(1, current.Frames - 2);
        double z = UpperNormalQuantile((1 - ChangeConfidence) / Math.Max(1, alternatives));
        return Math.Log(ju * ju / (jc * jc)) > z * Math.Sqrt(2 / nuCurrent + 2 / nuCandidate);
    }

    /// <summary>z with P(Z &gt; z) = <paramref name="p"/> (0 &lt; p ≤ 0.5), Abramowitz &amp; Stegun 26.2.23 (error &lt; 4.5e-4).</summary>
    public static double UpperNormalQuantile(double p)
    {
        p = Math.Clamp(p, 1e-12, 0.5);
        double t = Math.Sqrt(-2 * Math.Log(p));
        return t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
    }

    /// <summary>Most common reason among infeasible combinations (for camera.noFeasible).</summary>
    public static string DominantReason(IReadOnlyList<CoachCameraResult> results) =>
        results.Where(r => r.Reason is not null).GroupBy(r => r.Reason!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault() ?? ReasonNoStar;

    /// <summary>
    /// Defocus: HFD above 4 px and above 5″. Both are required: undersampled guide scopes (≥ 3″/px) show about 2 px ≈ 6″
    /// even in perfect focus, and finely sampled ones (≤ 0.5″/px) show many pixels for a normal 3–4″ star.
    /// </summary>
    public static bool IsDefocused(double hfdPx, double pixelScale) => hfdPx > 4 && hfdPx * (pixelScale > 0 ? pixelScale : 1.0) > 5;

    /// <summary>Centroid noise estimate (arcsec, total of both axes) from HFD and SNR: √2·HFD/(2.355·SNR)·scale.</summary>
    public static double? CentroidNoiseArcsec(double? hfdPx, double? snr, double pixelScale) =>
        hfdPx is { } h && snr is { } s && s > 0 ? Math.Sqrt(2) * h / (2.355 * s) * (pixelScale > 0 ? pixelScale : 1.0) : null;

    /// <summary>√(σx² + σy²) of the positions after removing a linear drift in x and y (n − 2 degrees of freedom).</summary>
    public static double DetrendedJitter(IReadOnlyList<CameraFrameMeasurement> frames)
    {
        var t = frames.Select(f => f.T).ToArray();
        double vx = ResidualVariance(t, frames.Select(f => f.X).ToArray());
        double vy = ResidualVariance(t, frames.Select(f => f.Y).ToArray());
        return Math.Sqrt(vx + vy);
    }

    private static double ResidualVariance(double[] t, double[] v)
    {
        var (slope, intercept) = DriftAnalyzer.LinearFit(t, v);
        double sse = t.Select((x, i) => v[i] - (slope * x + intercept)).Sum(r => r * r);
        return sse / Math.Max(1, t.Length - 2);
    }

    private static double? Round(double? v, int digits) => v is { } d && double.IsFinite(d) ? Math.Round(d, digits) : null;
}

/// <summary>
/// Captures the camera-check frames directly from the camera (the capture loop must be stopped), preprocesses them like
/// the loop and measures the primary star per exposure × gain combination.
/// </summary>
internal sealed class CameraSweep(ICameraSource camera, FramePreprocessor preprocessor, IClock clock)
{
    /// <summary>Measures one combination. Returns the frame measurements and the usable star count.</summary>
    public async Task<(IReadOnlyList<CameraFrameMeasurement> Frames, int Stars)> MeasureAsync(CameraCombination c, int frames, int binning, int? offset,
        StarFinderOptions finder, int maxStars, Action<GuideFrame, Star?>? onFrame, CancellationToken ct, Func<CancellationToken, Task>? beforeCapture = null)
    {
        var result = new List<CameraFrameMeasurement>(frames);
        int stars = 0;
        Star? primary = null;
        double t0 = clock.NowSeconds();
        int failures = 0;
        for (int i = 0; i < frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (beforeCapture is not null)
            {
                await beforeCapture(ct).ConfigureAwait(false);
            }

            GuideFrame raw;
            double t = clock.NowSeconds() - t0;
            try
            {
                raw = await camera.CaptureAsync(new CaptureRequest(c.ExposureSeconds * 1000.0, binning, default, c.Gain, offset), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (++failures <= 2)
            {
                i--;
                continue;
            }

            var frame = preprocessor.Process(raw);
            if (i == 0 || primary is null)
            {
                var found = GuideStar.AutoFind(frame, 0, finder.SearchRegion, default, maxStars, finder);
                stars = found.Count(s => s.Snr >= finder.MinSnr);
                if (found.Count > 0)
                {
                    primary = new Star { Position = found[0].Position };
                }
            }

            bool ok = false;
            if (primary is not null)
            {
                ok = primary.Find(frame, finder.SearchRegion, finder.FindMode, finder.MinHfd, finder.MaxHfd, finder.SaturationAdu);
            }

            bool saturated = ok && (primary!.LastFindResult == StarFindResult.Saturated || (camera.MaxAdu > 0 && primary.PeakValue >= camera.MaxAdu));
            result.Add(ok
                ? new CameraFrameMeasurement(t, true, primary!.X, primary.Y, primary.Snr, primary.Hfd, saturated)
                : new CameraFrameMeasurement(t, false, 0, 0, 0, 0, false));
            onFrame?.Invoke(frame, ok ? primary : null);
        }

        return (result, stars);
    }
}
