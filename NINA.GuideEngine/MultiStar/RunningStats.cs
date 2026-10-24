// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2018 Bruce Waddington
// Ported from PHD2 src/guiding_stats.cpp (DescriptiveStats, subset) (a6c02722)

namespace NINA.GuideEngine.MultiStar;

/// <summary>
/// Running mean / sample standard deviation (Welford), the subset of PHD2 DescriptiveStats used by
/// the multi-star logic. Kept local to the multi-star code on purpose.
/// </summary>
internal sealed class RunningStats
{
    private double runningMean;
    private double runningS;

    public int Count { get; private set; }

    public double Mean => runningMean;

    public void AddValue(double val)
    {
        Count++;
        if (Count == 1)
        {
            runningMean = val;
        }
        else
        {
            double newMean = runningMean + (val - runningMean) / Count;
            double newS = runningS + (val - runningMean) * (val - newMean);
            runningMean = newMean;
            runningS = newS;
        }
    }

    /// <summary>Sample standard deviation (PHD2 GetSigma; NaN-free only for Count ≥ 2).</summary>
    public double Sigma => Count > 0 ? Math.Sqrt(runningS / (Count - 1)) : 0.0;

    public void Clear()
    {
        Count = 0;
        runningMean = 0.0;
        runningS = 0.0;
    }
}
