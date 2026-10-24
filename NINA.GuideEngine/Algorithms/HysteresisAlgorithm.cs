// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm_hysteresis.cpp (a6c02722)

using System.Globalization;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// PHD2 Hysteresis algorithm: <c>out = ((1-h)·in + h·lastOut)·aggression</c>, zeroed when
/// <c>|in| &lt; minMove</c>; the last output is remembered even when zeroed.
/// </summary>
public sealed class HysteresisAlgorithm : GuideAlgorithmBase
{
    public const double DefaultMinMove = 0.2;
    public const double DefaultHysteresis = 0.1;
    public const double DefaultAggression = 0.7;
    public const double MaxAggression = 2.0;
    public const double MaxHysteresis = 0.99;

    private static readonly string[] Names = ["minMove", "hysteresis", "aggression"];

    private double minMove;
    private double hysteresis;
    private double aggression;
    private double lastMove;

    public HysteresisAlgorithm()
    {
        RestoreDefaults();
        Reset();
    }

    public override string Name => "Hysteresis";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.Hysteresis;

    public override double MinMove
    {
        get => minMove;
        set => SetMinMove(value);
    }

    /// <summary>Hysteresis as a fraction 0..0.99.</summary>
    public double Hysteresis => hysteresis;

    /// <summary>Aggression as a fraction 0..2.</summary>
    public double Aggression => aggression;

    public override void Reset()
    {
        lastMove = 0;
    }

    public override double Result(double input)
    {
        double dReturn = (1.0 - hysteresis) * input + hysteresis * lastMove;

        dReturn *= aggression;

        if (Math.Abs(input) < minMove)
        {
            dReturn = 0.0;
        }

        lastMove = dReturn;
        return dReturn;
    }

    /// <summary>Returns true on error (PHD2 convention); invalid values fall back to the default.</summary>
    public bool SetMinMove(double value)
    {
        if (value < 0.0)
        {
            minMove = DefaultMinMove;
            return true;
        }

        minMove = value;
        return false;
    }

    /// <summary>Returns true on error; out-of-range values are clipped to [0, 0.99].</summary>
    public bool SetHysteresis(double value)
    {
        if (value < 0.0 || value > MaxHysteresis)
        {
            hysteresis = Math.Clamp(value, 0.0, MaxHysteresis);
            return true;
        }

        hysteresis = value;
        return false;
    }

    /// <summary>Returns true on error; invalid values fall back to the default. Always clears the last move.</summary>
    public bool SetAggression(double value)
    {
        bool error = false;
        if (value < 0.0 || value > MaxAggression)
        {
            error = true;
            aggression = DefaultAggression;
        }
        else
        {
            aggression = value;
        }

        lastMove = 0.0;
        return error;
    }

    public override IReadOnlyList<string> ParamNames => Names;

    public override bool TryGetParam(string name, out double value)
    {
        switch (name)
        {
            case "minMove": value = minMove; return true;
            case "hysteresis": value = hysteresis; return true;
            case "aggression": value = aggression; return true;
            default: value = 0.0; return false;
        }
    }

    public override bool TrySetParam(string name, double value)
    {
        bool err = name switch
        {
            "minMove" => SetMinMove(value),
            "hysteresis" => SetHysteresis(value),
            "aggression" => SetAggression(value),
            _ => true,
        };
        return !err;
    }

    public override string SettingsSummary => string.Format(
        CultureInfo.InvariantCulture, "Hysteresis = {0:F3}, Aggression = {1:F3}, Minimum move = {2:F3}", hysteresis, aggression, minMove);

    protected override void RestoreDefaults()
    {
        SetMinMove(DefaultMinMove);
        SetHysteresis(DefaultHysteresis);
        SetAggression(DefaultAggression);
    }
}
