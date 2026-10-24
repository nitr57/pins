// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Imaging;

[TestFixture]
public class DarkAndDefectTests
{
    private static GuideFrame Flat(int w, int h, ushort v, double exposureMs = 1000, int binning = 1)
    {
        var f = new GuideFrame(w, h) { ExposureMs = exposureMs, Binning = binning };
        Array.Fill(f.Pixels, v);
        return f;
    }

    [Test]
    public void SelectDark_SmallestAtLeastExposure_ElseLongest()
    {
        var lib = new DarkLibrary();
        lib.Add(Flat(4, 4, 10, 500));
        lib.Add(Flat(4, 4, 20, 2000));
        lib.Add(Flat(4, 4, 30, 1000));
        lib.Add(Flat(4, 4, 99, 800, binning: 2));

        lib.SelectDark(700)!.ExposureMs.Should().Be(1000);
        lib.SelectDark(1000)!.ExposureMs.Should().Be(1000);
        lib.SelectDark(100)!.ExposureMs.Should().Be(500);
        lib.SelectDark(5000)!.ExposureMs.Should().Be(2000);
        lib.SelectDark(100, binning: 2)!.ExposureMs.Should().Be(800);
        lib.SelectDark(100, binning: 3).Should().BeNull();
    }

    [Test]
    public void Subtract_Phd2RuntimeMode_PedestalIsDarkMedian()
    {
        var light = Flat(8, 8, 1000);
        var dark = Flat(8, 8, 300);
        dark[3, 3] = 5000; // hot pixel in dark only
        DarkLibrary.Subtract(light, new DarkFrame(dark), DarkPedestalMode.Phd2Runtime).Should().BeTrue();
        light.Pedestal.Should().Be(300);
        light[0, 0].Should().Be(1000);
        light[3, 3].Should().Be(0, "clamped at 0");
    }

    [Test]
    public void Subtract_LightMedianMode_UsesFormula()
    {
        var light = Flat(8, 8, 1000);
        var dark = Flat(8, 8, 300);
        DarkLibrary.Subtract(light, new DarkFrame(dark), DarkPedestalMode.LightMedian);
        light.Pedestal.Should().Be(0, "dark median below light median");
        light[1, 1].Should().Be(700);

        var light2 = Flat(8, 8, 200);
        light2[0, 0] = 65535;
        dark[0, 0] = 0;
        DarkLibrary.Subtract(light2, new DarkFrame(dark), DarkPedestalMode.LightMedian);
        light2.Pedestal.Should().Be(100);
        light2[1, 1].Should().Be(0);
        light2[0, 0].Should().Be(65535, "clamped at 65535");
    }

    [Test]
    public void Subtract_Subframe_OnlyTouchesSubframeAndUsesDarkRoiMedian()
    {
        var light = Flat(10, 10, 1000);
        light.Subframe = new IntRect(2, 2, 4, 4);
        var dark = Flat(10, 10, 100);
        for (int y = 2; y < 6; y++)
            for (int x = 2; x < 6; x++)
                dark[x, y] = 400;
        DarkLibrary.Subtract(light, new DarkFrame(dark));
        light.Pedestal.Should().Be(400);
        light[3, 3].Should().Be(1000);
        light[0, 0].Should().Be(1000, "outside subframe untouched");
    }

    [Test]
    public void Subtract_IncompatibleSize_ReturnsFalse()
    {
        DarkLibrary.Subtract(Flat(8, 8, 1), new DarkFrame(Flat(4, 4, 1))).Should().BeFalse();
    }

    [Test]
    public void MasterDarkBuilder_AveragesWithIntegerDivision()
    {
        var b = new MasterDarkBuilder();
        b.Add(Flat(3, 3, 10, 1500));
        b.Add(Flat(3, 3, 11, 1500));
        b.Add(Flat(3, 3, 13, 1500));
        var m = b.Build();
        m[1, 1].Should().Be(11); // 34/3
        m.ExposureMs.Should().Be(1500);
        b.FrameCount.Should().Be(3);
        DarkLibrary.DefaultFramesPerDark.Should().Be(5);
    }

    [Test]
    public void DarkSubtraction_RemovesHotPixelsSoAutoFindIgnoresThem()
    {
        var r = new StarFieldRenderer(200, 150) { Seed = 3 };
        r.AddStar(100.3, 75.6, 30000);
        var darkR = new StarFieldRenderer(200, 150) { Background = 0, Seed = 4 };
        foreach (var hp in new[] { (40, 40), (150, 30), (60, 120) })
        {
            r.HotPixels.Add((hp.Item1, hp.Item2, 20000));
            darkR.HotPixels.Add((hp.Item1, hp.Item2, 19600));
        }

        var darks = Enumerable.Range(0, 5).Select(i => darkR.Render(100 + i)).ToList();
        var lib = new DarkLibrary();
        var master = MasterDarkBuilder.Average(darks);
        master.ExposureMs = 2000;
        lib.Add(master);
        var light = r.Render();
        lib.Subtract(light).Should().BeTrue();
        light[40, 40].Should().BeLessThan(1500);
        light.Pedestal.Should().BeGreaterThan(400);
    }

    private static GuideFrame MasterDarkWithDefects(out List<PixelCoord> hot, out List<PixelCoord> cold)
    {
        var r = new StarFieldRenderer(120, 90) { Background = 0, Bias = 1000, ReadNoise = 3, Gain = 0, Seed = 21 };
        var f = r.Render();
        hot = new List<PixelCoord> { new(10, 10), new(50, 60), new(0, 5), new(119, 89) };
        cold = new List<PixelCoord> { new(70, 20) };
        foreach (var p in hot) f[p.X, p.Y] = 3000;
        foreach (var p in cold) f[p.X, p.Y] = 50;
        return f;
    }

    [Test]
    public void DefectMapBuilder_FindsHotAndColdPixels()
    {
        var master = MasterDarkWithDefects(out var hot, out var cold);
        var b = new DefectMapBuilder();
        b.Init(master);
        b.SetAggressiveness(0, 0); // 8 sigma: only the planted defects
        var map = b.Build();
        map.Defects.Should().Contain(hot).And.Contain(cold);
        map.Count.Should().Be(hot.Count + cold.Count);
        b.HotPixelCount.Should().Be(hot.Count);
        b.ColdPixelCount.Should().Be(cold.Count);
        map.Defects[0].Should().Be(cold[0], "cold defects are emitted first");

        b.SetAggressiveness(100, 100);
        b.HotPixelCount.Should().BeGreaterThan(hot.Count, "aggressive threshold picks up noise too");
        DefectMapBuilder.AggrToSigma(0).Should().Be(8.0);
        DefectMapBuilder.AggrToSigma(100).Should().Be(0.125);
    }

    [Test]
    public void DefectMap_ApplyReplacesDefectsWithNeighbourMedian_AndRespectsSubframe()
    {
        var master = MasterDarkWithDefects(out var hot, out _);
        var map = DefectMapBuilder.FromMasterDark(master, 0, 0);
        var light = master.Clone();
        map.Apply(light);
        foreach (var p in hot)
            light[p.X, p.Y].Should().BeInRange(980, 1020);

        var light2 = master.Clone();
        light2.Subframe = new IntRect(0, 0, 30, 30);
        map.Apply(light2);
        light2[10, 10].Should().BeInRange(980, 1020);
        light2[50, 60].Should().Be(3000, "outside the subframe");
    }

    [Test]
    public void DefectMap_SerializeRoundTrip()
    {
        var map = new DefectMap(new[] { new PixelCoord(1, 2), new PixelCoord(300, 4000) });
        var text = "# comment\n" + map.Serialize();
        var back = DefectMap.Parse(text);
        back.Defects.Should().Equal(map.Defects);
    }

    [Test]
    public void FramePreprocessor_DefectMapReplacesDarkSubtraction()
    {
        var light = Flat(20, 20, 1000);
        light[5, 5] = 30000;
        var lib = new DarkLibrary();
        lib.Add(Flat(20, 20, 300, 1000));
        var pre = new FramePreprocessor { Darks = lib, DefectMap = new DefectMap(new[] { new PixelCoord(5, 5) }) };
        var outFrame = pre.Process(light, out var info);
        info.DefectMapApplied.Should().BeTrue();
        info.DarkSubtracted.Should().BeFalse();
        outFrame[5, 5].Should().Be(1000);
        outFrame[0, 0].Should().Be(1000);
        outFrame.Pedestal.Should().Be(0);

        pre.DefectMap = null;
        var light2 = Flat(20, 20, 1000);
        light2.ExposureMs = 900;
        pre.Process(light2, out info);
        info.DarkSubtracted.Should().BeTrue();
        info.DarkExposureMs.Should().Be(1000);
        light2.Pedestal.Should().Be(300);
    }

    [Test]
    public void FramePreprocessor_NoiseReductionAndBinning_OnSubframe()
    {
        var src = new StarFieldRenderer(64, 48) { Seed = 5 }.Render();
        src.Subframe = new IntRect(8, 8, 32, 24);
        var expected = src.Clone();
        ImageMath.Median3(expected);

        var pre = new FramePreprocessor { NoiseReduction = NoiseReduction.Median3x3 };
        var f = src.Clone();
        var result = pre.Process(f);
        result.Should().BeSameAs(f);
        result.Pixels.Should().Equal(expected.Pixels);

        pre.NoiseReduction = NoiseReduction.Mean2x2;
        pre.SoftwareBinning = 2;
        var binned = pre.Process(src.Clone());
        binned.Width.Should().Be(32);
        binned.Subframe.Should().Be(new IntRect(4, 4, 16, 12));
        binned.Binning.Should().Be(2);
    }

    [Test]
    public void FramePreprocessor_PedestalKeepsSaturationDetectionCorrect()
    {
        // saturated star in a light frame; after dark subtraction with pedestal the known-ADU test still fires
        var r = new StarFieldRenderer(100, 100) { Saturation = 4095, BitsPerPixel = 12, Bias = 200, Seed = 8 };
        r.AddStar(50.2, 49.7, 400000);
        var light = r.Render();
        var lib = new DarkLibrary();
        var dark = Flat(100, 100, 200, 2000);
        lib.Add(dark);
        new FramePreprocessor { Darks = lib }.Process(light);
        light.Pedestal.Should().Be(200);
        var s = new Star();
        s.Find(light, 15, 50, 50, StarFindMode.Centroid, 1.5, 20, 4095 - 200).Should().BeTrue();
        s.LastFindResult.Should().Be(StarFindResult.Saturated);
    }
}
