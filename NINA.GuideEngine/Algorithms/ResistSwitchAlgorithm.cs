// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm_resistswitch.cpp (a6c02722)

using System.Globalization;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// PHD2 ResistSwitch algorithm (default for Dec): resists switching the correction direction until the
/// last 10 inputs make a compelling case; optional fast switch for large excursions.
/// </summary>
/// <remarks>
/// Lifecycle as in PHD2: history is re-seeded with zeros and the current side cleared on reset, which
/// happens on stop, resume, dither and guiding re-enable. Settle-done is a no-op.
/// </remarks>
public sealed class ResistSwitchAlgorithm : GuideAlgorithmBase
{
    public const double DefaultMinMove = 0.2;
    public const double DefaultAggression = 1.0;
    public const bool DefaultFastSwitch = true;
    public const int HistorySize = 10;

    private static readonly string[] Names = ["minMove", "fastSwitch", "aggression"];

    private readonly List<double> history = new(HistorySize + 1);
    private double minMove;
    private double aggression;
    private bool fastSwitchEnabled;
    private int currentSide;

    public ResistSwitchAlgorithm()
    {
        RestoreDefaults();
        Reset();
    }

    public override string Name => "ResistSwitch";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.ResistSwitch;

    public override double MinMove
    {
        get => minMove;
        set => SetMinMove(value);
    }

    /// <summary>Aggression as a fraction 0..1.</summary>
    public double Aggression => aggression;

    public bool FastSwitchEnabled
    {
        get => fastSwitchEnabled;
        set => fastSwitchEnabled = value;
    }

    /// <summary>Current correction side: -1, 0 (undecided) or +1.</summary>
    public int CurrentSide => currentSide;

    /// <summary>Snapshot of the input history, oldest first.</summary>
    public IReadOnlyList<double> History => history.ToArray();

    public override void Reset()
    {
        history.Clear();
        while (history.Count < HistorySize)
        {
            history.Add(0.0);
        }

        currentSide = 0;
    }

    private static int Sign(double x) => x > 0.0 ? 1 : x < 0.0 ? -1 : 0;

    public override double Result(double input)
    {
        double rslt = input;

        history.Add(input);
        history.RemoveAt(0);

        if (!Evaluate(input))
            rslt = 0.0;

        rslt *= aggression;
        return rslt;
    }

    // Returns false where PHD2 throws (vetoes the move).
    private bool Evaluate(double input)
    {
        if (Math.Abs(input) < minMove)
            return false; // input < m_minMove

        if (fastSwitchEnabled)
        {
            double thresh = 3.0 * minMove;
            if (Sign(input) != currentSide && Math.Abs(input) > thresh)
            {
                // force switch
                currentSide = 0;
                int i;
                for (i = 0; i < HistorySize - 3; i++)
                    history[i] = 0.0;
                for (; i < HistorySize; i++)
                    history[i] = input;
            }
        }

        int decHistory = 0;
        for (int i = 0; i < history.Count; i++)
        {
            if (Math.Abs(history[i]) > minMove)
            {
                decHistory += Sign(history[i]);
            }
        }

        if (currentSide == 0 || Sign(currentSide) == -Sign(decHistory))
        {
            if (Math.Abs(decHistory) < 3)
                return false; // not compelling enough

            double oldest = 0.0;
            double newest = 0.0;

            for (int i = 0; i < 3; i++)
            {
                oldest += history[i];
                newest += history[history.Count - (i + 1)];
            }

            if (Math.Abs(newest) <= Math.Abs(oldest))
                return false; // Not getting worse

            currentSide = Sign(decHistory);
        }

        if (currentSide != Sign(input))
            return false; // must have overshot -- vetoing move

        return true;
    }

    /// <summary>Returns true on error; values &lt;= 0 fall back to the default. Clears the current side.</summary>
    public bool SetMinMove(double value)
    {
        if (value <= 0.0)
        {
            minMove = DefaultMinMove;
            return true;
        }

        minMove = value;
        currentSide = 0;
        return false;
    }

    /// <summary>Returns true on error; values outside [0, 1] fall back to the default.</summary>
    public bool SetAggression(double value)
    {
        if (value < 0.0 || value > 1.0)
        {
            aggression = DefaultAggression;
            return true;
        }

        aggression = value;
        return false;
    }

    public override IReadOnlyList<string> ParamNames => Names;

    public override bool TryGetParam(string name, out double value)
    {
        switch (name)
        {
            case "minMove": value = minMove; return true;
            case "fastSwitch": value = fastSwitchEnabled ? 1.0 : 0.0; return true;
            case "aggression": value = aggression; return true;
            default: value = 0.0; return false;
        }
    }

    public override bool TrySetParam(string name, double value)
    {
        bool err;
        switch (name)
        {
            case "minMove":
                err = SetMinMove(value);
                break;
            case "fastSwitch":
                fastSwitchEnabled = value != 0.0;
                err = false;
                break;
            case "aggression":
                err = SetAggression(value);
                break;
            default:
                err = true;
                break;
        }

        return !err;
    }

    public override string SettingsSummary => string.Format(
        CultureInfo.InvariantCulture,
        "Minimum move = {0:F3} Aggression = {1:F0}% FastSwitch = {2}",
        minMove,
        aggression * 100.0,
        fastSwitchEnabled ? "enabled" : "disabled");

    protected override void RestoreDefaults()
    {
        SetMinMove(DefaultMinMove);
        SetAggression(DefaultAggression);
        fastSwitchEnabled = DefaultFastSwitch;
    }
}
