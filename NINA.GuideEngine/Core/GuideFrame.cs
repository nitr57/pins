// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>
/// A 16-bit guide camera frame, mirroring PHD2's usImage. Pixel data is always sized to the full
/// sensor (<see cref="Width"/> x <see cref="Height"/>); when <see cref="Subframe"/> is not empty only
/// that region contains valid data.
/// </summary>
public sealed class GuideFrame
{
    public GuideFrame(int width, int height, ushort[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        pixels ??= new ushort[width * height];
        if (pixels.Length != width * height) throw new ArgumentException("pixel buffer size mismatch", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major pixel data, length Width*Height.</summary>
    public ushort[] Pixels { get; }

    /// <summary>Region containing valid data; empty means the full frame is valid.</summary>
    public IntRect Subframe { get; set; } = IntRect.Empty;

    /// <summary>Offset added by dark subtraction; saturation checks subtract it.</summary>
    public ushort Pedestal { get; set; }

    /// <summary>Bits per pixel delivered by the camera (8..16).</summary>
    public int BitsPerPixel { get; set; } = 16;

    /// <summary>Hardware binning the frame was taken at.</summary>
    public int Binning { get; set; } = 1;

    public double ExposureMs { get; set; }

    public DateTimeOffset StartTime { get; set; }

    public long FrameNumber { get; set; }

    public IntRect ValidRect => Subframe.IsEmpty ? new IntRect(0, 0, Width, Height) : Subframe;

    public ushort this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public GuideFrame Clone()
    {
        return new GuideFrame(Width, Height, (ushort[])Pixels.Clone())
        {
            Subframe = Subframe,
            Pedestal = Pedestal,
            BitsPerPixel = BitsPerPixel,
            Binning = Binning,
            ExposureMs = ExposureMs,
            StartTime = StartTime,
            FrameNumber = FrameNumber,
        };
    }
}
