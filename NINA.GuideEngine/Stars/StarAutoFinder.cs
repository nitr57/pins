// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Copyright (c) 2012 Bret McKee
// Ported from PHD2 src/star.cpp (GuideStar::AutoFind, psf_conv, Downsample, GetStats) (a6c02722)

using System.Threading.Tasks;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;

namespace NINA.GuideEngine.Stars;

/// <summary>Why an AutoFind candidate was not chosen as primary (diagnostics / UI).</summary>
[Flags]
public enum AutoFindPenalty
{
    None = 0,

    /// <summary>HFD in the lowest quartile and well below the median: hot-pixel like.</summary>
    LowHfd = 1,

    /// <summary>Another non-negligible peak inside the search box.</summary>
    Crowded = 2,

    /// <summary>Within one search region of the AutoFind edge margin.</summary>
    NearEdge = 4,
}

/// <summary>A local maximum that survived merging, crowding and edge rejection.</summary>
public sealed record AutoFindCandidate(int X, int Y, float Intensity, StarFindResult FindResult, double Snr, double Mass, double Hfd,
    ushort PeakValue, AutoFindPenalty Penalty);

/// <summary>Intermediate results of <see cref="GuideStar.AutoFind"/>, for diagnostics, tests and parity checks.</summary>
public sealed class AutoFindDiagnostics
{
    /// <summary>Downsample factor that was used.</summary>
    public int Downsample { get; internal set; }

    public double GlobalMean { get; internal set; }

    public double GlobalStdev { get; internal set; }

    /// <summary>Local maxima (image coordinates) after the top-100 cut, before merging; ascending intensity.</summary>
    public List<(int X, int Y, float Intensity)> LocalMaxima { get; } = new();

    /// <summary>Surviving candidates, brightest first.</summary>
    public List<AutoFindCandidate> Candidates { get; } = new();

    /// <summary>Saturation level including the pedestal.</summary>
    public uint SaturationLevel { get; internal set; }

    /// <summary>Near-saturation threshold (90 % of range above the pedestal).</summary>
    public ushort SaturationThreshold { get; internal set; }

    /// <summary>Pass (1..3) in which the primary was accepted, 0 when none.</summary>
    public int Pass { get; internal set; }

    /// <summary>Integer peak coordinates of the chosen primary (PHD2 sets the AutoFind star to these).</summary>
    public GuidePoint PrimaryPeak { get; internal set; } = GuidePoint.Invalid;

    /// <summary>The candidate PHD2 would have chosen (differs from <see cref="PrimaryPeak"/> only when scoring changed the choice).</summary>
    public GuidePoint Phd2PrimaryPeak { get; internal set; } = GuidePoint.Invalid;
}

/// <summary>Port of PHD2 GuideStar::AutoFind.</summary>
internal static class StarAutoFinder
{
    private const int ConvRadius = 4;
    private const int TopN = 100;
    private const double Threshold = 0.1;
    private const double DownsampleScaleThresh = 0.6;

    private readonly record struct Peak(int X, int Y, float Val);

    public static List<GuideStar> AutoFind(GuideFrame image, int extraEdgeAllowance, int searchRegion, IntRect roi, int maxStars,
        StarFinderOptions options, AutoFindDiagnostics? diag)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        var foundStars = new List<GuideStar>();

        if (!image.Subframe.IsEmpty)
            return foundStars; // AutoFind called on subframe, returning error

        // run a 3x3 median first to eliminate hot pixels
        var smoothed = new ushort[image.Pixels.Length];
        var region = new IntRect(0, 0, image.Width, image.Height);
        if (!roi.IsEmpty)
        {
            // set the subframe to the roi so that the Median3 operation blanks pixels outside the ROI
            region = roi.Intersect(region);
            if (region.Width < searchRegion || region.Height < searchRegion)
                return foundStars; // bad ROI
        }

        ImageMath.Median3(smoothed, image.Pixels, image.Width, region);

        // convert to floating point
        int w = image.Width, h = image.Height;
        var conv = new float[smoothed.Length];
        for (int i = 0; i < conv.Length; i++)
            conv[i] = smoothed[i];

        // downsample the source image
        int downsample = options.AutoFindDownsample;
        if (downsample == 0)
            downsample = options.PixelScale > DownsampleScaleThresh ? 1 : 2;
        if (downsample > 1)
        {
            conv = Downsample(conv, w, h, downsample, out w, out h);
        }

        if (diag is not null) diag.Downsample = downsample;

        // run the PSF convolution
        conv = PsfConv(conv, w, h);

        int dw = w, dh = h;
        var convRect = new IntRect(ConvRadius, ConvRadius, dw - 2 * ConvRadius, dh - 2 * ConvRadius);
        if (convRect.IsEmpty)
            return foundStars;

        GetStats(out double globalMean, out double globalStdev, conv, dw, convRect);
        if (diag is not null)
        {
            diag.GlobalMean = globalMean;
            diag.GlobalStdev = globalStdev;
        }

        // keep track of the brightest stars (std::set<Peak>: ordered by value, equal values are rejected)
        var stars = new List<Peak>(TopN + 2);

        // find each local maximum
        const int srch = 4;
        // A pixel is a local maximum when no pixel of its 9x9 window is greater, i.e. when it equals the
        // window maximum. The row-wise 9-pixel maxima are precomputed (in parallel); the candidate scan
        // below keeps PHD2's row-major order, which matters for the top-N set.
        var hmax = RowMax(conv, dw, convRect, srch);

        // Candidate detection is independent per pixel and runs in parallel per row; the insertion into
        // the top-N set below happens sequentially in PHD2's row-major order.
        int yFirst = convRect.Top + srch, yLast = convRect.Bottom - srch;
        var rowPeaks = new List<Peak>?[Math.Max(0, yLast - yFirst + 1)];
        double gStdev = globalStdev;
        int ds = downsample;
        Parallel.For(yFirst, yLast + 1, y =>
        {
            List<Peak>? list = null;
            for (int x = convRect.Left + srch; x <= convRect.Right - srch; x++)
            {
                float val = conv[dw * y + x];
                if (!(val > 0.0f) || !IsLocalMax(hmax, dw, x, y, val, srch))
                    continue;

                // compare local maximum to mean value of surrounding pixels
                const int local = 7;
                var localRect = new IntRect(x - local, y - local, 2 * local + 1, 2 * local + 1).Intersect(convRect);
                double localMean = GetMean(conv, dw, localRect);

                // this is our measure of star intensity
                double hh = (val - localMean) / gStdev;
                if (hh < Threshold)
                    continue;

                // coordinates on the original image
                int imgx = x * ds + ds / 2;
                int imgy = y * ds + ds / 2;
                (list ??= new List<Peak>()).Add(new Peak(imgx, imgy, (float)hh));
            }

            rowPeaks[y - yFirst] = list;
        });

        foreach (var list in rowPeaks)
        {
            if (list is null) continue;
            foreach (var p in list)
            {
                SetInsert(stars, p);
                if (stars.Count > TopN)
                    stars.RemoveAt(0);
            }
        }

        if (diag is not null)
        {
            foreach (var p in stars)
                diag.LocalMaxima.Add((p.X, p.Y, p.Val));
        }

        // merge stars that are very close into a single star
        {
            const int minlimitsq = 5 * 5;
            bool repeat;
            do
            {
                repeat = false;
                for (int a = 0; a < stars.Count && !repeat; a++)
                {
                    for (int b = a + 1; b < stars.Count; b++)
                    {
                        int dx = stars[a].X - stars[b].X;
                        int dy = stars[a].Y - stars[b].Y;
                        int d2 = dx * dx + dy * dy;
                        if (d2 < minlimitsq)
                        {
                            // very close, treat as single star; erase the dimmer one
                            stars.RemoveAt(a);
                            repeat = true;
                            break;
                        }
                    }
                }
            }
            while (repeat);
        }

        // Candidates after merging (used by the crowding penalty of the scoring extension).
        var merged = stars.ToArray();

        // exclude stars that would fit within a single searchRegion box
        {
            var toErase = new HashSet<int>();
            const int extra = 5; // extra safety margin
            int fullw = searchRegion + extra;
            for (int a = 0; a < stars.Count; a++)
            {
                for (int b = a + 1; b < stars.Count; b++)
                {
                    int dx = Math.Abs(stars[a].X - stars[b].X);
                    int dy = Math.Abs(stars[a].Y - stars[b].Y);
                    if (dx <= fullw && dy <= fullw)
                    {
                        // stars closer than search region, exclude them both
                        // but do not let a very dim star eliminate a very bright star
                        if (!(stars[b].Val / stars[a].Val >= 5.0))
                        {
                            toErase.Add(a);
                            toErase.Add(b);
                        }
                    }
                }
            }

            if (toErase.Count > 0)
            {
                var kept = new List<Peak>(stars.Count);
                for (int i = 0; i < stars.Count; i++)
                    if (!toErase.Contains(i))
                        kept.Add(stars[i]);
                stars = kept;
            }
        }

        // exclude stars too close to the edge
        int edgeDist = searchRegion + extraEdgeAllowance;
        stars.RemoveAll(p => p.X <= edgeDist || p.X >= image.Width - edgeDist || p.Y <= edgeDist || p.Y >= image.Height - edgeDist);

        // brightest first (the reverse iteration order used by all remaining PHD2 loops)
        stars.Reverse();

        // Star::Find is deterministic, so each candidate is measured once and the result reused by the
        // saturation probe, the multi-star list and the three selection passes (PHD2 repeats the Find).
        var finds = new GuideStar[stars.Count];
        var mode = StarFindMode.Centroid;
        ushort satAdu = options.SaturationAdu;
        for (int i = 0; i < stars.Count; i++)
        {
            var tmp = new GuideStar();
            tmp.Find(image, searchRegion, stars[i].X, stars[i].Y, mode, options.MinHfd, options.MaxHfd, satAdu);
            finds[i] = tmp;
        }

        // At first I tried running Star::Find on the survivors to find the best star. This had the
        // unfortunate effect of locating hot pixels which the psf convolution so nicely avoids. -ag

        uint satLevel; // saturation level, including pedestal
        if (satAdu > 0)
        {
            // Known saturation level
            satLevel = (uint)satAdu + image.Pedestal;
        }
        else
        {
            // try to identify the saturation point; first, find the peak pixel overall
            ushort maxVal = 0;
            foreach (var v in image.Pixels)
                if (v > maxVal)
                    maxVal = v;

            // next see if any of the stars has a flat-top
            bool foundSaturated = false;
            for (int i = 0; i < stars.Count; i++)
            {
                var tmp = finds[i];
                if (tmp.WasFound() && tmp.LastFindResult == StarFindResult.Saturated)
                {
                    if ((maxVal - tmp.PeakValue) * 255U > maxVal)
                    {
                        // false positive saturation, flat top but below maxVal
                    }
                    else
                    {
                        // a saturated star was found
                        foundSaturated = true;
                        break;
                    }
                }
            }

            satLevel = foundSaturated
                ? maxVal // includes pedestal
                : ((1U << image.BitsPerPixel) - 1) + image.Pedestal;
        }

        uint range = satLevel > image.Pedestal ? satLevel - image.Pedestal : 0U;
        // "near-saturation" threshold at 90% saturation
        uint t = image.Pedestal + 9 * range / 10;
        if (t > 65535) t = 65535;
        ushort satThresh = (ushort)t;

        // scoring extension (own code, KStars idea): penalties per candidate
        var penalties = new AutoFindPenalty[stars.Count];
        if (options.AutoFindScoring)
            ComputePenalties(stars, finds, merged, penalties, searchRegion, edgeDist, image.Width, image.Height);

        if (diag is not null)
        {
            diag.SaturationLevel = satLevel;
            diag.SaturationThreshold = satThresh;
            for (int i = 0; i < stars.Count; i++)
            {
                var f = finds[i];
                diag.Candidates.Add(new AutoFindCandidate(stars[i].X, stars[i].Y, stars[i].Val, f.LastFindResult, f.Snr, f.Mass, f.Hfd, f.PeakValue,
                    penalties[i]));
            }
        }

        // Before sifting for the best star, collect all the viable candidates
        double minSnr = options.MinSnr;
        if (maxStars > 1)
        {
            for (int i = 0; i < stars.Count; i++)
            {
                var tmp = finds[i];
                // We're repeating the find, so we're vulnerable to hot pixels and creation of unwanted duplicates
                if (tmp.WasFound() && tmp.Snr >= minSnr)
                {
                    bool duplicate = foundStars.Exists(other => other.Position.Distance(tmp.Position) < 25.0);
                    if (!duplicate)
                    {
                        var gs = new GuideStar(tmp); // referencePoint = position
                        foundStars.Add(gs);
                    }
                }
            }
        }

        // Final star selection - either the only star or the primary one for multi-star mode
        //   pass 1: find brightest star with peak value < 90% saturation AND SNR >= MinSNR (defaults to 6)
        //       this pass will reject saturated and nearly-saturated stars
        //   pass 2: find brightest non-saturated star with SNR >= MinSNR
        //   pass 3: find brightest star, even if saturated or below MinSNR
        for (int pass = 1; pass <= 3; pass++)
        {
            int phd2Choice = -1, choice = -1;
            for (int i = 0; i < stars.Count; i++)
            {
                var tmp = finds[i];
                if (!tmp.WasFound())
                    continue;
                if (pass == 1)
                {
                    if (tmp.PeakValue > satThresh)
                        continue; // near-saturated
                    if (tmp.LastFindResult == StarFindResult.Saturated || tmp.Snr < minSnr)
                        continue;
                }
                else if (pass == 2)
                {
                    if (tmp.LastFindResult == StarFindResult.Saturated || tmp.Snr < minSnr)
                        continue;
                }

                if (phd2Choice < 0)
                {
                    phd2Choice = i;
                    if (!options.AutoFindScoring || pass == 3)
                        break;
                }

                // A preferred primary must also head the multi-star list, otherwise PHD2's list logic would
                // drop all secondaries; keep searching in that case.
                if (penalties[i] == AutoFindPenalty.None && (maxStars <= 1 || foundStars.Exists(g => g.X == tmp.X && g.Y == tmp.Y)))
                {
                    choice = i;
                    break;
                }
            }

            if (phd2Choice < 0)
                continue;
            if (choice < 0)
                choice = phd2Choice;

            // star accepted
            var primary = finds[choice];
            var peak = stars[choice];
            if (diag is not null)
            {
                diag.Pass = pass;
                diag.PrimaryPeak = new GuidePoint(peak.X, peak.Y);
                diag.Phd2PrimaryPeak = new GuidePoint(stars[phd2Choice].X, stars[phd2Choice].Y);
            }

            if (maxStars > 1)
            {
                // Find the chosen star in the list and compute the offsetFromPrimary for all secondary stars
                int primaryLoc = -1;
                var primaryRef = new GuidePoint(peak.X, peak.Y);
                for (int k = 0; k < foundStars.Count; k++)
                {
                    var gs = foundStars[k];
                    if (gs.X == primary.X && gs.Y == primary.Y)
                        primaryLoc = k;
                    else
                        gs.OffsetFromPrimary = gs.ReferencePoint - primaryRef;
                }

                if (primaryLoc >= 0)
                {
                    // Delete saturated stars ahead of chosen star, likely saturated or otherwise flawed
                    foundStars.RemoveRange(0, primaryLoc);
                    // Prune total list size to match maxStars parameter
                    if (foundStars.Count > maxStars)
                        foundStars.RemoveRange(maxStars, foundStars.Count - maxStars);
                }
                else
                {
                    // Secondary stars are presumably degraded, just put primary star at head of list
                    foundStars.Clear();
                    foundStars.Add(new GuideStar(primary));
                }
            }
            else
            {
                foundStars.Add(new GuideStar(primary));
            }

            return foundStars;
        }

        // no star found
        foundStars.Clear();
        return foundStars;
    }

    private static void ComputePenalties(List<Peak> stars, GuideStar[] finds, Peak[] merged, AutoFindPenalty[] penalties, int searchRegion,
        int edgeDist, int width, int height)
    {
        // hot-pixel like: lowest HFD quartile and clearly below the median HFD of the found candidates
        var hfds = new List<double>();
        for (int i = 0; i < stars.Count; i++)
            if (finds[i].WasFound())
                hfds.Add(finds[i].Hfd);
        double q1 = double.NegativeInfinity, median = 0;
        if (hfds.Count >= 4)
        {
            hfds.Sort();
            q1 = hfds[(hfds.Count - 1) / 4];
            median = hfds[hfds.Count / 2];
        }

        int box = searchRegion + 5;
        int edge2 = edgeDist + searchRegion;
        for (int i = 0; i < stars.Count; i++)
        {
            var p = AutoFindPenalty.None;
            var s = stars[i];
            if (finds[i].WasFound() && finds[i].Hfd <= q1 && finds[i].Hfd < 0.8 * median)
                p |= AutoFindPenalty.LowHfd;

            foreach (var o in merged)
            {
                if (o.X == s.X && o.Y == s.Y) continue;
                if (Math.Abs(o.X - s.X) <= box && Math.Abs(o.Y - s.Y) <= box && o.Val >= 0.1f * s.Val)
                {
                    p |= AutoFindPenalty.Crowded;
                    break;
                }
            }

            if (s.X <= edge2 || s.X >= width - edge2 || s.Y <= edge2 || s.Y >= height - edge2)
                p |= AutoFindPenalty.NearEdge;
            penalties[i] = p;
        }
    }

    // std::set<Peak>::insert with ordering by value: equal values are not inserted
    private static void SetInsert(List<Peak> set, Peak p)
    {
        int lo = 0, hi = set.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (set[mid].Val < p.Val) lo = mid + 1;
            else hi = mid;
        }

        if (lo < set.Count && !(p.Val < set[lo].Val))
            return; // equivalent element exists
        set.Insert(lo, p);
    }

    // hmax[y*dw + x] = max(conv[y, x-srch .. x+srch]) for the rows of convRect and the candidate columns
    private static float[] RowMax(float[] conv, int dw, IntRect convRect, int srch)
    {
        var hmax = new float[conv.Length];
        int x0 = convRect.Left + srch, x1 = convRect.Right - srch;
        if (x1 < x0)
            return hmax;
        Parallel.For(convRect.Top, convRect.Bottom + 1, y =>
        {
            int row = y * dw;
            for (int x = x0; x <= x1; x++)
            {
                float m = conv[row + x - srch];
                for (int i = -srch + 1; i <= srch; i++)
                {
                    float v = conv[row + x + i];
                    if (v > m) m = v;
                }

                hmax[row + x] = m;
            }
        });
        return hmax;
    }

    // true when no pixel in the (2*srch+1)^2 window is greater than val (val itself is part of the window)
    private static bool IsLocalMax(float[] hmax, int dw, int x, int y, float val, int srch)
    {
        for (int j = -srch; j <= srch; j++)
        {
            if (hmax[dw * (y + j) + x] > val)
                return false;
        }

        return true;
    }

    // The mean part of GetStats: PHD2 computes mean = sum / count with the same row-major double
    // accumulation, so skipping the (unused) Welford variance gives a bit-identical mean.
    private static double GetMean(float[] img, int width, IntRect win)
    {
        double sum = 0.0;
        int p0 = win.Top * width + win.Left;
        for (int y = 0; y < win.Height; y++)
        {
            int end = p0 + win.Width;
            for (int p = p0; p < end; p++)
                sum += img[p];
            p0 += width;
        }

        return sum / ((double)win.Width * win.Height);
    }

    private static void GetStats(out double mean, out double stdev, float[] img, int width, IntRect win)
    {
        // Determine the mean and standard deviation
        double sum = 0.0;
        double a = 0.0;
        double q = 0.0;
        double k = 1.0;
        double km1 = 0.0;

        int p0 = win.Top * width + win.Left;
        for (int y = 0; y < win.Height; y++)
        {
            int end = p0 + win.Width;
            for (int p = p0; p < end; p++)
            {
                double x = img[p];
                sum += x;
                double a0 = a;
                a += (x - a) / k;
                q += (x - a0) * (x - a);
                km1 = k;
                k += 1.0;
            }

            p0 += width;
        }

        mean = sum / km1;
        stdev = Math.Sqrt(q / km1);
    }

    private static float[] Downsample(float[] src, int width, int height, int downsample, out int dw, out int dh)
    {
        dw = width / downsample;
        dh = height / downsample;
        var dst = new float[dw * dh];
        float d2 = downsample * downsample;
        for (int yy = 0; yy < dh; yy++)
        {
            for (int xx = 0; xx < dw; xx++)
            {
                float sum = 0.0f;
                for (int j = 0; j < downsample; j++)
                    for (int i = 0; i < downsample; i++)
                        sum += src[(yy * downsample + j) * width + xx * downsample + i];
                dst[yy * dw + xx] = sum / d2;
            }
        }

        return dst;
    }

    //                               A      B1     B2    C1     C2    C3     D1     D2     D3
    private static readonly double[] Psf = [0.906, 0.584, 0.365, .117, .049, -0.05, -.064, -.074, -.094];

    private static float[] PsfConv(float[] src, int width, int height)
    {
        var dst = new float[width * height];
        const int psfSize = 4;
        if (height - psfSize > psfSize)
        {
            if ((long)width * height >= 256 * 1024)
                Parallel.For(psfSize, height - psfSize, y => PsfConvRow(dst, src, width, y));
            else
                for (int y = psfSize; y < height - psfSize; y++)
                    PsfConvRow(dst, src, width, y);
        }

        return dst;
    }

    /* PSF Grid is:
    D3 D3 D3 D3 D3 D3 D3 D3 D3
    D3 D3 D3 D2 D1 D2 D3 D3 D3
    D3 D3 C3 C2 C1 C2 C3 D3 D3
    D3 D2 C2 B2 B1 B2 C2 D2 D3
    D3 D1 C1 B1 A  B1 C1 D1 D3
    D3 D2 C2 B2 B1 B2 C2 D2 D3
    D3 D3 C3 C2 C1 C2 C3 D3 D3
    D3 D3 D3 D2 D1 D2 D3 D3 D3
    D3 D3 D3 D3 D3 D3 D3 D3 D3
    */
    private static unsafe void PsfConvRow(float[] dstArr, float[] srcArr, int width, int y)
    {
        const int psfSize = 4;
        double p0 = Psf[0], p1 = Psf[1], p2 = Psf[2], p3 = Psf[3], p4 = Psf[4], p5 = Psf[5], p6 = Psf[6], p7 = Psf[7], p8 = Psf[8];
        fixed (float* src = srcArr, dst = dstArr)
        {
            for (int x = psfSize; x < width - psfSize; x++)
            {
                float* c = src + width * y + x;
                float* m1 = c - width, m2 = c - 2 * width, m3 = c - 3 * width, m4 = c - 4 * width;
                float* q1 = c + width, q2 = c + 2 * width, q3 = c + 3 * width, q4 = c + 4 * width;

                // Float additions in exactly the order of PHD2 psf_conv so the result is bit-identical.
                float a = c[0];
                float b1 = m1[0] + q1[0] + c[+1] + c[-1];
                float b2 = m1[-1] + m1[+1] + q1[-1] + q1[+1];
                float c1 = m2[0] + c[-2] + c[+2] + q2[0];
                float c2 = m2[-1] + m2[+1] + m1[-2] + m1[+2] + q1[-2] + q1[+2] + q2[-1] + q2[+1];
                float c3 = m2[-2] + m2[+2] + q2[-2] + q2[+2];
                float d1 = m3[0] + c[-3] + c[+3] + q3[0];
                float d2 = m3[-1] + m3[+1] + m1[-3] + m1[+3] + q1[-3] + q1[+3] + q3[-1] + q3[+1];
                float d3 = m2[-4] + m2[-3] + m2[+3] + m2[+4] + m1[-4] + m1[+4] + c[-4] + c[+4] + q1[-4] + q1[+4] + q2[-4] + q2[-3] +
                    q2[+3] + q2[+4];

                float* u = m4 - 4;
                for (int i = 0; i < 9; i++) d3 += *u++;
                u = m3 - 4;
                for (int i = 0; i < 3; i++) d3 += *u++;
                u += 3;
                for (int i = 0; i < 3; i++) d3 += *u++;
                u = q3 - 4;
                for (int i = 0; i < 3; i++) d3 += *u++;
                u += 3;
                for (int i = 0; i < 3; i++) d3 += *u++;
                u = q4 - 4;
                for (int i = 0; i < 9; i++) d3 += *u++;

                double mean = (a + b1 + b2 + c1 + c2 + c3 + d1 + d2 + d3) / 81.0;
                double psfFit = p0 * (a - mean) + p1 * (b1 - 4.0 * mean) + p2 * (b2 - 4.0 * mean) + p3 * (c1 - 4.0 * mean) +
                    p4 * (c2 - 8.0 * mean) + p5 * (c3 - 4.0 * mean) + p6 * (d1 - 4.0 * mean) + p7 * (d2 - 8.0 * mean) +
                    p8 * (d3 - 44.0 * mean);

                dst[width * y + x] = (float)psfFit;
            }
        }
    }
}
