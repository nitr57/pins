// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Copyright (c) 2012 Bret McKee
// Ported from PHD2 src/star.cpp (Star::Find, hfr) (a6c02722)

using System.Runtime.InteropServices;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Stars;

/// <summary>Exact port of PHD2 <c>Star::Find</c>.</summary>
internal static class StarFinder
{
    /// <summary>Inner radius of the background annulus / centroid aperture radius.</summary>
    public const int A = 7;

    /// <summary>Outer radius of the background annulus.</summary>
    public const int B = 12;

    public const double LowSnr = 3.0;

    /// <summary>Nominal gain (electrons per ADU) used by the SNR estimate.</summary>
    public const double Gain = 0.5;

    // Upper bound for the number of aperture pixels (integer points with r² ≤ 49 is 149).
    private const int MaxAperturePixels = 160;

    [StructLayout(LayoutKind.Sequential)]
    private struct R2M
    {
        public double R2;
        public int X;
        public int Y;
        public double M;
    }

    public static unsafe bool Find(Star star, GuideFrame img, int searchRegion, int baseX, int baseY, StarFindMode mode, double minHfd,
        double maxHfd, ushort maxAdu)
    {
        var result = StarFindResult.Ok;
        double newX = baseX;
        double newY = baseY;

        // Values assigned to the star (PHD2 assigns the members directly as it goes).
        double mass = star.Mass, snr = star.Snr, hfd = star.Hfd;
        ushort peakVal = star.PeakValue;
        Span<R2M> hfrvec = stackalloc R2M[MaxAperturePixels];

        do
        {
            int minx, miny, maxx, maxy;
            if (img.Subframe.IsEmpty)
            {
                minx = miny = 0;
                maxx = img.Width - 1;
                maxy = img.Height - 1;
            }
            else
            {
                minx = img.Subframe.Left;
                maxx = img.Subframe.Right;
                miny = img.Subframe.Top;
                maxy = img.Subframe.Bottom;
            }

            // search region bounds
            int startX = Math.Max(baseX - searchRegion, minx);
            int endX = Math.Min(baseX + searchRegion, maxx);
            int startY = Math.Max(baseY - searchRegion, miny);
            int endY = Math.Min(baseY + searchRegion, maxy);

            if (endX <= startX || endY <= startY)
            {
                // "coordinates are invalid"
                result = StarFindResult.Error;
                break;
            }

            int rowsize = img.Width;
            int peakX = 0, peakY = 0;
            uint peakValue = 0;
            ushort max0 = 0, max1 = 0, max2 = 0;

            fixed (ushort* imgdata = img.Pixels)
            {
                if (mode == StarFindMode.Peak)
                {
                    for (int y = startY; y <= endY; y++)
                    {
                        ushort* row = imgdata + y * rowsize;
                        for (int x = startX; x <= endX; x++)
                        {
                            ushort val = row[x];
                            if (val > peakValue)
                            {
                                peakValue = val;
                                peakX = x;
                                peakY = y;
                            }
                        }
                    }

                    peakVal = (ushort)peakValue;
                }
                else
                {
                    // find the peak value within the search region using a smoothing function
                    // also check for saturation
                    for (int y = startY + 1; y <= endY - 1; y++)
                    {
                        ushort* rm = imgdata + (y - 1) * rowsize;
                        ushort* r0 = rm + rowsize;
                        ushort* rp = r0 + rowsize;
                        for (int x = startX + 1; x <= endX - 1; x++)
                        {
                            ushort p = r0[x];
                            uint val = 4 * (uint)p + rm[x - 1] + rm[x + 1] + rp[x - 1] + rp[x + 1] + 2 * (uint)rm[x] +
                                2 * (uint)r0[x - 1] + 2 * (uint)r0[x + 1] + 2 * (uint)rp[x];

                            if (val > peakValue)
                            {
                                peakValue = val;
                                peakX = x;
                                peakY = y;
                            }

                            if (p > max0) (p, max0) = (max0, p);
                            if (p > max1) (p, max1) = (max1, p);
                            if (p > max2) (p, max2) = (max2, p);
                        }
                    }

                    peakVal = max0; // raw peak val
                    peakValue /= 16; // smoothed peak value
                }

                // measure noise in the annulus with inner radius A and outer radius B
                const int a2 = A * A;
                const int b2 = B * B;

                // center window around peak value
                startX = Math.Max(peakX - B, minx);
                endX = Math.Min(peakX + B, maxx);
                startY = Math.Max(peakY - B, miny);
                endY = Math.Min(peakY + B, maxy);

                // find the mean and stdev of the background
                uint nbg = 0;
                double meanBg = 0.0, prevMeanBg;
                double sigma2Bg = 0.0;
                double sigmaBg = 0.0;

                for (int iter = 0; iter < 9; iter++)
                {
                    double sum = 0.0;
                    double a = 0.0;
                    double q = 0.0;
                    nbg = 0;

                    ushort* row = imgdata + rowsize * startY;
                    for (int y = startY; y <= endY; y++, row += rowsize)
                    {
                        int dy = y - peakY;
                        int dy2 = dy * dy;
                        for (int x = startX; x <= endX; x++)
                        {
                            int dx = x - peakX;
                            int r2 = dx * dx + dy2;

                            // exclude points not in annulus
                            if (r2 <= a2 || r2 > b2)
                                continue;

                            double val = row[x];

                            if (iter > 0 && (val < meanBg - 2.0 * sigmaBg || val > meanBg + 2.0 * sigmaBg))
                                continue;

                            sum += val;
                            ++nbg;
                            double k = nbg;
                            double a0 = a;
                            a += (val - a) / k;
                            q += (val - a0) * (val - a);
                        }
                    }

                    if (nbg < 10) // only possible after the first iteration
                        break;

                    prevMeanBg = meanBg;
                    meanBg = sum / nbg;
                    sigma2Bg = q / (nbg - 1);
                    sigmaBg = Math.Sqrt(sigma2Bg);

                    if (iter > 0 && Math.Abs(meanBg - prevMeanBg) < 0.5)
                        break;
                }

                ushort thresh;
                double cx = 0.0;
                double cy = 0.0;
                double m = 0.0;
                uint n;

                int hfrCount = 0;

                if (mode == StarFindMode.Peak)
                {
                    m = peakValue;
                    n = 1;
                    thresh = 0;
                }
                else
                {
                    thresh = ToUShort(meanBg + 3.0 * sigmaBg + 0.5);

                    // find pixels over threshold within aperture; compute mass and centroid
                    startX = Math.Max(peakX - A, minx);
                    endX = Math.Min(peakX + A, maxx);
                    startY = Math.Max(peakY - A, miny);
                    endY = Math.Min(peakY + A, maxy);

                    n = 0;

                    ushort* row = imgdata + rowsize * startY;
                    for (int y = startY; y <= endY; y++, row += rowsize)
                    {
                        int dy = y - peakY;
                        int dy2 = dy * dy;
                        if (dy2 > a2)
                            continue;

                        for (int x = startX; x <= endX; x++)
                        {
                            int dx = x - peakX;

                            // exclude points outside aperture
                            if (dx * dx + dy2 > a2)
                                continue;

                            // exclude points below threshold
                            ushort val = row[x];
                            if (val < thresh)
                                continue;

                            double d = val - meanBg;

                            cx += dx * d;
                            cy += dy * d;
                            m += d;
                            ++n;

                            ref var e = ref hfrvec[hfrCount++];
                            e.X = x;
                            e.Y = y;
                            e.M = d;
                        }
                    }
                }

                mass = m;

                // SNR estimate from: Measuring the Signal-to-Noise Ratio S/N of the CCD Image of a Star or Nebula,
                // J.H.Simonetti, 2004 January 8
                snr = n > 0 ? m / Math.Sqrt(m / Gain + sigma2Bg * n * (1.0 + 1.0 / nbg)) : 0.0;

                // a few scattered pixels over threshold can give a false positive
                // avoid this by requiring the smoothed peak value to be above the threshold
                if (peakValue <= thresh && snr >= LowSnr)
                    snr = LowSnr - 0.1;

                if (m < 10.0)
                {
                    hfd = 0.0;
                    result = StarFindResult.LowMass;
                    break;
                }

                if (snr < LowSnr)
                {
                    hfd = 0.0;
                    result = StarFindResult.LowSnr;
                    break;
                }

                newX = peakX + cx / m;
                newY = peakY + cy / m;

                hfd = 2.0 * Hfr(hfrvec[..hfrCount], newX, newY, m);

                // Check for constraints on HFD value
                if (mode != StarFindMode.Peak)
                {
                    if (hfd < minHfd)
                    {
                        result = StarFindResult.LowHfd;
                        break;
                    }

                    if (hfd > maxHfd)
                    {
                        result = StarFindResult.HighHfd;
                        break;
                    }
                }

                // check for saturation
                uint mx = max0;

                // remove pedestal
                if (mx >= img.Pedestal)
                    mx -= img.Pedestal;
                else
                    mx = 0; // unlikely

                if (maxAdu > 0)
                {
                    // maxADU is known
                    if (mx >= maxAdu)
                        result = StarFindResult.Saturated;
                    break;
                }

                // maxADU not known, use the "flat-top" heuristic
                //
                // even at saturation, the max values may vary a bit due to noise
                // Call it saturated if the the top three values are within 32 parts per 65535 of max for 16-bit
                // cameras, or within 1 part per 191 for 8-bit cameras
                uint dd = (uint)(max0 - max2);

                if (img.BitsPerPixel < 12)
                {
                    if (dd * 191U < 1U * mx)
                        result = StarFindResult.Saturated;
                }
                else
                {
                    if (dd * 65535U < 32U * mx)
                        result = StarFindResult.Saturated;
                }
            }
        }
        while (false);

        // update state
        star.Position = new GuidePoint(newX, newY);
        star.LastFindResult = result;
        star.PeakValue = peakVal;

        if (result == StarFindResult.Error)
        {
            mass = 0.0;
            snr = 0.0;
            hfd = 0.0;
        }

        star.Mass = mass;
        star.Snr = snr;
        star.Hfd = hfd;

        return Star.WasFound(result);
    }

    // C++ (unsigned short)(double) for the value range that occurs in practice.
    // Deviation from PHD2: out-of-range values (undefined behaviour in C++) are clamped.
    private static ushort ToUShort(double v) => v >= 65535.0 ? ushort.MaxValue : v <= 0 ? (ushort)0 : (ushort)(uint)v;

    private static double Hfr(Span<R2M> vec, double cx, double cy, double mass)
    {
        if (vec.Length == 1) // hot pixel?
            return 0.25;

        // compute Half Flux Radius (HFR)
        for (int i = 0; i < vec.Length; i++)
        {
            double dx = vec[i].X - cx;
            double dy = vec[i].Y - cy;
            vec[i].R2 = dx * dx + dy * dy;
        }

        // sort by ascending radius^2
        // Deviation from PHD2: std::sort is not stable; entries with exactly equal r² (only possible for
        // exactly symmetric centroids) may be ordered differently. We use a stable insertion sort.
        for (int i = 1; i < vec.Length; i++)
        {
            var v = vec[i];
            int j = i - 1;
            while (j >= 0 && vec[j].R2 > v.R2)
            {
                vec[j + 1] = vec[j];
                j--;
            }

            vec[j + 1] = v;
        }

        // find radius of half-mass
        double r20, r21, m0, m1;
        r20 = r21 = m0 = m1 = 0.0;
        double halfm = 0.5 * mass;
        for (int i = 0; i < vec.Length; i++)
        {
            r20 = r21;
            m0 = m1;
            r21 = vec[i].R2;
            m1 += vec[i].M;
            if (m1 > halfm)
                break;
        }

        // interpolate
        double hfr;
        if (m1 > m0)
        {
            double r0 = Math.Sqrt(r20), r1 = Math.Sqrt(r21);
            double s = (r1 - r0) / (m1 - m0);
            hfr = r0 + s * (halfm - m0);
        }
        else
        {
            hfr = 0.25;
        }

        return hfr;
    }
}
