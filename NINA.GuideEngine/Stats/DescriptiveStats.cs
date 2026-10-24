// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2018 Bruce Waddington
// Ported from PHD2 src/guiding_stats.cpp (a6c02722)

namespace NINA.GuideEngine.Stats;

/// <summary>
/// Running (non-windowed) statistics using Knuth/Welford updates. Port of PHD2 <c>DescriptiveStats</c>.
/// Values are not retained.
/// </summary>
public sealed class DescriptiveStats
{
    // C++ std::numeric_limits<double>::min() is the smallest positive normal double, not the most negative value.
    internal const double CppDoubleMin = 2.2250738585072014E-308;

    private int count;
    private double runningS;
    private double newS;
    private double runningMean;
    private double newMean;
    private double minValue;
    private double maxValue;
    private double lastValue;
    private double maxDelta;

    public DescriptiveStats()
    {
        ClearAll();
    }

    /// <summary>Adds a value and updates the running statistics.</summary>
    public void AddValue(double val)
    {
        count++;
        if (count == 1)
        {
            runningMean = val;
            newMean = val;
            minValue = val;
            maxValue = val;
            maxDelta = 0.0;
        }
        else
        {
            newMean = runningMean + (val - runningMean) / count;
            newS = runningS + (val - runningMean) * (val - newMean);
            runningMean = newMean;
            runningS = newS;
            minValue = Math.Min(minValue, val);
            maxValue = Math.Max(maxValue, val);
            double newDelta = Math.Abs(val - lastValue);
            maxDelta = Math.Max(maxDelta, newDelta);
        }

        lastValue = val;
    }

    /// <summary>Resets all statistics.</summary>
    public void ClearAll()
    {
        count = 0;
        runningS = 0.0;
        newS = 0.0;
        runningMean = 0.0;
        newMean = 0.0;
        lastValue = 0.0;
        minValue = double.MaxValue;
        maxValue = CppDoubleMin;
        maxDelta = 0.0;
    }

    public int Count => count;

    /// <summary>The value most recently added (0 when empty).</summary>
    public double LastValue => lastValue;

    public double Mean => count > 0 ? runningMean : 0.0;

    public double Sum => count > 0 ? runningMean * count : 0.0;

    public double Minimum => count > 0 ? minValue : 0.0;

    public double Maximum => count > 0 ? maxValue : 0.0;

    /// <summary>Raw sum of squared deviations (PHD2 calls this "variance").</summary>
    public double Variance => count > 1 ? runningS : 0.0;

    /// <summary>Sample standard deviation (n-1).</summary>
    public double Sigma => count > 0 ? Math.Sqrt(runningS / (count - 1)) : 0.0;

    /// <summary>Population standard deviation (n).</summary>
    public double PopulationSigma => count > 0 ? Math.Sqrt(runningS / count) : 0.0;

    /// <summary>Maximum absolute difference between consecutive values.</summary>
    public double MaxDelta => count > 1 ? maxDelta : 0.0;
}
