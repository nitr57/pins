// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Ported from PHD2 src/image_math.cpp, src/image_math.h, src/usImage.cpp, src/camera.cpp (a6c02722)

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Imaging;

/// <summary>
/// Pixel-level image helpers ported from PHD2's image_math.cpp: small fixed-size medians, the 3x3
/// median filter (<c>Median3</c>), the sliding 2x2 mean (<c>QuickLRecon</c>), the neighbour median
/// used for defect correction, software binning, selection helpers and a histogram median filter.
/// </summary>
public static class ImageMath
{
    // Frames with at least this many pixels are filtered on multiple threads. The result is
    // identical to the sequential computation (each output row depends only on the source).
    private const int ParallelThresholdPixels = 256 * 1024;

    #region small medians (exact ports, including PHD2's "average of the two middle values" for even counts)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(ref ushort a, ref ushort b)
    {
        ushort t = a;
        a = b;
        b = t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Median9(ushort a0, ushort a1, ushort a2, ushort a3, ushort a4, ushort a5, ushort a6, ushort a7, ushort a8)
    {
        ushort l0 = a0, l1 = a1, l2 = a2, l3 = a3, l4 = a4;
        ushort x = a5;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);
        x = a6;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);
        x = a7;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);
        x = a8;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);

        if (l1 > l0) l0 = l1;
        if (l2 > l0) l0 = l2;
        if (l3 > l0) l0 = l3;
        if (l4 > l0) l0 = l4;
        return l0;
    }

    internal static ushort Median8(ReadOnlySpan<ushort> l)
    {
        ushort l0 = l[0], l1 = l[1], l2 = l[2], l3 = l[3], l4 = l[4];
        ushort x = l[5];
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);
        x = l[6];
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);
        x = l[7];
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        if (x < l4) Swap(ref x, ref l4);

        if (l2 > l0) Swap(ref l2, ref l0);
        if (l2 > l1) Swap(ref l2, ref l1);
        if (l3 > l0) Swap(ref l3, ref l0);
        if (l3 > l1) Swap(ref l3, ref l1);
        if (l4 > l0) Swap(ref l4, ref l0);
        if (l4 > l1) Swap(ref l4, ref l1);

        return (ushort)(((uint)l0 + l1) / 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Median6(ushort a0, ushort a1, ushort a2, ushort a3, ushort a4, ushort a5)
    {
        ushort l0 = a0, l1 = a1, l2 = a2, l3 = a3;
        ushort x = a4;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);
        x = a5;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        if (x < l3) Swap(ref x, ref l3);

        if (l2 > l0) Swap(ref l2, ref l0);
        if (l2 > l1) Swap(ref l2, ref l1);
        if (l3 > l0) Swap(ref l3, ref l0);
        if (l3 > l1) Swap(ref l3, ref l1);

        return (ushort)(((uint)l0 + l1) / 2);
    }

    internal static ushort Median5(ushort a0, ushort a1, ushort a2, ushort a3, ushort a4)
    {
        ushort l0 = a0, l1 = a1, l2 = a2;
        ushort x = a3;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);
        x = a4;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);

        if (l1 > l0) l0 = l1;
        if (l2 > l0) l0 = l2;
        return l0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Median4(ushort a0, ushort a1, ushort a2, ushort a3)
    {
        ushort l0 = a0, l1 = a1, l2 = a2;
        ushort x = a3;
        if (x < l0) Swap(ref x, ref l0);
        if (x < l1) Swap(ref x, ref l1);
        if (x < l2) Swap(ref x, ref l2);

        if (l2 > l0) Swap(ref l2, ref l0);
        if (l2 > l1) Swap(ref l2, ref l1);

        return (ushort)(((uint)l0 + l1) / 2);
    }

    internal static ushort Median3Values(ushort a0, ushort a1, ushort a2)
    {
        ushort l0 = a0, l1 = a1, l2 = a2;
        if (l2 < l0) Swap(ref l2, ref l0);
        if (l2 < l1) Swap(ref l2, ref l1);
        if (l1 > l0) l0 = l1;
        return l0;
    }

    #endregion

    #region Median3 (3x3 median filter)

    /// <summary>
    /// 3x3 median filter of <paramref name="rect"/> from <paramref name="src"/> into <paramref name="dst"/>
    /// (both row-major with row length <paramref name="width"/>). Border pixels use the median of the
    /// available neighbours (4 or 6 values), as in PHD2. Pixels outside <paramref name="rect"/> are not written.
    /// </summary>
    public static void Median3(ushort[] dst, ushort[] src, int width, IntRect rect)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        // Deviation from PHD2: PHD2 reads out of bounds for regions narrower than 2 px; we copy instead.
        if (rect.Width < 2 || rect.Height < 2)
        {
            for (int y = rect.Top; y <= rect.Bottom; y++)
                Array.Copy(src, y * width + rect.X, dst, y * width + rect.X, rect.Width);
            return;
        }

        int rh = rect.Height;
        if ((long)rect.Width * rect.Height >= ParallelThresholdPixels)
            Parallel.For(0, rh, y => Median3Row(dst, src, width, rect, y));
        else
            for (int y = 0; y < rh; y++)
                Median3Row(dst, src, width, rect, y);
    }

    private static unsafe void Median3Row(ushort[] dst, ushort[] src, int width, IntRect rect, int y)
    {
        int rx = rect.X, ry = rect.Y, rw = rect.Width, rh = rect.Height;
        fixed (ushort* s0 = src, d0 = dst)
        {
            ushort* d = d0 + (ry + y) * width + rx;
            ushort* c = s0 + (ry + y) * width + rx; // current row
            if (y == 0 || y == rh - 1)
            {
                // top row pairs with the row below, bottom row with the row above
                ushort* o = y == 0 ? c + width : c - width;
                // PHD2 argument order: top row = (row0, row1); bottom row = (row RH-2, row RH-1)
                ushort* r0 = y == 0 ? c : o;
                ushort* r1 = y == 0 ? o : c;
                *d++ = Median4(r0[0], r0[1], r1[0], r1[1]);
                for (int x = 1; x <= rw - 2; x++)
                    *d++ = Median6(r0[x - 1], r0[x], r0[x + 1], r1[x - 1], r1[x], r1[x + 1]);
                *d = Median4(r0[rw - 2], r0[rw - 1], r1[rw - 2], r1[rw - 1]);
                return;
            }

            ushort* u = c - width;
            ushort* b = c + width;
            *d++ = Median6(u[0], u[1], c[0], c[1], b[0], b[1]);
            for (int x = 1; x <= rw - 2; x++)
                *d++ = Median9(u[x - 1], u[x], u[x + 1], c[x - 1], c[x], c[x + 1], b[x - 1], b[x], b[x + 1]);
            *d = Median6(u[rw - 2], u[rw - 1], c[rw - 2], c[rw - 1], b[rw - 2], b[rw - 1]);
        }
    }

    /// <summary>
    /// In-place 3x3 median filter (PHD2 <c>Median3(usImage&amp;)</c>). With a subframe only the subframe is
    /// filtered and, as in PHD2, all pixels outside it are cleared to 0.
    /// </summary>
    /// <param name="img">Frame to filter.</param>
    /// <param name="scratch">Optional reusable buffer of at least Width*Height elements.</param>
    public static void Median3(GuideFrame img, ushort[]? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        var tmp = EnsureScratch(scratch, img.Pixels.Length);
        if (img.Subframe.IsEmpty)
        {
            Median3(tmp, img.Pixels, img.Width, new IntRect(0, 0, img.Width, img.Height));
        }
        else
        {
            Array.Clear(tmp, 0, img.Pixels.Length);
            Median3(tmp, img.Pixels, img.Width, img.Subframe);
        }

        Array.Copy(tmp, img.Pixels, img.Pixels.Length);
    }

    #endregion

    #region QuickLRecon (2x2 mean)

    /// <summary>
    /// In-place sliding 2x2 mean ("2x2 mean" noise reduction, PHD2 <c>QuickLRecon</c>). With a subframe only
    /// the subframe is processed and pixels outside it are cleared to 0, as in PHD2.
    /// </summary>
    public static unsafe void QuickLRecon(GuideFrame img, ushort[]? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        int w = img.Width;
        int rx, ry, rw, rh;
        var tmp = EnsureScratch(scratch, img.Pixels.Length);
        if (img.Subframe.IsEmpty)
        {
            rx = ry = 0;
            rw = img.Width;
            rh = img.Height;
        }
        else
        {
            rx = img.Subframe.X;
            ry = img.Subframe.Y;
            rw = img.Subframe.Width;
            rh = img.Subframe.Height;
            Array.Clear(tmp, 0, img.Pixels.Length);
        }

        if (rw < 1 || rh < 1)
            return;

        fixed (ushort* src = img.Pixels, dst = tmp)
        {
            uint t;
            ushort* d;
            for (int y = 0; y <= rh - 2; y++)
            {
                d = dst + (ry + y) * w + rx;
                ushort* r0 = src + (ry + y) * w + rx;
                ushort* r1 = r0 + w;
                for (int x = 0; x <= rw - 2; x++)
                {
                    t = r0[x];
                    t += r0[x + 1];
                    t += r1[x];
                    t += r1[x + 1];
                    *d++ = (ushort)(t >> 2);
                }

                // last col
                t = r0[rw - 1];
                t += r1[rw - 1];
                *d = (ushort)(t >> 1);
            }

            // last row
            d = dst + (ry + rh - 1) * w + rx;
            ushort* lr = src + (ry + rh - 1) * w + rx;
            for (int x = 0; x <= rw - 2; x++)
            {
                t = lr[x];
                t += lr[x + 1];
                *d++ = (ushort)(t >> 1);
            }

            // bottom-right pixel
            *d = lr[rw - 1];
        }

        Array.Copy(tmp, img.Pixels, img.Pixels.Length);
    }

    #endregion

    #region defect helpers

    /// <summary>Median of the (up to 8) pixels bordering (x, y) — PHD2 <c>MedianBorderingPixels</c>.</summary>
    public static ushort MedianBorderingPixels(GuideFrame img, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(img);
        return MedianBorderingPixels(img.Pixels, img.Width, img.Height, x, y);
    }

    internal static ushort MedianBorderingPixels(ushort[] p, int xsize, int ysize, int x, int y)
    {
        if (x > 0 && y > 0 && x < xsize - 1 && y < ysize - 1)
        {
            Span<ushort> a = stackalloc ushort[8];
            a[0] = p[(x - 1) + (y - 1) * xsize];
            a[1] = p[x + (y - 1) * xsize];
            a[2] = p[(x + 1) + (y - 1) * xsize];
            a[3] = p[(x - 1) + y * xsize];
            a[4] = p[(x + 1) + y * xsize];
            a[5] = p[(x - 1) + (y + 1) * xsize];
            a[6] = p[x + (y + 1) * xsize];
            a[7] = p[(x + 1) + (y + 1) * xsize];
            return Median8(a);
        }

        if (x == 0 && y > 0 && y < ysize - 1)
        {
            // left edge
            return Median5(p[x + (y - 1) * xsize], p[x + (y + 1) * xsize], p[(x + 1) + (y - 1) * xsize], p[(x + 1) + y * xsize],
                p[(x + 1) + (y + 1) * xsize]);
        }

        if (x == xsize - 1 && y > 0 && y < ysize - 1)
        {
            // right edge
            return Median5(p[x + (y - 1) * xsize], p[x + (y + 1) * xsize], p[(x - 1) + (y - 1) * xsize], p[(x - 1) + y * xsize],
                p[(x - 1) + (y + 1) * xsize]);
        }

        if (y == 0 && x > 0 && x < xsize - 1)
        {
            return Median5(p[(x - 1) + y * xsize], p[(x - 1) + (y + 1) * xsize], p[x + (y + 1) * xsize], p[(x + 1) + y * xsize],
                p[(x + 1) + (y + 1) * xsize]);
        }

        if (y == ysize - 1 && x > 0 && x < xsize - 1)
        {
            return Median5(p[(x - 1) + y * xsize], p[(x - 1) + (y - 1) * xsize], p[x + (y - 1) * xsize], p[(x + 1) + y * xsize],
                p[(x + 1) + (y - 1) * xsize]);
        }

        if (x == 0 && y == 0)
            return Median3Values(p[(x + 1) + y * xsize], p[x + (y + 1) * xsize], p[(x + 1) + (y + 1) * xsize]);
        if (x == 0 && y == ysize - 1)
            return Median3Values(p[(x + 1) + y * xsize], p[x + (y - 1) * xsize], p[(x + 1) + (y - 1) * xsize]);
        if (x == xsize - 1 && y == ysize - 1)
            return Median3Values(p[(x - 1) + y * xsize], p[x + (y - 1) * xsize], p[(x - 1) + (y - 1) * xsize]);
        if (x == xsize - 1 && y == 0)
            return Median3Values(p[(x - 1) + y * xsize], p[x + (y + 1) * xsize], p[(x - 1) + (y + 1) * xsize]);

        return 0; // unreachable for valid coordinates (1-pixel wide images)
    }

    #endregion

    #region binning

    /// <summary>
    /// Software binning (PHD2 <c>BinPixels</c>): each output pixel is the integer average of a
    /// binning x binning block. Supported factors 2, 3, 4. The destination is (w/b) x (h/b).
    /// </summary>
    public static void BinPixels(ushort[] dst, ushort[] src, int srcWidth, int srcHeight, int binning)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        if (binning is < 2 or > 4) throw new ArgumentOutOfRangeException(nameof(binning));

        // Deviation from PHD2: PHD2 iterates the full (even) source size for 2x/4x and would read past
        // the last row/column for sizes not divisible by the factor; we only emit whole blocks, which is
        // identical for divisible sizes and matches the destination size used by GuideCamera::Capture.
        int dw = srcWidth / binning, dh = srcHeight / binning;
        int n = binning * binning;
        int k = 0;
        for (int yy = 0; yy < dh; yy++)
        {
            for (int xx = 0; xx < dw; xx++)
            {
                uint sum = 0;
                int sy = yy * binning, sx = xx * binning;
                for (int j = 0; j < binning; j++)
                {
                    int row = (sy + j) * srcWidth + sx;
                    for (int i = 0; i < binning; i++)
                        sum += src[row + i];
                }

                dst[k++] = (ushort)(sum / (uint)n);
            }
        }
    }

    /// <summary>
    /// Returns a software-binned copy of <paramref name="src"/> (as PHD2 GuideCamera::Capture does after
    /// dark subtraction): size, subframe and <see cref="GuideFrame.Binning"/> are scaled.
    /// </summary>
    public static GuideFrame SoftwareBin(GuideFrame src, int binning)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (binning == 1) return src.Clone();
        int dw = src.Width / binning, dh = src.Height / binning;
        var dst = new GuideFrame(dw, dh)
        {
            Pedestal = src.Pedestal,
            BitsPerPixel = src.BitsPerPixel,
            Binning = src.Binning * binning,
            ExposureMs = src.ExposureMs,
            StartTime = src.StartTime,
            FrameNumber = src.FrameNumber,
        };
        BinPixels(dst.Pixels, src.Pixels, src.Width, src.Height, binning);
        if (!src.Subframe.IsEmpty)
        {
            // binned_rect from camera.cpp
            dst.Subframe = new IntRect(src.Subframe.X / binning, src.Subframe.Y / binning, src.Subframe.Width / binning,
                src.Subframe.Height / binning);
        }

        return dst;
    }

    #endregion

    #region selection / statistics

    /// <summary>
    /// Rearranges <paramref name="values"/> so that the element at <paramref name="k"/> is the one that would
    /// be there after sorting (std::nth_element semantics) and returns it.
    /// </summary>
    public static T NthElement<T>(Span<T> values, int k)
        where T : IComparable<T>
    {
        if ((uint)k >= (uint)values.Length) throw new ArgumentOutOfRangeException(nameof(k));
        int lo = 0, hi = values.Length - 1;
        while (hi > lo)
        {
            // median of three pivot
            int mid = lo + ((hi - lo) >> 1);
            if (values[mid].CompareTo(values[lo]) < 0) (values[mid], values[lo]) = (values[lo], values[mid]);
            if (values[hi].CompareTo(values[lo]) < 0) (values[hi], values[lo]) = (values[lo], values[hi]);
            if (values[hi].CompareTo(values[mid]) < 0) (values[hi], values[mid]) = (values[mid], values[hi]);
            T pivot = values[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (values[i].CompareTo(pivot) < 0) i++;
                while (pivot.CompareTo(values[j]) < 0) j--;
                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else break;
        }

        return values[k];
    }

    /// <summary>Median as PHD2 computes it: the element at index n/2 of the sorted values (upper median).</summary>
    public static T Median<T>(Span<T> values)
        where T : IComparable<T>
        => NthElement(values, values.Length / 2);

    /// <summary>Median ADU of <paramref name="roi"/> (PHD2 <c>median_value_in_roi</c>).</summary>
    public static ushort MedianValueInRoi(ushort[] data, int width, IntRect roi)
    {
        ArgumentNullException.ThrowIfNull(data);
        return HistogramStats(data, width, roi).Median;
    }

    /// <summary>
    /// Min, max and median (element n/2 of the sorted values) of a region using a histogram, like
    /// PHD2's <c>HistogramBuilder</c> in usImage::CalcStats.
    /// </summary>
    public static FrameStats HistogramStats(ushort[] data, int width, IntRect roi)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (roi.IsEmpty) return default;
        int[] histo = System.Buffers.ArrayPool<int>.Shared.Rent(65536);
        try
        {
            Array.Clear(histo, 0, 65536);
            ushort min = ushort.MaxValue, max = 0;
            for (int y = roi.Top; y <= roi.Bottom; y++)
            {
                int row = y * width;
                for (int x = roi.Left; x <= roi.Right; x++)
                {
                    ushort v = data[row + x];
                    histo[v]++;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            int count = roi.Width * roi.Height;
            int left = count / 2;
            ushort median = max;
            for (int i = min; i < max; i++)
            {
                if (histo[i] > left)
                {
                    median = (ushort)i;
                    break;
                }

                left -= histo[i];
            }

            return new FrameStats(min, max, median);
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(histo);
        }
    }

    /// <summary>Min/max/median of the valid region of a frame.</summary>
    public static FrameStats ComputeStats(GuideFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return HistogramStats(frame.Pixels, frame.Width, frame.ValidRect);
    }

    /// <summary>Mean, standard deviation, median and MAD of a region (PHD2 image_math.cpp GetImageStats).</summary>
    public static ImageStats GetImageStats(GuideFrame img, IntRect win)
    {
        ArgumentNullException.ThrowIfNull(img);
        int n = win.Width * win.Height;
        var tmp = new ushort[n];
        double a = 0.0, q = 0.0, k = 1.0, km1 = 0.0;
        int t = 0;
        for (int y = win.Top; y <= win.Bottom; y++)
        {
            int row = y * img.Width;
            for (int x = win.Left; x <= win.Right; x++)
            {
                ushort p = img.Pixels[row + x];
                tmp[t++] = p;
                double xv = p;
                double a0 = a;
                a += (xv - a) / k;
                q += (xv - a0) * (xv - a);
                km1 = k;
                k += 1.0;
            }
        }

        double mean = a;
        double stdev = Math.Sqrt(q / km1);
        ushort median = NthElement<ushort>(tmp, n / 2);
        for (int i = 0; i < n; i++)
            tmp[i] = (ushort)Math.Abs(tmp[i] - median);
        ushort mad = NthElement<ushort>(tmp, n / 2);
        return new ImageStats(mean, stdev, median, mad);
    }

    #endregion

    #region median filter (defect map)

    /// <summary>
    /// Square median filter of half-width <paramref name="halfWidth"/> (window 2*halfWidth+1) using a two
    /// level histogram, exact port of PHD2's MedianFilter (image_math.cpp).
    /// </summary>
    public static GuideFrame MedianFilter(GuideFrame src, int halfWidth)
    {
        ArgumentNullException.ThrowIfNull(src);
        int width = src.Width, height = src.Height;
        var dst = new GuideFrame(width, height);
        var s = src.Pixels;
        var d = dst.Pixels;

        Parallel.For(0, height, () => (new ushort[256], new ushort[65536]), (y, _, state) =>
        {
            var (histo1, histo2) = state;
            int di = y * width;
            int top = Math.Max(0, y - halfWidth);
            int bot = Math.Min(y + halfWidth, height - 1);
            int left = 0;
            int right = halfWidth;

            Array.Clear(histo1);
            Array.Clear(histo2);

            for (int j = top; j <= bot; j++)
            {
                int p = j * width + left;
                for (int i = left; i <= right; i++, p++)
                {
                    ++histo1[s[p] >> 8];
                    ++histo2[s[p]];
                }
            }

            uint n = (uint)((right - left + 1) * (bot - top + 1));
            d[di++] = HistoMedian(histo1, histo2, (int)n);

            for (int i = 1; i < width; i++)
            {
                left = Math.Max(0, i - halfWidth);
                right = Math.Min(i + halfWidth, width - 1);

                if (left > 0)
                {
                    int p = top * width + left - 1;
                    for (int j = top; j <= bot; j++, p += width)
                    {
                        --histo1[s[p] >> 8];
                        --histo2[s[p]];
                    }

                    n -= (uint)(bot - top + 1);
                }

                if (i + halfWidth <= width - 1)
                {
                    int p = top * width + right;
                    for (int j = top; j <= bot; j++, p += width)
                    {
                        ++histo1[s[p] >> 8];
                        ++histo2[s[p]];
                    }

                    n += (uint)(bot - top + 1);
                }

                d[di++] = HistoMedian(histo1, histo2, (int)n);
            }

            return state;
        }, _ => { });

        return dst;
    }

    private static ushort HistoMedian(ushort[] histo1, ushort[] histo2, int n)
    {
        n /= 2;
        uint i;
        for (i = 0; i < 256; i++)
        {
            if (histo1[i] > n) break;
            n -= histo1[i];
        }

        for (i <<= 8; i < 65536; i++)
        {
            if (histo2[i] > n) break;
            n -= histo2[i];
        }

        return (ushort)i;
    }

    #endregion

    internal static ushort[] EnsureScratch(ushort[]? scratch, int length)
        => scratch is not null && scratch.Length >= length ? scratch : new ushort[length];
}

/// <summary>Min / max / median ADU of a frame region.</summary>
public readonly record struct FrameStats(ushort Min, ushort Max, ushort Median);

/// <summary>Image statistics used by the defect map builder (PHD2 ImageStats).</summary>
public readonly record struct ImageStats(double Mean, double Stdev, ushort Median, ushort Mad);
