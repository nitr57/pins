// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2018 Bruce Waddington
// Ported from PHD2 src/guiding_stats.cpp (a6c02722)

namespace NINA.GuideEngine.Stats;

/// <summary>Single-pole high-pass filter on a sample stream. Port of PHD2 <c>HighPassFilter</c>.</summary>
public sealed class HighPassFilter
{
    private readonly double alphaCutoff = 1.0;
    private double count;
    private double prevVal;
    private double hpfResult;

    /// <summary>Creates a pass-through filter (alpha 1), like PHD2's default constructor.</summary>
    public HighPassFilter()
    {
    }

    /// <param name="cutoffPeriod">Cutoff period (same unit as <paramref name="samplePeriod"/>, typically seconds).</param>
    /// <param name="samplePeriod">Sample period; values below 1 are treated as 1 (PHD2 behaviour).</param>
    public HighPassFilter(double cutoffPeriod, double samplePeriod)
    {
        alphaCutoff = cutoffPeriod / (cutoffPeriod + Math.Max(1.0, samplePeriod));
        Reset();
    }

    public double AddValue(double newVal)
    {
        if (count == 0)
        {
            hpfResult = newVal;
        }
        else
        {
            hpfResult = alphaCutoff * (hpfResult + newVal - prevVal);
        }

        prevVal = newVal;
        ++count;
        return hpfResult;
    }

    public double CurrentValue => hpfResult;

    public void Reset()
    {
        count = 0;
        prevVal = 0.0;
        hpfResult = 0.0;
    }
}

/// <summary>Single-pole low-pass filter on a sample stream. Port of PHD2 <c>LowPassFilter</c>.</summary>
public sealed class LowPassFilter
{
    private readonly double alphaCutoff = 1.0;
    private double count;
    private double lpfResult;

    public LowPassFilter()
    {
    }

    /// <param name="cutoffPeriod">Cutoff period (same unit as <paramref name="samplePeriod"/>).</param>
    /// <param name="samplePeriod">Sample period; values below 1 are treated as 1 (PHD2 behaviour).</param>
    public LowPassFilter(double cutoffPeriod, double samplePeriod)
    {
        alphaCutoff = 1.0 - (cutoffPeriod / (cutoffPeriod + Math.Max(1.0, samplePeriod)));
        Reset();
    }

    public double AddValue(double newVal)
    {
        if (count == 0)
        {
            lpfResult = newVal;
        }
        else
        {
            lpfResult += alphaCutoff * (newVal - lpfResult);
        }

        ++count;
        return lpfResult;
    }

    public double CurrentValue => lpfResult;

    public void Reset()
    {
        count = 0;
        lpfResult = 0.0;
    }
}
