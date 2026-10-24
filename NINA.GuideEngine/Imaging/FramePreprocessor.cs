// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Imaging;

/// <summary>Noise reduction applied after dark/defect correction (PHD2 NOISE_REDUCTION_METHOD).</summary>
public enum NoiseReduction
{
    None,

    /// <summary>Sliding 2x2 mean (PHD2 QuickLRecon).</summary>
    Mean2x2,

    /// <summary>3x3 median (PHD2 Median3).</summary>
    Median3x3,
}

/// <summary>Result summary of <see cref="FramePreprocessor.Process"/>.</summary>
public readonly record struct PreprocessInfo(bool DefectMapApplied, bool DarkSubtracted, double? DarkExposureMs, int SoftwareBinning, NoiseReduction NoiseReduction);

/// <summary>
/// Per-frame image pipeline in PHD2 order: defect-map correction (replaces dark subtraction when a
/// map is set) or dark subtraction → optional software binning → optional noise reduction.
/// Works on subframes (only the valid region is processed). Reuses an internal scratch buffer, so an
/// instance must not be used concurrently from multiple threads.
/// </summary>
/// <remarks>
/// PHD2 subtracts darks inside the camera capture, bins in GuideCamera::Capture and applies noise
/// reduction in the worker thread; this class reproduces that order. Darks are expected at software
/// binning 1 (as PHD2 captures them) so one library serves every software binning level.
/// </remarks>
public sealed class FramePreprocessor
{
    private ushort[]? scratch;

    /// <summary>Dark library used when no defect map is loaded (null = no dark subtraction).</summary>
    public DarkLibrary? Darks { get; set; }

    /// <summary>Defect map; when set it replaces dark subtraction (PHD2 SubtractDark).</summary>
    public DefectMap? DefectMap { get; set; }

    public NoiseReduction NoiseReduction { get; set; } = NoiseReduction.None;

    /// <summary>Software binning factor 1..4 applied after dark/defect correction.</summary>
    public int SoftwareBinning { get; set; } = 1;

    /// <summary>
    /// Processes <paramref name="frame"/>. Returns the same instance (modified in place) unless software
    /// binning is active, in which case a new, binned frame is returned.
    /// </summary>
    public GuideFrame Process(GuideFrame frame) => Process(frame, out _);

    /// <inheritdoc cref="Process(GuideFrame)"/>
    public GuideFrame Process(GuideFrame frame, out PreprocessInfo info)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool defects = false, dark = false;
        double? darkExp = null;

        if (DefectMap is { } map)
        {
            map.Apply(frame);
            defects = true;
        }
        else if (Darks is { } lib)
        {
            var d = lib.SelectDark(frame.ExposureMs, frame.Binning);
            if (d is not null && DarkLibrary.Subtract(frame, d, lib.PedestalMode))
            {
                dark = true;
                darkExp = d.ExposureMs;
            }
        }

        var result = frame;
        if (SoftwareBinning > 1)
            result = ImageMath.SoftwareBin(frame, SoftwareBinning);

        switch (NoiseReduction)
        {
            case NoiseReduction.Mean2x2:
                scratch = ImageMath.EnsureScratch(scratch, result.Pixels.Length);
                ImageMath.QuickLRecon(result, scratch);
                break;
            case NoiseReduction.Median3x3:
                scratch = ImageMath.EnsureScratch(scratch, result.Pixels.Length);
                ImageMath.Median3(result, scratch);
                break;
        }

        info = new PreprocessInfo(defects, dark, darkExp, SoftwareBinning, NoiseReduction);
        return result;
    }
}
