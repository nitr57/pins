// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>Guide optics description used to derive the image scale.</summary>
public sealed record GuideOptics(double FocalLengthMm, double PixelSizeUm, int Binning = 1)
{
    /// <summary>Image scale in arcsec per (binned) pixel. 1.0 when the optics are unknown.</summary>
    public double PixelScale => FocalLengthMm > 0 && PixelSizeUm > 0
        ? 206.265 * PixelSizeUm * Binning / FocalLengthMm
        : 1.0;
}
