// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>Tuning of <see cref="DecFlipVerifier"/>. Defaults are deliberately conservative.</summary>
public sealed record DecFlipVerifierOptions
{
    /// <summary>Number of Dec corrections evaluated after the flip.</summary>
    public int WindowCorrections { get; init; } = 5;

    /// <summary>Maximum guide frames observed (frames without a Dec correction give no evidence).</summary>
    public int MaxFrames { get; init; } = 8;

    /// <summary>Wrong-sign corrections needed to invert.</summary>
    public int MinWrongCorrections { get; init; } = 3;

    /// <summary>Corrections whose expected move (pulse × yRate) is below this are ignored as noise, px.</summary>
    public double MinExpectedMovePx { get; init; } = 0.5;

    /// <summary>
    /// A correction counts as wrong (or right) only when the error moved against (or with) the expected direction by
    /// at least this fraction of the expected move, and |error| grew (or shrank) by the same amount.
    /// </summary>
    public double MoveFraction { get; init; } = 0.5;

    /// <summary>Absolute floor for the move/growth thresholds, px.</summary>
    public double MinMovePx { get; init; } = 0.3;

    /// <summary>Minimum growth of |Dec error| from the first to the last evaluated frame, px.</summary>
    public double MinTotalGrowthPx { get; init; } = 1.0;
}

/// <summary>State/verdict of the Dec self-check.</summary>
public enum DecFlipVerdict
{
    /// <summary>Not started or finished.</summary>
    Inactive,

    /// <summary>Collecting evidence.</summary>
    Observing,

    /// <summary>The window ended without clear evidence of inverted Dec corrections.</summary>
    DecOk,

    /// <summary>Dec corrections clearly have the wrong sign: flip yAngle, alert and persist the per-mount Dec-flip setting.</summary>
    InvertDec,
}

/// <summary>Evidence gathered so far.</summary>
/// <param name="Verdict">Current verdict.</param>
/// <param name="FramesObserved">Frames observed since <see cref="DecFlipVerifier.Start"/>.</param>
/// <param name="CorrectionsEvaluated">Significant corrections whose effect has been measured.</param>
/// <param name="WrongSign">Corrections after which the error grew in the direction opposite to the correction.</param>
/// <param name="RightSign">Corrections that moved the star as expected.</param>
/// <param name="TotalGrowthPx">|error| at the last evaluated frame minus |error| before the first evaluated correction.</param>
public sealed record DecFlipCheckResult(DecFlipVerdict Verdict, int FramesObserved, int CorrectionsEvaluated, int WrongSign,
    int RightSign, double TotalGrowthPx);

/// <summary>
/// Post-meridian-flip Dec self-check (agreed extension, own design, see ARCHITECTURE.md, "Calibration"). After a flipped calibration
/// is applied, feed each guide frame's Dec error and the Dec correction issued for it. When the Dec error repeatedly
/// grows in the direction opposite to the corrections (i.e. the corrections have the wrong sign), the verdict becomes
/// <see cref="DecFlipVerdict.InvertDec"/>. Small or noisy errors, backlash (no movement) and correct-sign corrections
/// never trigger.
/// </summary>
public sealed class DecFlipVerifier
{
    private readonly DecFlipVerifierOptions options;
    private double? pendingError;
    private double pendingExpected;
    private double? firstError;
    private double lastEvaluatedError;
    private int frames;
    private int evaluated;
    private int wrong;
    private int right;
    private double wrongExpectedSum;

    public DecFlipVerifier(DecFlipVerifierOptions? options = null)
    {
        this.options = options ?? new DecFlipVerifierOptions();
    }

    public DecFlipVerdict Verdict { get; private set; } = DecFlipVerdict.Inactive;

    public bool IsActive => Verdict == DecFlipVerdict.Observing;

    /// <summary>Starts observing (call when guiding starts with a calibration flipped for the other pier side).</summary>
    public void Start()
    {
        pendingError = null;
        firstError = null;
        frames = evaluated = wrong = right = 0;
        wrongExpectedSum = 0;
        lastEvaluatedError = 0;
        Verdict = DecFlipVerdict.Observing;
    }

    /// <summary>Stops observing without a verdict.</summary>
    public void Reset()
    {
        pendingError = null;
        Verdict = DecFlipVerdict.Inactive;
    }

    /// <summary>
    /// Observes one guide frame.
    /// </summary>
    /// <param name="decErrorPx">Dec error of this frame in mount coordinates (mount Y of star − lock, px); NaN when the star was lost.</param>
    /// <param name="decCorrection">Dec pulse issued in response to this frame, null or 0 ms when none.</param>
    /// <param name="yRatePxPerMs">Dec calibration rate, px/ms.</param>
    public DecFlipCheckResult Observe(double decErrorPx, PulseCommand? decCorrection, double yRatePxPerMs)
    {
        if (Verdict != DecFlipVerdict.Observing)
        {
            return Snapshot();
        }

        frames++;

        if (double.IsNaN(decErrorPx))
        {
            // star lost: the effect of a pending correction can't be measured any more
            pendingError = null;
        }
        else
        {
            if (pendingError is { } prev)
            {
                Evaluate(prev, pendingExpected, decErrorPx);
                pendingError = null;
            }

            double expected = ExpectedMove(decCorrection, yRatePxPerMs);
            if (Math.Abs(expected) >= options.MinExpectedMovePx)
            {
                pendingError = decErrorPx;
                pendingExpected = expected;
            }
        }

        double growth = firstError is { } f ? Math.Abs(lastEvaluatedError) - Math.Abs(f) : 0.0;
        if (IsInverted(growth, final: false))
        {
            Verdict = DecFlipVerdict.InvertDec;
        }
        else if (evaluated >= options.WindowCorrections || frames >= options.MaxFrames)
        {
            Verdict = IsInverted(growth, final: true) ? DecFlipVerdict.InvertDec : DecFlipVerdict.DecOk;
        }

        return Snapshot();
    }

    /// <summary>
    /// Expected change of the mount-Y error for a Dec pulse: a South pulse reduces mount Y, a North pulse increases it
    /// (PHD2 MoveOffset: yDistance &gt; 0 → SOUTH).
    /// </summary>
    public static double ExpectedMove(PulseCommand? pulse, double yRatePxPerMs)
    {
        if (pulse is not { DurationMs: > 0 } p || !(yRatePxPerMs > 0))
        {
            return 0.0;
        }

        double amount = p.DurationMs * yRatePxPerMs;
        return p.Direction switch
        {
            GuideDirection.South => -amount,
            GuideDirection.North => amount,
            _ => 0.0,
        };
    }

    private void Evaluate(double before, double expected, double after)
    {
        firstError ??= before;
        lastEvaluatedError = after;
        evaluated++;

        double actual = after - before;
        double threshold = Math.Max(options.MoveFraction * Math.Abs(expected), options.MinMovePx);
        double growth = Math.Abs(after) - Math.Abs(before);

        if (actual * expected < 0 && Math.Abs(actual) >= threshold && growth >= threshold)
        {
            wrong++;
            wrongExpectedSum += Math.Abs(expected);
        }
        else if (actual * expected > 0 && Math.Abs(actual) >= threshold)
        {
            right++;
        }
    }

    private bool IsInverted(double totalGrowth, bool final)
    {
        if (wrong < options.MinWrongCorrections)
        {
            return false;
        }

        // Early decision only with unanimous evidence; at the end of the window a clear majority is required.
        bool evidence = final ? right * 2 < wrong : right == 0;
        double growthNeeded = Math.Max(options.MinTotalGrowthPx, 0.5 * wrongExpectedSum);
        return evidence && totalGrowth >= growthNeeded;
    }

    private DecFlipCheckResult Snapshot()
    {
        double growth = firstError is { } f ? Math.Abs(lastEvaluatedError) - Math.Abs(f) : 0.0;
        return new DecFlipCheckResult(Verdict, frames, evaluated, wrong, right, growth);
    }
}
