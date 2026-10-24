// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;

namespace NINA.GuideEngine.Test.Imaging;

[TestFixture]
public class ImageMathTests
{
    private static GuideFrame RandomFrame(int w, int h, int seed, int max = 4000)
    {
        var rng = new Random(seed);
        var f = new GuideFrame(w, h);
        for (int i = 0; i < f.Pixels.Length; i++)
            f.Pixels[i] = (ushort)rng.Next(max);
        return f;
    }

    // Reference: PHD2 medians return the middle element for odd counts, the (integer) mean of the two
    // middle elements for even counts.
    private static ushort RefMedian(List<ushort> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (ushort)((v[n / 2 - 1] + v[n / 2]) / 2);
    }

    private static ushort RefMedian3At(GuideFrame f, IntRect r, int x, int y)
    {
        var v = new List<ushort>();
        for (int j = y - 1; j <= y + 1; j++)
            for (int i = x - 1; i <= x + 1; i++)
                if (i >= r.Left && i <= r.Right && j >= r.Top && j <= r.Bottom)
                    v.Add(f[i, j]);
        return RefMedian(v);
    }

    [Test]
    public void Median3_MatchesBruteForce_FullFrame()
    {
        var src = RandomFrame(37, 23, 1);
        var copy = src.Clone();
        ImageMath.Median3(copy);
        var r = new IntRect(0, 0, 37, 23);
        for (int y = 0; y < 23; y++)
            for (int x = 0; x < 37; x++)
                copy[x, y].Should().Be(RefMedian3At(src, r, x, y), $"pixel {x},{y}");
    }

    [Test]
    public void Median3_Subframe_FiltersOnlySubframeAndClearsOutside()
    {
        var src = RandomFrame(40, 30, 2);
        var f = src.Clone();
        var sf = new IntRect(5, 7, 20, 11);
        f.Subframe = sf;
        ImageMath.Median3(f);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                if (sf.Contains(x, y))
                    f[x, y].Should().Be(RefMedian3At(src, sf, x, y));
                else
                    f[x, y].Should().Be(0);
            }
        }
    }

    [Test]
    public void Median3_LargeFrame_ParallelEqualsSequentialReference()
    {
        var src = RandomFrame(700, 500, 3);
        var dst = new ushort[src.Pixels.Length];
        ImageMath.Median3(dst, src.Pixels, 700, new IntRect(0, 0, 700, 500));
        var r = new IntRect(0, 0, 700, 500);
        var rng = new Random(9);
        for (int k = 0; k < 2000; k++)
        {
            int x = rng.Next(700), y = rng.Next(500);
            dst[y * 700 + x].Should().Be(RefMedian3At(src, r, x, y));
        }
    }

    [Test]
    public void QuickLRecon_Is2x2SlidingMean()
    {
        var src = RandomFrame(9, 7, 4);
        var f = src.Clone();
        ImageMath.QuickLRecon(f);
        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                int expected;
                if (x < 8 && y < 6) expected = (src[x, y] + src[x + 1, y] + src[x, y + 1] + src[x + 1, y + 1]) >> 2;
                else if (x == 8 && y < 6) expected = (src[x, y] + src[x, y + 1]) >> 1;
                else if (y == 6 && x < 8) expected = (src[x, y] + src[x + 1, y]) >> 1;
                else expected = src[x, y];
                f[x, y].Should().Be((ushort)expected, $"{x},{y}");
            }
        }
    }

    [Test]
    public void QuickLRecon_Subframe()
    {
        var src = RandomFrame(20, 20, 5);
        var f = src.Clone();
        f.Subframe = new IntRect(3, 4, 6, 5);
        ImageMath.QuickLRecon(f);
        f[3, 4].Should().Be((ushort)((src[3, 4] + src[4, 4] + src[3, 5] + src[4, 5]) >> 2));
        f[8, 8].Should().Be(src[8, 8]); // bottom-right of subframe
        f[0, 0].Should().Be(0);
    }

    [Test]
    public void MedianBorderingPixels_InteriorEdgeCorner()
    {
        var f = RandomFrame(6, 5, 6);
        var interior = new List<ushort> { f[1, 1], f[2, 1], f[3, 1], f[1, 2], f[3, 2], f[1, 3], f[2, 3], f[3, 3] };
        ImageMath.MedianBorderingPixels(f, 2, 2).Should().Be(RefMedian(interior));
        var left = new List<ushort> { f[0, 1], f[0, 3], f[1, 1], f[1, 2], f[1, 3] };
        ImageMath.MedianBorderingPixels(f, 0, 2).Should().Be(RefMedian(left));
        var corner = new List<ushort> { f[1, 0], f[0, 1], f[1, 1] };
        ImageMath.MedianBorderingPixels(f, 0, 0).Should().Be(RefMedian(corner));
        var corner2 = new List<ushort> { f[4, 4], f[5, 3], f[4, 3] };
        ImageMath.MedianBorderingPixels(f, 5, 4).Should().Be(RefMedian(corner2));
    }

    [Test]
    public void SoftwareBin2x2_AveragesBlocksAndScalesMetadata()
    {
        var src = RandomFrame(10, 8, 7);
        src.Subframe = new IntRect(2, 2, 6, 4);
        src.Pedestal = 12;
        var b = ImageMath.SoftwareBin(src, 2);
        b.Width.Should().Be(5);
        b.Height.Should().Be(4);
        b.Binning.Should().Be(2);
        b.Pedestal.Should().Be(12);
        b.Subframe.Should().Be(new IntRect(1, 1, 3, 2));
        b[1, 2].Should().Be((ushort)((src[2, 4] + src[3, 4] + src[2, 5] + src[3, 5]) / 4));
    }

    [Test]
    public void NthElement_And_HistogramStats_MatchSort()
    {
        var rng = new Random(8);
        for (int trial = 0; trial < 50; trial++)
        {
            int n = rng.Next(1, 300);
            var a = new ushort[n];
            for (int i = 0; i < n; i++) a[i] = (ushort)rng.Next(50);
            var sorted = a.OrderBy(v => v).ToArray();
            int k = rng.Next(n);
            ImageMath.NthElement<ushort>(a.ToArray(), k).Should().Be(sorted[k]);
            var st = ImageMath.HistogramStats(a, n, new IntRect(0, 0, n, 1));
            st.Median.Should().Be(sorted[n / 2]);
            st.Min.Should().Be(sorted[0]);
            st.Max.Should().Be(sorted[^1]);
        }
    }

    [Test]
    public void MedianFilter_MatchesBruteForce()
    {
        var src = RandomFrame(25, 19, 10, 300);
        var filt = ImageMath.MedianFilter(src, 3);
        // PHD2's histogram median returns the element at index n/2 of the sorted window
        for (int y = 0; y < 19; y++)
        {
            for (int x = 0; x < 25; x++)
            {
                var v = new List<ushort>();
                for (int j = Math.Max(0, y - 3); j <= Math.Min(18, y + 3); j++)
                    for (int i = Math.Max(0, x - 3); i <= Math.Min(24, x + 3); i++)
                        v.Add(src[i, j]);
                v.Sort();
                filt[x, y].Should().Be(v[v.Count / 2], $"{x},{y}");
            }
        }
    }
}
