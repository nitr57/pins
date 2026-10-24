// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Ported from PHD2 src/image_math.cpp (DefectMapBuilder, RemoveDefects) (a6c02722)

using System.Globalization;
using System.Text;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Imaging;

/// <summary>A sensor pixel coordinate.</summary>
public readonly record struct PixelCoord(int X, int Y);

/// <summary>
/// Bad-pixel (defect) map: a list of pixel coordinates that are replaced by the median of their
/// neighbours (PHD2 RemoveDefects). When loaded it replaces dark subtraction.
/// </summary>
public sealed class DefectMap
{
    private readonly List<PixelCoord> defects = new();

    public DefectMap()
    {
    }

    public DefectMap(IEnumerable<PixelCoord> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        defects.AddRange(points);
    }

    public IReadOnlyList<PixelCoord> Defects => defects;

    public int Count => defects.Count;

    public void Add(PixelCoord p) => defects.Add(p);

    public bool Contains(PixelCoord p) => defects.Contains(p);

    /// <summary>
    /// Replaces each defect inside the valid region of <paramref name="light"/> by the median of its
    /// bordering pixels. Defects are corrected in list order, so later defects see already-corrected
    /// neighbours, exactly as in PHD2.
    /// </summary>
    public void Apply(GuideFrame light)
    {
        ArgumentNullException.ThrowIfNull(light);
        var px = light.Pixels;
        int w = light.Width, h = light.Height;
        if (!light.Subframe.IsEmpty)
        {
            var sf = light.Subframe;
            foreach (var d in defects)
            {
                if (sf.Contains(d.X, d.Y))
                    px[d.Y * w + d.X] = ImageMath.MedianBorderingPixels(px, w, h, d.X, d.Y);
            }
        }
        else
        {
            foreach (var d in defects)
            {
                if (d.X >= 0 && d.X < w && d.Y >= 0 && d.Y < h)
                    px[d.Y * w + d.X] = ImageMath.MedianBorderingPixels(px, w, h, d.X, d.Y);
            }
        }
    }

    /// <summary>Serialises to text, one "x y" pair per line (PHD2 defect map file body format).</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var d in defects)
            sb.Append(d.X.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(d.Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parses the output of <see cref="Serialize"/>. Lines starting with '#' are ignored.</summary>
    public static DefectMap Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var map = new DefectMap();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new FormatException($"invalid defect map line '{line}'");
            map.Add(new PixelCoord(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)));
        }

        return map;
    }
}

/// <summary>
/// Builds a <see cref="DefectMap"/> from a master dark (PHD2 DefectMapBuilder): the master dark is
/// median filtered (PHD2 window constant 15 = half-width, i.e. a 31x31 window) and pixels deviating from
/// the filtered value by more than an aggressiveness-dependent multiple of the dark's standard
/// deviation are hot (positive) or cold (negative) defects.
/// </summary>
public sealed class DefectMapBuilder
{
    /// <summary>PHD2 BuildFilteredDark WINDOW constant (passed as half-width to MedianFilter).</summary>
    public const int FilterHalfWidth = 15;

    /// <summary>PHD2 default aggressiveness (Refine_DefMap.cpp DefDMSigmaX).</summary>
    public const int DefaultAggressiveness = 75;

    private readonly List<BadPixel> coldPx = new();
    private readonly List<BadPixel> hotPx = new();
    private int aggrCold = DefaultAggressiveness;
    private int aggrHot = DefaultAggressiveness;

    private readonly record struct BadPixel(int X, int Y, int V);

    public ImageStats Stats { get; private set; }

    public GuideFrame? FilteredDark { get; private set; }

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Loads potential defects from <paramref name="masterDark"/>. <paramref name="filteredDark"/> may be
    /// supplied (e.g. cached); otherwise it is computed with <see cref="ImageMath.MedianFilter"/>.
    /// </summary>
    public void Init(GuideFrame masterDark, GuideFrame? filteredDark = null)
    {
        ArgumentNullException.ThrowIfNull(masterDark);
        filteredDark ??= ImageMath.MedianFilter(masterDark, FilterHalfWidth);
        FilteredDark = filteredDark;
        Stats = ImageMath.GetImageStats(masterDark, new IntRect(0, 0, masterDark.Width, masterDark.Height));

        int thresh = (int)(AggrToSigma(100) * Stats.Stdev);
        coldPx.Clear();
        hotPx.Clear();
        int w = masterDark.Width;
        for (int y = 0; y < masterDark.Height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int filt = filteredDark.Pixels[y * w + x];
                int val = masterDark.Pixels[y * w + x];
                int v = val - filt;
                if (v > thresh)
                    hotPx.Add(new BadPixel(x, y, v));
                else if (-v > thresh)
                    coldPx.Add(new BadPixel(x, y, -v));
            }
        }

        // Deviation from PHD2: PHD2 stores candidates in a std::set ordered by deviation only, which
        // silently drops every pixel whose deviation equals that of an already stored pixel. We keep all
        // of them (stable-sorted by deviation, scan order within equal deviations).
        StableSortByV(coldPx);
        StableSortByV(hotPx);
        IsInitialized = true;
    }

    /// <summary>Aggressiveness 0..100 for cold and hot pixels (higher finds more defects).</summary>
    public void SetAggressiveness(int cold, int hot)
    {
        aggrCold = Math.Clamp(cold, 0, 100);
        aggrHot = Math.Clamp(hot, 0, 100);
    }

    public int ColdPixelCount => coldPx.Count - LowerBound(coldPx, ColdThreshold);

    public int HotPixelCount => hotPx.Count - LowerBound(hotPx, HotThreshold);

    private int ColdThreshold => (int)(AggrToSigma(aggrCold) * Stats.Stdev);

    private int HotThreshold => (int)(AggrToSigma(aggrHot) * Stats.Stdev);

    /// <summary>Aggressiveness 0..100 maps to a sigma factor from 8.0 to 0.125.</summary>
    public static double AggrToSigma(int val) => double.Exp2(3.0 - (6.0 / 100.0) * val);

    /// <summary>Builds the map: cold defects first, then hot, each in ascending deviation (PHD2 order).</summary>
    public DefectMap Build()
    {
        if (!IsInitialized) throw new InvalidOperationException("DefectMapBuilder not initialized");
        var map = new DefectMap();
        for (int i = LowerBound(coldPx, ColdThreshold); i < coldPx.Count; i++)
            map.Add(new PixelCoord(coldPx[i].X, coldPx[i].Y));
        for (int i = LowerBound(hotPx, HotThreshold); i < hotPx.Count; i++)
            map.Add(new PixelCoord(hotPx[i].X, hotPx[i].Y));
        return map;
    }

    /// <summary>Convenience: build a defect map from a master dark with the given aggressiveness.</summary>
    public static DefectMap FromMasterDark(GuideFrame masterDark, int coldAggressiveness = DefaultAggressiveness, int hotAggressiveness = DefaultAggressiveness)
    {
        var b = new DefectMapBuilder();
        b.Init(masterDark);
        b.SetAggressiveness(coldAggressiveness, hotAggressiveness);
        return b.Build();
    }

    private static void StableSortByV(List<BadPixel> list)
    {
        var sorted = list.Select((p, i) => (p, i)).OrderBy(t => t.p.V).ThenBy(t => t.i).Select(t => t.p).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // first index with V >= thresh (std::set::lower_bound)
    private static int LowerBound(List<BadPixel> list, int thresh)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].V < thresh) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
