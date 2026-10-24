// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2018 Bruce Waddington
// Ported from PHD2 src/guiding_stats.cpp (a6c02722)

namespace NINA.GuideEngine.Stats;

/// <summary>A guide star displacement sample with relative time. Port of PHD2 <c>StarDisplacement</c>.</summary>
public readonly record struct StarDisplacement(double DeltaTime, double StarPos, bool Guided = false, bool Reversal = false);

/// <summary>
/// Collects (time, position, guide amount) samples and computes statistics incrementally. Port of PHD2
/// <c>AxisStats</c>. Timestamps should be small relative values (e.g. seconds since start) since they
/// are only used for linear fits.
/// </summary>
/// <remarks>
/// PHD2 quirks are kept on purpose: the max/min displacement start at <c>DBL_MAX</c> / <c>DBL_MIN</c>
/// (the smallest positive double, so the maximum of an all-negative dataset is ~2.2e-308), and the
/// max delta is only tracked from the third sample on.
/// </remarks>
public class AxisStats
{
    private protected readonly LinkedList<StarDisplacement> entries = new();
    private protected StarDisplacement[]? indexCache;
    private protected int axisMoves;
    private protected int axisReversals;
    private protected double prevMove;
    private protected double prevPosition;
    private protected double sumX;
    private protected double sumY;
    private protected double sumXY;
    private protected double sumXSq;
    private protected double sumYSq;
    private protected double maxDisplacement;
    private protected double minDisplacement;
    private protected double maxDelta;
    private protected int maxDeltaInx;

    public AxisStats()
    {
        InitializeScalars();
    }

    private protected void InitializeScalars()
    {
        axisMoves = 0;
        axisReversals = 0;
        sumY = 0.0;
        sumYSq = 0.0;
        sumX = 0.0;
        sumXY = 0.0;
        sumXSq = 0.0;
        prevPosition = 0.0;
        prevMove = 0.0;
        minDisplacement = double.MaxValue;
        maxDisplacement = DescriptiveStats.CppDoubleMin;
        maxDelta = 0.0;

        // Deviation from PHD2: maxDeltaInx is left uninitialised in PHD2; we start at 0.
        maxDeltaInx = 0;
    }

    public void ClearAll()
    {
        InitializeScalars();
        entries.Clear();
        indexCache = null;
    }

    /// <summary>Number of samples in the dataset with a non-zero guide amount.</summary>
    public int MoveCount => axisMoves;

    /// <summary>Number of samples whose guide amount reversed the direction of the previous non-zero one.</summary>
    public int ReversalCount => axisReversals;

    public int Count => entries.Count;

    /// <summary>Returns the entry at <paramref name="index"/> (oldest = 0) or a zero entry when out of range.</summary>
    public StarDisplacement GetEntry(int index)
    {
        if (index < 0 || index >= entries.Count)
            return new StarDisplacement(0.0, 0.0);
        indexCache ??= entries.ToArray();
        return indexCache[index];
    }

    public StarDisplacement LastEntry => entries.Count > 0 ? entries.Last!.Value : new StarDisplacement(0.0, 0.0);

    /// <summary>Adds a sample of relative time, star position and guide amount.</summary>
    public virtual void AddGuideInfo(double deltaT, double starPos, double guideAmt)
    {
        bool guided = false;
        bool reversal = false;
        minDisplacement = Math.Min(starPos, minDisplacement);
        maxDisplacement = Math.Max(starPos, maxDisplacement);
        sumX += deltaT;
        sumXY += deltaT * starPos;
        sumXSq += deltaT * deltaT;
        sumYSq += starPos * starPos;
        sumY += starPos;
        if (guideAmt != 0.0)
        {
            guided = true;
            ++axisMoves;
            if (guideAmt * prevMove < 0.0)
            {
                ++axisReversals;
                reversal = true;
            }

            prevMove = guideAmt;
        }

        if (entries.Count > 1)
        {
            double newDelta = Math.Abs(starPos - prevPosition);
            if (newDelta >= maxDelta)
            {
                maxDelta = newDelta;
                maxDeltaInx = entries.Count;
            }
        }

        entries.AddLast(new StarDisplacement(deltaT, starPos, guided, reversal));
        indexCache = null;
        prevPosition = starPos;
    }

    /// <summary>Maximum absolute difference between consecutive positions (see remarks).</summary>
    public double MaxDelta => entries.Count > 1 ? maxDelta : 0.0;

    public double Sum => sumY;

    public double Mean => entries.Count > 0 ? sumY / entries.Count : 0.0;

    /// <summary>Sample variance (n-1).</summary>
    public double Variance
    {
        get
        {
            int sz = entries.Count;
            if (sz <= 1)
                return 0.0;
            double n = sz;
            return (n * sumYSq - sumY * sumY) / (n * (n - 1.0));
        }
    }

    /// <summary>Sample standard deviation (n-1).</summary>
    public double Sigma
    {
        get
        {
            int sz = entries.Count;
            if (sz <= 1)
                return 0.0;
            double n = sz;
            double variance = (n * sumYSq - sumY * sumY) / (n * (n - 1));
            return variance >= 0.0 ? Math.Sqrt(variance) : 0.0;
        }
    }

    /// <summary>Population standard deviation (n) about the mean.</summary>
    public double PopulationSigma
    {
        get
        {
            int sz = entries.Count;
            if (sz <= 1)
                return 0.0;
            double n = sz;
            double variance = (n * sumYSq - sumY * sumY) / (n * n);
            return variance >= 0.0 ? Math.Sqrt(variance) : 0.0;
        }
    }

    public double Median
    {
        get
        {
            int sz = entries.Count;
            if (sz > 1)
            {
                var sorted = new double[sz];
                int i = 0;
                foreach (var e in entries)
                    sorted[i++] = e.StarPos;
                Array.Sort(sorted);
                int ctr = sz / 2;
                return sz % 2 == 1 ? sorted[ctr] : (sorted[ctr] + sorted[ctr - 1]) / 2.0;
            }

            return sz == 1 ? entries.First!.Value.StarPos : 0.0;
        }
    }

    public double MinDisplacement => entries.Count > 0 ? minDisplacement : 0.0;

    public double MaxDisplacement => entries.Count > 0 ? maxDisplacement : 0.0;

    /// <summary>
    /// Least-squares linear fit of position over time. Returns R² (coefficient of determination).
    /// </summary>
    /// <param name="slope">Slope in position units per time unit.</param>
    /// <param name="intercept">Intercept.</param>
    public double GetLinearFitResults(out double slope, out double intercept) => GetLinearFitResults(out slope, out intercept, out _, false);

    /// <summary>
    /// Least-squares linear fit; also returns the sample sigma of the drift-removed data.
    /// </summary>
    public double GetLinearFitResults(out double slope, out double intercept, out double sigma) => GetLinearFitResults(out slope, out intercept, out sigma, true);

    private double GetLinearFitResults(out double slopeOut, out double interceptOut, out double sigmaOut, bool wantSigma)
    {
        int numVals = entries.Count;
        sigmaOut = 0.0;
        if (numVals <= 1)
        {
            slopeOut = 0.0;
            interceptOut = 0.0;
            return 0.0;
        }

        double currentVariance = 0.0;
        double currentMean = 0.0;
        double slope = ((numVals * sumXY) - (sumX * sumY)) / ((numVals * sumXSq) - (sumX * sumX));
        double intcpt = (sumY - (slope * sumX)) / numVals;
        if (wantSigma)
        {
            int inx = 0;
            foreach (var e in entries)
            {
                double newVal = e.StarPos - (e.DeltaTime * slope + intcpt);
                if (inx == 0)
                {
                    currentMean = newVal;
                }
                else
                {
                    // PHD2 quirk: divides by numVals rather than the running count.
                    double delta = newVal - currentMean;
                    double newMean = currentMean + delta / numVals;
                    currentVariance += delta * delta;
                    currentMean = newMean;
                }

                inx++;
            }

            sigmaOut = Math.Sqrt(currentVariance / (numVals - 1));
        }

        slopeOut = slope;
        interceptOut = intcpt;

        double syy = sumYSq - (sumY * sumY) / numVals;
        double sxy = sumXY - (sumX * sumY) / numVals;
        double sxx = sumXSq - (sumX * sumX) / numVals;
        double sse = syy - (sxy * sxy) / sxx;
        return (syy - sse) / syy;
    }
}

/// <summary>
/// <see cref="AxisStats"/> limited to the most recent entries. Auto-trimmed when constructed with a
/// positive window size, otherwise trimmed by the caller via <see cref="RemoveOldestEntry"/>. Port of
/// PHD2 <c>WindowedAxisStats</c>.
/// </summary>
public sealed class WindowedAxisStats : AxisStats
{
    private bool autoWindowing;
    private int windowSize;

    public WindowedAxisStats()
    {
    }

    /// <param name="autoWindowSize">Window size; 0 = no automatic trimming.</param>
    public WindowedAxisStats(int autoWindowSize)
    {
        autoWindowing = autoWindowSize > 0;
        windowSize = autoWindowSize;
    }

    public int WindowSize => windowSize;

    /// <summary>Changes the auto-window size, trimming older entries. 0 disables auto-windowing.</summary>
    public bool ChangeWindowSize(int newSize)
    {
        if (newSize > 0)
        {
            int numDeletes = Count - newSize;
            while (numDeletes > 0)
            {
                RemoveOldestEntry();
                numDeletes--;
            }

            windowSize = newSize;
            autoWindowing = true;
            return true;
        }

        if (newSize == 0)
        {
            autoWindowing = false;
            windowSize = 0;
            return true;
        }

        return false;
    }

    // Must be called before the oldest entry is removed.
    private void AdjustMinMaxValues()
    {
        var target = entries.First!.Value;
        bool recalNeeded = false;
        double prev = target.StarPos;
        if (entries.Count > 1)
        {
            recalNeeded = target.StarPos == maxDisplacement || target.StarPos == minDisplacement || maxDeltaInx == 0;
            if (recalNeeded)
            {
                minDisplacement = double.MaxValue;
                maxDisplacement = DescriptiveStats.CppDoubleMin;
                maxDelta = 0.0;
            }
        }

        if (recalNeeded)
        {
            int idx = 0;
            foreach (var entry in entries)
            {
                if (idx == 0)
                {
                    idx++;
                    continue;
                }

                minDisplacement = Math.Min(minDisplacement, entry.StarPos);
                maxDisplacement = Math.Max(maxDisplacement, entry.StarPos);
                if (idx > 1)
                {
                    if (Math.Abs(entry.StarPos - prev) > maxDelta)
                    {
                        maxDelta = Math.Abs(entry.StarPos - prev);
                        maxDeltaInx = idx;
                    }
                }

                prev = entry.StarPos;
                idx++;
            }
        }
    }

    /// <summary>Removes the oldest entry and updates the statistics.</summary>
    public void RemoveOldestEntry()
    {
        if (entries.Count == 0)
            return;
        var target = entries.First!.Value;
        double val = target.StarPos;
        double deltaT = target.DeltaTime;
        sumY -= val;
        sumYSq -= val * val;
        sumX -= deltaT;
        sumXSq -= deltaT * deltaT;
        sumXY -= deltaT * val;
        if (target.Reversal)
            axisReversals--;
        if (target.Guided)
            axisMoves--;
        AdjustMinMaxValues();
        entries.RemoveFirst();
        indexCache = null;
        maxDeltaInx--;
    }

    public override void AddGuideInfo(double deltaT, double starPos, double guideAmt)
    {
        base.AddGuideInfo(deltaT, starPos, guideAmt);
        if (autoWindowing && Count > windowSize)
            RemoveOldestEntry();
    }
}
