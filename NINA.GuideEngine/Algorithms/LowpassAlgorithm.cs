// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm_lowpass.cpp (a6c02722)

using System.Globalization;
using NINA.GuideEngine.Stats;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// PHD2 Lowpass algorithm: median of the last 10 inputs plus <c>slopeWeight</c> × linear-fit slope,
/// limited to the input magnitude and zeroed below min-move.
/// </summary>
public sealed class LowpassAlgorithm : GuideAlgorithmBase
{
    public const double DefaultMinMove = 0.2;
    public const double DefaultSlopeWeight = 5.0;
    public const int HistorySize = 10;

    private static readonly string[] Names = ["minMove", "slopeWeight"];

    private readonly WindowedAxisStats axisStats = new(0); // self-managed window
    private double minMove;
    private double slopeWeight;
    private int timeBase;

    public LowpassAlgorithm()
    {
        RestoreDefaults();
        Reset();
    }

    public override string Name => "Lowpass";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.Lowpass;

    public override double MinMove
    {
        get => minMove;
        set => SetMinMove(value);
    }

    public double SlopeWeight => slopeWeight;

    public override void Reset()
    {
        axisStats.ClearAll();
        timeBase = 0;

        // Needs to be zero-filled to start
        while (axisStats.Count < HistorySize)
        {
            axisStats.AddGuideInfo(timeBase++, 0, 0);
        }
    }

    public override double Result(double input)
    {
        // Manual trimming of window (instead of auto-size) is done for full backward compatibility with original algo
        axisStats.AddGuideInfo(timeBase++, input, 0);
        double median = axisStats.Median;
        axisStats.RemoveOldestEntry();
        axisStats.GetLinearFitResults(out double slope, out _);
        double dReturn = median + slopeWeight * slope;

        if (Math.Abs(dReturn) > Math.Abs(input))
        {
            dReturn = input;
        }

        if (Math.Abs(input) < minMove)
        {
            dReturn = 0.0;
        }

        return dReturn;
    }

    public bool SetMinMove(double value)
    {
        if (value < 0)
        {
            minMove = DefaultMinMove;
            return true;
        }

        minMove = value;
        return false;
    }

    public bool SetSlopeWeight(double value)
    {
        if (value < 0.0)
        {
            slopeWeight = DefaultSlopeWeight;
            return true;
        }

        slopeWeight = value;
        return false;
    }

    public override IReadOnlyList<string> ParamNames => Names;

    public override bool TryGetParam(string name, out double value)
    {
        switch (name)
        {
            case "minMove": value = minMove; return true;
            case "slopeWeight": value = slopeWeight; return true;
            default: value = 0.0; return false;
        }
    }

    public override bool TrySetParam(string name, double value)
    {
        bool err = name switch
        {
            "minMove" => SetMinMove(value),
            "slopeWeight" => SetSlopeWeight(value),
            _ => true,
        };
        return !err;
    }

    public override string SettingsSummary => string.Format(
        CultureInfo.InvariantCulture, "Slope weight = {0:F3}, Minimum move = {1:F3}", slopeWeight, minMove);

    protected override void RestoreDefaults()
    {
        SetMinMove(DefaultMinMove);
        SetSlopeWeight(DefaultSlopeWeight);
    }
}
