// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm_lowpass2.cpp (a6c02722)

using System.Globalization;
using NINA.GuideEngine.Stats;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// PHD2 Lowpass2 algorithm: linear fit over an auto-trimmed 10-sample window,
/// <c>out = slope · n · aggressiveness</c>, with outlier and reject-driven history resets.
/// </summary>
public sealed class Lowpass2Algorithm : GuideAlgorithmBase
{
    public const double DefaultMinMove = 0.2;

    /// <summary>Default aggressiveness in percent.</summary>
    public const double DefaultAggressiveness = 80.0;

    public const int HistorySize = 10;

    private static readonly string[] Names = ["minMove", "aggressiveness"];

    private readonly WindowedAxisStats axisStats = new(HistorySize); // Auto-windowed
    private double aggressiveness;
    private double minMove;
    private int rejects;
    private int timeBase;

    public Lowpass2Algorithm()
    {
        RestoreDefaults();
        Reset();
    }

    public override string Name => "Lowpass2";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.Lowpass2;

    public override double MinMove
    {
        get => minMove;
        set => SetMinMove(value);
    }

    /// <summary>Aggressiveness in percent (PHD2 UI range 0..100).</summary>
    public double Aggressiveness => aggressiveness;

    /// <summary>Number of samples currently in the history window.</summary>
    public int HistoryCount => axisStats.Count;

    public override void Reset()
    {
        axisStats.ClearAll();
        timeBase = 0;
        rejects = 0;
    }

    public override double Result(double input)
    {
        axisStats.AddGuideInfo(timeBase++, input, 0); // AxisStats instance is auto-windowed
        int numpts = axisStats.Count;
        double dReturn;
        double attenuation = aggressiveness / 100.0;
        double newSlope = 0;

        if (numpts < 4)
        {
            dReturn = input * attenuation; // Don't fall behind while we're figuring things out
        }
        else
        {
            if (Math.Abs(input) > 4.0 * minMove) // Outlier deflection - dump the history
            {
                dReturn = input * attenuation;
                Reset();
                numpts = 0;
            }
            else
            {
                axisStats.GetLinearFitResults(out newSlope, out _);
                dReturn = newSlope * numpts * attenuation;

                // Don't return a result that will push the star further in the wrong direction
                if (input * dReturn < 0)
                    dReturn = 0;
            }
        }

        if (Math.Abs(dReturn) > Math.Abs(input)) // Keep guide pulses below magnitude of last deflection
        {
            dReturn = input * attenuation;
            rejects++;
            if (rejects > 3) // 3-in-a-row, our slope is not useful
            {
                Reset();
            }
        }
        else
        {
            rejects = 0;
        }

        if (Math.Abs(input) < minMove)
            dReturn = 0.0;

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

    /// <summary>
    /// Sets aggressiveness in percent. Returns true on error; like PHD2, a negative value leaves the
    /// current aggressiveness unchanged.
    /// </summary>
    public bool SetAggressiveness(double value)
    {
        if (value < 0.0)
        {
            return true;
        }

        aggressiveness = value;
        return false;
    }

    public override IReadOnlyList<string> ParamNames => Names;

    public override bool TryGetParam(string name, out double value)
    {
        switch (name)
        {
            case "minMove": value = minMove; return true;
            case "aggressiveness": value = aggressiveness; return true;
            default: value = 0.0; return false;
        }
    }

    public override bool TrySetParam(string name, double value)
    {
        bool err = name switch
        {
            "minMove" => SetMinMove(value),
            "aggressiveness" => SetAggressiveness(value),
            _ => true,
        };
        return !err;
    }

    public override string SettingsSummary => string.Format(
        CultureInfo.InvariantCulture, "Aggressiveness = {0:F3}, Minimum move = {1:F3}", aggressiveness, minMove);

    protected override void RestoreDefaults()
    {
        SetMinMove(DefaultMinMove);
        SetAggressiveness(DefaultAggressiveness);
    }
}
