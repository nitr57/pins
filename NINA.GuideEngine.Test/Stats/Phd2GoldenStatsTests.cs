// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Stats;
using NINA.GuideEngine.Test.Golden;

namespace NINA.GuideEngine.Test.Stats;

/// <summary>Compares the guiding_stats ports with results of the compiled PHD2 sources.</summary>
[TestFixture]
public class Phd2GoldenStatsTests
{
    [TestCase("windowed7")]
    [TestCase("negative")]
    public void AxisStatsMatchPhd2(string caseName)
    {
        var c = GoldenData.Case("stats", caseName);
        var w = new WindowedAxisStats(c.I("window"));
        int n = 0;
        foreach (var op in c.GetProperty("ops").EnumerateArray())
        {
            n++;
            switch (op.S("op"))
            {
                case "add":
                    w.AddGuideInfo(op.D("t"), op.D("pos"), op.D("guide"));
                    break;
                case "window":
                    w.ChangeWindowSize(op.I("size")).Should().BeTrue();
                    break;
                case "removeOldest":
                    w.RemoveOldestEntry();
                    break;
                case "state":
                    AssertState(w, op, n);
                    break;
            }
        }
    }

    private static void AssertState(AxisStats w, JsonElement op, int n)
    {
        double r2 = w.GetLinearFitResults(out double slope, out double intercept, out double sigma);
        var because = $"op {n}";
        w.Count.Should().Be(op.I("count"), because);
        w.Sum.Should().Be(op.D("sum"), because);
        w.Mean.Should().Be(op.D("mean"), because);
        w.Variance.Should().Be(op.D("variance"), because);
        w.Sigma.Should().Be(op.D("sigma"), because);
        w.PopulationSigma.Should().Be(op.D("popSigma"), because);
        w.Median.Should().Be(op.D("median"), because);
        w.MinDisplacement.Should().Be(op.D("minDisp"), because);
        w.MaxDisplacement.Should().Be(op.D("maxDisp"), because);
        w.MaxDelta.Should().Be(op.D("maxDelta"), because);
        w.MoveCount.Should().Be(op.I("moves"), because);
        w.ReversalCount.Should().Be(op.I("reversals"), because);
        Same(slope, op.D("slope"), because);
        Same(intercept, op.D("intercept"), because);
        Same(sigma, op.D("fitSigma"), because);
        Same(r2, op.D("r2"), because);
    }

    private static void Same(double actual, double expected, string because)
    {
        if (double.IsNaN(expected))
            double.IsNaN(actual).Should().BeTrue(because);
        else
            actual.Should().Be(expected, because);
    }

    [Test]
    public void DescriptiveStatsAndFiltersMatchPhd2()
    {
        var c = GoldenData.Case("stats", "descriptive");
        var ds = new DescriptiveStats();
        var hpf = new HighPassFilter(1.0, 2.0);
        var lpf = new LowPassFilter(6.0, 2.0);
        var hpf2 = new HighPassFilter(10.0, 0.5);
        var lpf2 = new LowPassFilter(3.0, 0.5);
        foreach (var op in c.GetProperty("ops").EnumerateArray())
        {
            double v = op.D("v");
            ds.AddValue(v);
            hpf.AddValue(v).Should().Be(op.D("hpf"));
            lpf.AddValue(v).Should().Be(op.D("lpf"));
            hpf2.AddValue(v).Should().Be(op.D("hpf2"));
            lpf2.AddValue(v).Should().Be(op.D("lpf2"));
            ds.Count.Should().Be(op.I("count"));
            ds.Mean.Should().Be(op.D("mean"));
            ds.Sum.Should().Be(op.D("sum"));
            ds.Minimum.Should().Be(op.D("min"));
            ds.Maximum.Should().Be(op.D("max"));
            ds.Variance.Should().Be(op.D("variance"));
            Same(ds.Sigma, op.D("sigma"), "sigma");
            ds.PopulationSigma.Should().Be(op.D("popSigma"));
            ds.MaxDelta.Should().Be(op.D("maxDelta"));
            ds.LastValue.Should().Be(op.D("last"));
        }
    }
}
