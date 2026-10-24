// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2013-2016 Andy Galasso
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm.cpp (SmartDefaultMinMove) (a6c02722)

namespace NINA.GuideEngine.Algorithms;

/// <summary>PHD2 "smart" min-move defaults.</summary>
public static class MinMove
{
    /// <summary>Fallback when the focal length is unknown (PHD2 returns 0.2).</summary>
    public const double UnknownScaleDefault = 0.2;

    /// <summary><c>max(0.1515 + 0.1548 / pixelScale, 0.15)</c> px; <paramref name="pixelScale"/> in ″/px.</summary>
    public static double SmartDefault(double pixelScale)
    {
        // Following based on empirical data with a range of image scales
        return Math.Max(0.1515 + 0.1548 / pixelScale, 0.15);
    }

    /// <summary>
    /// Smart default from guide optics. Scale = 206.265 · pixelSize · binning / focalLength (PHD2
    /// <c>MyFrame::GetPixelScale</c>). Returns 0.2 when the focal length is 0 (unknown).
    /// </summary>
    public static double SmartDefault(double focalLengthMm, double pixelSizeUm, int binning)
    {
        // Deviation from PHD2: PHD2 truncates the focal length to int; we keep the double.
        if (focalLengthMm == 0)
            return UnknownScaleDefault;
        double imageScale = 206.265 * pixelSizeUm * binning / focalLengthMm;
        return SmartDefault(imageScale);
    }
}
