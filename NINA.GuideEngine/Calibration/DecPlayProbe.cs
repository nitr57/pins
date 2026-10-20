// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

public enum DecPlayProbePhase
{
    /// <summary>North pulses until the star follows them: the gear is then engaged North.</summary>
    EngageNorth,

    /// <summary>South pulses back to the start: the take-up of the play from a gear engaged North.</summary>
    ReturnSouth,

    /// <summary>Done: <see cref="DecPlayProbe.TakeUp"/> holds the return, the gear is engaged South.</summary>
    Measured,

    /// <summary>Given up (<see cref="DecPlayProbe.FailureReason"/>): the gear's position is not known.</summary>
    Failed,
}

/// <summary>
/// Measures the Dec play again with a kept calibration, e.g. after a slew moved the gear to an unknown side of its play
/// and changed the load on it (docs/notes/PLAY-MODEL-BACKLASH.md). The calibration's Dec leg in short: North pulses until
/// the star follows <see cref="EngagedMoves"/> of them in a row (PHD2's backlash clearing), then South pulses back to the
/// start, whose pulse time and motion since the reversal are the take-up curve of the play, as the calibration's South
/// return (<see cref="CalibrationData.DecTakeUp"/>). The star ends near where it started, the gear engaged South.
/// The Dec rate is measured again on the engaged North steps (<see cref="RatePxPerMs"/>): the take-up is read against
/// it, and a rate that changed since the calibration (pier side, declination, load) would otherwise bend the curve.
/// </summary>
public sealed class DecPlayProbe
{
    /// <summary>Pulses in a row the star has to follow before the gear counts as engaged (PHD2 BL_BACKLASH_MIN_COUNT).</summary>
    public const int EngagedMoves = 3;

    /// <summary>
    /// Share of the previous step's motion the last step has to reach for the gear to count as engaged (PHD2: 0.6 of the
    /// calibrated motion). Relative to the motion seen rather than to the calibration: after a slew the Dec rate may be
    /// half or double the calibrated one (pier side, declination, load).
    /// </summary>
    public const double MoveFraction = 0.6;

    /// <summary>
    /// Share of a step's calibrated motion a pulse has to move the star to count as followed at all: the lowest Dec rate,
    /// relative to the calibration, the probe still measures.
    /// </summary>
    public const double MinMoveFraction = 0.25;

    /// <summary>Star motion one step makes with the gear engaged (px): the resolution of the take-up curve.</summary>
    public const double StepPx = 1.5;

    /// <summary>Shortest pulse sent (ms).</summary>
    public const int MinStepMs = 50;

    private readonly double maxPlayMs;
    private readonly List<double> northSteps = [];
    private double yRate;
    private readonly List<DecTakeUpPoint> takeUp = [];
    private bool started;
    private double lastPx;
    private double reversalPx;
    private int followed;
    private double maxSouthMs;

    /// <param name="yRatePxPerMs">Calibrated Dec rate (px/ms).</param>
    /// <param name="maxStepMs">Longest pulse allowed (the max Dec pulse).</param>
    /// <param name="maxPlayMs">Largest play to expect (ms of motor time); beyond it the probe gives up.</param>
    public DecPlayProbe(double yRatePxPerMs, int maxStepMs, double maxPlayMs)
    {
        if (!(yRatePxPerMs > 0) || !double.IsFinite(yRatePxPerMs))
        {
            throw new ArgumentOutOfRangeException(nameof(yRatePxPerMs));
        }

        yRate = yRatePxPerMs;
        this.maxPlayMs = maxPlayMs;
        StepMs = (int)Math.Clamp(Math.Round(StepPx / yRate), MinStepMs, Math.Max(MinStepMs, maxStepMs));
    }

    public DecPlayProbePhase Phase { get; private set; } = DecPlayProbePhase.EngageNorth;

    /// <summary>Pulse length of the North steps and the longest South step (ms).</summary>
    public int StepMs { get; }

    /// <summary>North pulse time sent (ms).</summary>
    public int NorthMs { get; private set; }

    /// <summary>South pulse time sent since the reversal (ms).</summary>
    public int SouthMs { get; private set; }

    /// <summary>
    /// Dec rate (px/ms) the take-up is read against: measured on the engaged North steps once the gear engaged, the
    /// calibrated rate until then.
    /// </summary>
    public double RatePxPerMs => yRate;

    /// <summary>True when <see cref="RatePxPerMs"/> was measured on the North steps rather than taken from the calibration.</summary>
    public bool RateMeasured { get; private set; }

    /// <summary>The return: South pulse time since the reversal and the star's motion South since then (px).</summary>
    public IReadOnlyList<DecTakeUpPoint> TakeUp => takeUp;

    public string? FailureReason { get; private set; }

    /// <summary>
    /// True when the probe failed because its North pulses moved the star South by several steps: the calibration's Dec
    /// direction does not fit the mount (a wrong Dec-flip setting after a meridian flip, or a stale calibration).
    /// </summary>
    public bool DecReversed { get; private set; }

    /// <summary>What the probe is doing, for the status line.</summary>
    public string Status => Phase switch
    {
        DecPlayProbePhase.EngageNorth => $"Dec play: North {NorthMs} ms",
        DecPlayProbePhase.ReturnSouth => $"Dec play: South {SouthMs} ms",
        DecPlayProbePhase.Measured => "Dec play measured",
        _ => $"Dec play not measured: {FailureReason}",
    };

    /// <summary>Rough progress (0..1).</summary>
    public double Progress => Phase switch
    {
        DecPlayProbePhase.EngageNorth => 0.4 * Math.Min(1.0, followed / (double)EngagedMoves),
        DecPlayProbePhase.ReturnSouth => 0.4 + 0.6 * Math.Clamp(SouthMs / Math.Max(1.0, maxSouthMs), 0, 1),
        _ => 1,
    };

    /// <summary>
    /// The next frame: the star's Dec position in mount coordinates since the start (px, North positive), measured after
    /// the pulses so far; NaN when the star was not measured. Returns the next pulse, null for none (the probe ended, or
    /// nothing was measured).
    /// </summary>
    public PulseCommand? Update(double decPx)
    {
        if (Phase is DecPlayProbePhase.Measured or DecPlayProbePhase.Failed || !double.IsFinite(decPx))
        {
            return null;
        }

        return Phase == DecPlayProbePhase.EngageNorth ? EngageNorth(decPx) : ReturnSouth(decPx);
    }

    /// <summary>A pulse did not go out as asked: the motion no longer tells the play.</summary>
    public void Abort(string reason)
    {
        if (Phase is not (DecPlayProbePhase.Measured or DecPlayProbePhase.Failed))
        {
            Fail(reason);
        }
    }

    private PulseCommand? EngageNorth(double decPx)
    {
        double expected = StepMs * yRate;
        if (!started)
        {
            started = true;
            lastPx = decPx;
            return North();
        }

        double step = decPx - lastPx;
        lastPx = decPx;
        followed = step >= MinMoveFraction * expected ? followed + 1 : 0;
        northSteps.Add(step);

        if (decPx <= -EngagedMoves * expected)
        {
            // North pulses moving the star South: the calibration does not fit this pointing
            DecReversed = true;
            return Fail("North pulses moved the star South");
        }

        // engaged once the star followed several pulses in a row and the one before the last already moved it about as far as
        // the last: while the play is still being taken up the motion per pulse grows
        if (followed >= EngagedMoves && northSteps[^2] >= MoveFraction * step)
        {
            MeasureRate();
            Phase = DecPlayProbePhase.ReturnSouth;
            reversalPx = decPx;

            // the play twice over and the way back: a return that takes longer did not happen as measured
            maxSouthMs = maxPlayMs + 2 * decPx / yRate + 2 * StepMs;
            return ReturnSouth(decPx);
        }

        if (NorthMs >= maxPlayMs + (EngagedMoves + 2) * StepMs)
        {
            return Fail($"the star did not follow {NorthMs} ms of North pulses");
        }

        return North();
    }

    private PulseCommand? ReturnSouth(double decPx)
    {
        double expected = StepMs * yRate;
        if (SouthMs > 0)
        {
            takeUp.Add(new DecTakeUpPoint(SouthMs, reversalPx - decPx));
        }

        // back at the start (within half a step): done
        if (decPx <= 0.5 * expected)
        {
            Phase = DecPlayProbePhase.Measured;
            return null;
        }

        if (SouthMs >= maxSouthMs)
        {
            if (takeUp.Count > 0 && takeUp[^1].MovedPx >= expected)
            {
                Phase = DecPlayProbePhase.Measured;
                return null;
            }

            return Fail($"the star did not follow {SouthMs} ms of South pulses");
        }

        // full steps while the play is taken up, shorter ones to end near the start
        int ms = (int)Math.Clamp(Math.Round(decPx / yRate), MinStepMs, StepMs);
        SouthMs += ms;
        return new PulseCommand(GuideDirection.South, ms);
    }

    // the rate from the followed North steps but the first, which may still have taken up the end of the play
    private void MeasureRate()
    {
        int n = EngagedMoves - 1;
        double moved = northSteps.Skip(northSteps.Count - n).Sum();
        double rate = moved / (n * (double)StepMs);
        if (rate > 0 && double.IsFinite(rate))
        {
            yRate = rate;
            RateMeasured = true;
        }
    }

    private PulseCommand North()
    {
        NorthMs += StepMs;
        return new PulseCommand(GuideDirection.North, StepMs);
    }

    private PulseCommand? Fail(string reason)
    {
        Phase = DecPlayProbePhase.Failed;
        FailureReason = reason;
        return null;
    }
}
