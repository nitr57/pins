// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>What the play model knows now, for status and log.</summary>
/// <param name="FullReversalMs">Play a reversal from a fully engaged gear takes up (ms), posterior mean.</param>
/// <param name="SlackNorthMs">Play left before a North pulse moves the star (ms), posterior mean; NaN while unknown.</param>
/// <param name="SlackSouthMs">Play left before a South pulse moves the star (ms), posterior mean; NaN while unknown.</param>
/// <param name="Engaged">Direction the gear rests against, null while it floats inside the play or is unknown.</param>
/// <param name="StateKnown">False until a pulse or a calibration gave the gear a position.</param>
/// <param name="Shape">Most probable shape of the play: None, Hard, Medium, Soft or Calibrated.</param>
/// <param name="NoiseSigmaPx">Frame-to-frame noise σ of the Dec offset the gate uses (px).</param>
/// <param name="Reversals">Dec reversals made.</param>
/// <param name="HeldMoves">Pulses the gate held back because they would only take up play the offset did not need.</param>
/// <param name="LearnedWindows">Take-up windows learned from (older ones count less).</param>
/// <param name="LastCompensationMs">Compensation added to the last Dec pulse (ms).</param>
/// <param name="ShapeFromCalibration">True when a calibration measured a shape of the play.</param>
public sealed record PlayModelValues(
    double FullReversalMs,
    double SlackNorthMs,
    double SlackSouthMs,
    GuideDirection? Engaged,
    bool StateKnown,
    string Shape,
    double NoiseSigmaPx,
    int Reversals,
    int HeldMoves,
    double LearnedWindows,
    int LastCompensationMs,
    bool ShapeFromCalibration);

/// <summary>What a play model learned, for the host to store between sessions.</summary>
public sealed record PlayModelState
{
    public DateTimeOffset LearnedAt { get; init; }

    /// <summary>Weights of <see cref="PlayModelBacklash.BaseWidthsMs"/> a calibration measured; null when none did.</summary>
    public double[]? CalibratedWeights { get; init; }

    /// <summary>Log posterior over the model's hypotheses (relative; the maximum is 0), in the model's order.</summary>
    public double[] LogPosterior { get; init; } = [];

    public double LearnedWindows { get; init; }
}

/// <summary>
/// Dec backlash as a hysteresis state: the gear's position inside its play is tracked over every pulse, a pulse that first
/// has to take up play is made only when the offset clearly needs it (<see cref="ReversalGate"/>), and it is lengthened by
/// the play that is left on that side. See docs/notes/PLAY-MODEL-BACKLASH.md.
/// </summary>
/// <remarks>
/// <para><b>Model.</b> The motion of the star for a motor travel u (ms at the guide rate) is a Prandtl-Ishlinskii
/// superposition of play operators, y = Σ wᵢ·Fᵣᵢ[u] with Σ wᵢ = 1 (Brokate &amp; Sprekels 1996, "Hysteresis and Phase
/// Transitions", §2.3; inversion as in Kuhnen 2003, IEE Proc. Control Theory Appl. 150(5)). A narrow operator follows the
/// motor almost at once, a wide one only after it crossed its play; so a gear that wobbled inside its play after small
/// corrections loses little at the next reversal, one that was driven far loses much, and the take-up is soft when the
/// weights spread over several widths. Engaged on one side, a pulse moves the star as calibrated.</para>
/// <para><b>Uncertainty.</b> Neither the size nor the shape of the play is known, and the size may change over the night.
/// The model keeps a grid of hypotheses: no play, and every combination of a shape (hard: one width; medium; soft: widths
/// spread over a factor 20; the shape a calibration measured) with a size (the full reversal, 62 ms … 8 s). Each hypothesis
/// tracks its own play state; a posterior over them is updated from what the star did after each take-up of play (a
/// window) and tempered by <see cref="Forgetting"/> towards the prior so that it follows a changing play. A hard play that
/// is never crossed tells only that it is wider than the travel so far, and the posterior then keeps the prior above it.</para>
/// <para><b>Decisions.</b> A pulse of T ms that should move the star needs uₖ = Fₖ⁻¹(T) of motor time under hypothesis
/// k; the compensation added is the <see cref="CautionQuantile"/> quantile of uₖ − T over the posterior: an overshoot
/// costs another reversal with the whole play, an undershoot only leaves some error for the next frame, which the model
/// then knows still has play to take up. A pulse that has to take up play (a reversal, or the gear floating inside the play
/// after small corrections) is gated by <see cref="ReversalGate"/>: while the offset is noise, such pulses would only push
/// the gear around inside its play, or, compensated, feed the seeing into the mount.</para>
/// </remarks>
public sealed class PlayModelBacklash : IBacklashCompensation
{
    /// <summary>
    /// Widths of the play operators the shapes are made of (ms, before scaling to a size). A calibration's or a probe's fit
    /// is a mix of them, so the widest one bounds the size it can measure: up to 4000 ms the OnStepX's 5–8 s on pier East
    /// (2026-10-19) were all measured as 4 s, and the limit above the measured size kept the model below 4.8 s. A stored
    /// state with fewer weights (the first ten) gets zeros for the wider ones.
    /// </summary>
    public static readonly IReadOnlyList<double> BaseWidthsMs = [0, 100, 200, 400, 700, 1000, 1500, 2000, 3000, 4000, 6000, 8000];

    /// <summary>Sizes of the full reversal the hypotheses span (ms): 62.5 … 8000 in steps of 2^(1/4).</summary>
    public static readonly IReadOnlyList<double> SizesMs = Enumerable.Range(0, 29).Select(k => 62.5 * Math.Pow(2, k / 4.0)).ToArray();

    /// <summary>The shapes every size is tried with (weights of <see cref="BaseWidthsMs"/>).</summary>
    public static readonly IReadOnlyList<(string Name, double[] Weights)> Shapes =
    [
        ("Hard", [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]),
        ("Medium", [0, 0, 0, 0, 1 / 3.0, 1 / 3.0, 1 / 3.0, 0, 0, 0, 0, 0]),
        ("Soft", [0, 1 / 7.0, 1 / 7.0, 1 / 7.0, 1 / 7.0, 1 / 7.0, 1 / 7.0, 1 / 7.0, 0, 0, 0, 0]),
    ];

    /// <summary>Prior probability of the no-play hypothesis.</summary>
    public const double NoPlayPrior = 0.1;

    /// <summary>Prior share of the shape a calibration measured (the built-in shapes share the rest).</summary>
    public const double CalibratedShapePrior = 0.6;

    /// <summary>Default of <see cref="Caution"/>.</summary>
    public const double CautionQuantile = 0.3;

    /// <summary>Weight the evidence of older take-up windows keeps per new window (a memory of about 20 take-ups).</summary>
    public const double Forgetting = 0.95;

    /// <summary>Frames after a take-up over which the star's motion is measured.</summary>
    public const int WindowFrames = 6;

    /// <summary>Width σ of the prior over the size (natural log).</summary>
    public const double PriorLogSigma = 0.9;

    /// <summary>Width σ of the prior over the size around a calibration-measured size (natural log).</summary>
    public const double PriorLogSigmaCalibrated = 0.5;

    /// <summary>Drift and wander allowance of a take-up window per frame (px), added to the measurement noise.</summary>
    public const double WanderPxPerFrame = 0.03;

    /// <summary>Below this much play left (ms) in its direction a pulse is not gated: the gear is engaged that way.</summary>
    public const double GateMinSlackMs = 60;

    /// <summary>
    /// Weight of a window opened at a reversal where the model expected no play (a full window weighs 1). Such a window is
    /// there to notice a play that appears; at full weight, the windows of a mount without play, whose seeing cannot tell
    /// no play from about 100 ms, kept the mean play around <see cref="GateMinSlackMs"/>, the gate held back about every
    /// other pulse, and the Dec RMS was 7 % worse than without compensation (docs/notes/PLAY-MODEL-BACKLASH.md).
    /// </summary>
    public const double ProbeWindowWeight = 0.1;

    /// <summary>Largest compensation added to one pulse (ms); the rest is taken up by the next pulses.</summary>
    public const int MaxCompensationMs = 8000;

    /// <summary>
    /// How far above a measured size (a calibration's or a probe's) the learned size may go, about one step of
    /// <see cref="SizesMs"/> (<see cref="SizeLimitMs"/>).
    /// A soft play that guiding never crosses tells only that it is wider than the travel so far, and runs of pulses one way
    /// pushed the posterior to the top of the grid (8 s against 4 s measured on the OnStepX of 2026-10-18: above 4.5 s half
    /// the reversals overshot, against 12 % below, and the Dec RMS doubled); a new measurement (the probe after a slew) moves
    /// the limit. Smaller sizes are not limited: an undershoot is cheap.
    /// </summary>
    public const double MaxGrowthOverMeasured = 1.2;

    private readonly ReversalGate gate;
    private Hypothesis[] hypotheses = [];
    private double[] logPrior = [];
    private double[] logPosterior = [];
    private bool[] aboveLimit = [];
    private double[]? calibratedWeights;
    private bool stateKnown;
    private GuideDirection? lastDirection;
    private double lastRawOffset = double.NaN;
    private double yRatePxPerMs = double.NaN;
    private double expectedCorrectionPx;
    private Window? window;

    /// <param name="priorFullReversalMs">
    /// Prior guess of the play a full reversal takes up (ms), e.g. the "Dec backlash pulse" setting or a Coach measurement;
    /// ≤ 0 for a guess of 500 ms.
    /// </param>
    /// <param name="enabled">Whether the compensation is on.</param>
    /// <param name="limiter">Optional limiter; a compensation never makes a pulse exceed its Dec max duration.</param>
    /// <param name="gate">The gate for pulses that take up play; null for the default.</param>
    public PlayModelBacklash(double priorFullReversalMs = 0, bool enabled = true, PulseLimiter? limiter = null, ReversalGate? gate = null)
    {
        this.gate = gate ?? new ReversalGate();
        Limiter = limiter;
        IsEnabled = enabled;
        PriorFullReversalMs = priorFullReversalMs > 0 ? priorFullReversalMs : 500;
        Build(null);
        Array.Copy(logPrior, logPosterior, logPrior.Length);
    }

    public bool IsEnabled { get; private set; }

    /// <summary>Optional limiter: a compensation never makes a pulse exceed its Dec max duration.</summary>
    public PulseLimiter? Limiter { get; set; }

    /// <summary>Take-up windows learned from since construction (each changes <see cref="State"/>).</summary>
    public int LearnCount { get; private set; }

    /// <summary>Prior guess of the full reversal's play (ms).</summary>
    public double PriorFullReversalMs { get; private set; }

    /// <summary>True when a calibration measured a shape of the play (<see cref="SeedFromCalibration"/>).</summary>
    public bool ShapeFromCalibration => calibratedWeights is not null;

    /// <summary>
    /// Largest size the posterior may learn (ms): the size of the grid nearest to <see cref="MaxGrowthOverMeasured"/> × a
    /// measured size (so 1.09–1.30 × it, never without a size above it), else no limit.
    /// </summary>
    public double SizeLimitMs => ShapeFromCalibration ? LimitAbove(PriorFullReversalMs) : double.PositiveInfinity;

    public GuideDirection? LastDirection => lastDirection;

    /// <summary>Dec reversals made since construction.</summary>
    public int Reversals { get; private set; }

    /// <summary>Pulses held back by the gate since construction.</summary>
    public int HeldMoves { get; private set; }

    /// <summary>Take-up windows behind the posterior, each weighted by the forgetting since.</summary>
    public double LearnedWindows { get; private set; }

    /// <summary>Compensation added to the last Dec pulse (ms).</summary>
    public int LastCompensationMs { get; private set; }

    /// <summary>Quantile of the needed compensation over the posterior that is added (below the median: undershoot).</summary>
    public double Caution { get; init; } = CautionQuantile;

    /// <summary>When false, no pulse is held back: the play is tracked and compensated only.</summary>
    public bool GateEnabled { get; set; } = true;

    /// <summary>The gate for pulses that take up play (noise estimate and evidence).</summary>
    public ReversalGate Gate => gate;

    /// <summary>Number of hypotheses (the length of <see cref="PlayModelState.LogPosterior"/>).</summary>
    public int HypothesisCount => hypotheses.Length;

    public int PulseWidthMs
    {
        get
        {
            var p = Posterior();
            double slack = lastDirection is { } d && stateKnown ? Mean(p, h => h.Slack(Opposite(d))) : Mean(p, h => h.SizeMs);
            return (int)Math.Round(slack);
        }
    }

    /// <summary>
    /// Switches the compensation on or off. The gear is tracked while it is off too (<see cref="PulseSent"/> and
    /// <see cref="TrackResults"/> run either way), so switching it on keeps the gear's position; a host that did not feed
    /// the model in between calls <see cref="ResetState"/>. Before, switching it on forgot the position, and the first pulse
    /// got half the play without the gate (a 0.15 px offset sent 947 ms on the OnStepX of 2026-10-18, 4″ the other way).
    /// </summary>
    public void Enable(bool enable) => IsEnabled = enable;

    /// <summary>A new prior guess of the full reversal (e.g. the setting changed); what was learned is kept.</summary>
    public void SetPriorFullReversal(double fullReversalMs)
    {
        if (fullReversalMs > 0 && Math.Abs(fullReversalMs - PriorFullReversalMs) > 0.5 && !ShapeFromCalibration)
        {
            PriorFullReversalMs = fullReversalMs;
            var evidence = Evidence();
            Build(calibratedWeights);
            ApplyEvidence(evidence);
        }
    }

    /// <summary>The model's values now.</summary>
    public PlayModelValues Values
    {
        get
        {
            var p = Posterior();
            double north = stateKnown ? Mean(p, h => h.Slack(GuideDirection.North)) : double.NaN;
            double south = stateKnown ? Mean(p, h => h.Slack(GuideDirection.South)) : double.NaN;
            double full = Mean(p, h => h.SizeMs);
            GuideDirection? engaged = null;
            if (stateKnown && lastDirection is { } d && Mean(p, h => h.Slack(d)) < Math.Max(GateMinSlackMs, 0.05 * full))
            {
                engaged = d;
            }

            var shapeProbability = new Dictionary<string, double>();
            for (int k = 0; k < hypotheses.Length; k++)
            {
                shapeProbability[hypotheses[k].Shape] = shapeProbability.GetValueOrDefault(hypotheses[k].Shape) + p[k];
            }

            string shape = shapeProbability.MaxBy(kv => kv.Value).Key;
            return new PlayModelValues(full, north, south, engaged, stateKnown, shape, gate.NoiseSigmaPx, Reversals, HeldMoves, LearnedWindows,
                LastCompensationMs, ShapeFromCalibration);
        }
    }

    /// <summary>What was learned, for the host to store.</summary>
    public PlayModelState State(DateTimeOffset now)
    {
        double max = logPosterior.Max();
        return new PlayModelState
        {
            LearnedAt = now,
            CalibratedWeights = calibratedWeights is null ? null : [.. calibratedWeights],
            LogPosterior = logPosterior.Select(v => v - max).ToArray(),
            LearnedWindows = LearnedWindows,
        };
    }

    /// <summary>Restores what a previous session learned; an invalid state is ignored. The gear's position is not restored.</summary>
    public bool Restore(PlayModelState? stored)
    {
        if (stored is null || !ValidWeights(stored.CalibratedWeights, allowNull: true))
        {
            return false;
        }

        var weights = stored.CalibratedWeights is { } w ? Normalised(Padded(w)) : null;
        int expected = 1 + SizesMs.Count * (Shapes.Count + (weights is null ? 0 : 1));
        if (stored.LogPosterior.Length != expected || stored.LogPosterior.Any(double.IsNaN) || !stored.LogPosterior.Any(double.IsFinite))
        {
            return false;
        }

        if (weights is not null)
        {
            // the size a calibration measured is the calibrated shape's mean width (SeedFromCalibration)
            PriorFullReversalMs = Math.Max(1, weights.Select((w, i) => w * BaseWidthsMs[i]).Sum());
        }

        Build(weights);
        for (int k = 0; k < logPosterior.Length; k++)
        {
            logPosterior[k] = double.IsFinite(stored.LogPosterior[k]) ? stored.LogPosterior[k] : -1e6;
        }

        ExcludeAboveLimit();
        LearnedWindows = Math.Max(0, stored.LearnedWindows);
        ResetState();
        return true;
    }

    /// <summary>
    /// Measures the shape of the play from a calibration's Dec return: the points after the North leg (gear engaged North)
    /// as South pulse time and star motion since the reversal. The weights are fitted by least squares on the simplex
    /// (projected gradient; Duchi et al. 2008, "Efficient projections onto the l1-ball", ICML) and become a shape of their
    /// own with most of the prior, centred on the fitted size; what was learned before is forgotten. Returns false when the
    /// points do not tell (fewer than three, no motion, invalid rate).
    /// </summary>
    public bool SeedFromCalibration(IReadOnlyList<DecTakeUpPoint> points, double yRatePxPerMs)
    {
        if (!(yRatePxPerMs > 0) || !double.IsFinite(yRatePxPerMs))
        {
            return false;
        }

        var usable = points.Where(p => p.PulseMs > 0 && double.IsFinite(p.PulseMs) && double.IsFinite(p.MovedPx)).OrderBy(p => p.PulseMs).ToList();
        if (usable.Count < 3 || usable[^1].MovedPx <= 0)
        {
            return false;
        }

        // from a gear engaged the other way the play of operator i is its whole width. A width the longest pulse did not get
        // past moves nothing in the points and would only stand in for a lower rate (a probe at half the calibrated rate
        // measured a 1.1 s play as 1.6 s with it): the fit takes the widths up to FitWidthMs and the wider ones the return
        // got past
        double longest = usable[^1].PulseMs;
        int n = BaseWidthsMs.Count(r => r <= FitWidthMs || r < longest);
        var a = usable.Select(p => BaseWidthsMs.Take(n).Select(r => Math.Max(0, p.PulseMs - r)).ToArray()).ToArray();
        var y = usable.Select(p => p.MovedPx / yRatePxPerMs).ToArray();
        if (FitOnSimplex(a, y, n) is not { } fitted)
        {
            return false;
        }

        var w = Padded(fitted);

        double size = Enumerable.Range(0, w.Length).Sum(i => w[i] * BaseWidthsMs[i]);
        PriorFullReversalMs = Math.Max(1, size);
        Build(size > 1 ? w : null);
        Array.Copy(logPrior, logPosterior, logPrior.Length);
        LearnedWindows = 0;
        ResetState();
        return true;
    }

    public void TrackResults(MoveOptions options, double yRawOffset, double decMinMove, double yRate, DateTimeOffset now = default)
    {
        if (double.IsFinite(yRate) && yRate > 0)
        {
            yRatePxPerMs = yRate;
        }

        if (!double.IsFinite(yRawOffset))
        {
            return;
        }

        gate.Observe(yRawOffset, expectedCorrectionPx);
        expectedCorrectionPx = 0;
        lastRawOffset = yRawOffset;

        if (window is { } w)
        {
            if ((options & MoveOptions.AlgoResult) == 0)
            {
                // a recovery or deduced move: the lock position or the star may have jumped
                window = null;
            }
            else
            {
                w.Points.Add((yRawOffset, w.Pulses.Count));
                if (w.Points.Count >= WindowFrames)
                {
                    Learn(w);
                    window = null;
                }
            }
        }
    }

    public bool HoldMove(MoveOptions options, double yGuideDistance, double yRate, DateTimeOffset now = default)
    {
        // also while the gear's position is unknown (after a stop, a Dec guide mode change or a pulse not sent): the play is
        // then assumed centred, and the first pulse either way would get half of it on a noise offset (947 ms for 0.15 px on
        // the OnStepX of 2026-10-18)
        if (!IsEnabled || !GateEnabled || (options & MoveOptions.AlgoResult) == 0 || yGuideDistance == 0.0 || !double.IsFinite(yGuideDistance))
        {
            return false;
        }

        // a pulse the gear follows at once is guiding as usual; one that first has to take up play (a reversal, or the gear
        // floating inside the play) is made only when the offset needs it. Every such pulse needs the evidence on its own:
        // with a soft play the gear engages gradually, and letting the pulses that complete a take-up through unchecked
        // would chase the seeing with compensated pulses again
        var direction = yGuideDistance > 0 ? GuideDirection.South : GuideDirection.North;
        // (letting partly engaged pulses through by their expected effect was tried: it chases the seeing again and makes a
        // hard play worse, docs/notes/PLAY-MODEL-BACKLASH.md)
        if (Mean(Posterior(), h => h.Slack(direction)) < GateMinSlackMs || gate.Allows(direction == GuideDirection.South ? 1 : -1))
        {
            return false;
        }

        HeldMoves++;
        return true;
    }

    public int Apply(MoveOptions options, double yGuideDistance, ref int yAmountMs, DateTimeOffset now = default)
    {
        LastCompensationMs = 0;
        if (!IsEnabled || (options & MoveOptions.UseBlc) == 0 || yAmountMs <= 0 || yGuideDistance == 0.0)
        {
            return 0;
        }

        var direction = yGuideDistance > 0 ? GuideDirection.South : GuideDirection.North;
        int requested = yAmountMs;
        var needed = hypotheses.Select(h => h.Inverse(direction, requested) - requested).ToArray();
        double comp = WeightedQuantile(needed, Posterior(), Caution);
        int max = MaxCompensationMs;
        if (Limiter is { } limiter)
        {
            max = Math.Min(max, Math.Max(0, limiter.MaxDecDurationMs - requested));
        }

        int added = (int)Math.Clamp(Math.Floor(comp + 0.5), 0, max);
        yAmountMs += added;
        LastCompensationMs = added;
        return added;
    }

    /// <summary>
    /// What the pulse moves the star by, as told to the guide algorithm (<see cref="AxisCorrector"/>). While the
    /// compensation is off this is the pulse as sent, as with the fixed compensation switched off: the model still tracks
    /// the gear (<see cref="ExpectedMs"/>), but does not change the guiding.
    /// </summary>
    public double EffectiveMs(GuideDirection direction, int durationMs, int compensationMs)
    {
        return IsEnabled ? ExpectedMs(direction, durationMs, compensationMs) : Math.Max(0, durationMs - compensationMs);
    }

    // what the model expects the pulse to move the star by, also while the compensation is off
    private double ExpectedMs(GuideDirection direction, int durationMs, int compensationMs)
    {
        if (durationMs <= 0)
        {
            return 0;
        }

        if (!stateKnown)
        {
            // nothing known about the gear: the compensation is assumed to take up slack, as with the fixed pulse
            return Math.Max(0, durationMs - compensationMs);
        }

        return Mean(Posterior(), h => h.Forward(direction, durationMs));
    }

    public void PulseSent(GuideDirection direction, int durationMs)
    {
        if (direction.Axis() != GuideAxis.Dec || durationMs <= 0)
        {
            return;
        }

        int sign = direction == GuideDirection.South ? 1 : -1;
        if (double.IsFinite(yRatePxPerMs))
        {
            expectedCorrectionPx += sign * ExpectedMs(direction, durationMs, LastCompensationMs) * yRatePxPerMs;
        }

        bool reversal = lastDirection is { } last && last != direction;
        if (reversal)
        {
            Reversals++;
        }

        if (window is { } open && open.Direction != direction)
        {
            // a reversal before the window completed: learn from what it saw up to the last frame
            Learn(open);
            window = null;
        }

        // a pulse that takes up play opens a window: what the star does next tells how much play there was. Every reversal
        // does, also when the model expects no play there: else a model sure of no play never measures a play that appears.
        // Such a window weighs ProbeWindowWeight
        if (window is null && double.IsFinite(lastRawOffset) && double.IsFinite(yRatePxPerMs))
        {
            bool expectsPlay = Mean(Posterior(), h => h.Slack(direction)) >= GateMinSlackMs;
            if (expectsPlay || reversal)
            {
                var start = hypotheses.Select(h => h.Snapshot()).ToArray();
                window = new Window(direction, lastRawOffset, start, expectsPlay ? 1 : ProbeWindowWeight);
            }
        }

        window?.Pulses.Add((sign, durationMs));
        foreach (var h in hypotheses)
        {
            h.Move(sign * (double)durationMs);
        }

        stateKnown = true;
        lastDirection = direction;
    }

    public void GearEngaged(GuideDirection direction)
    {
        if (direction.Axis() != GuideAxis.Dec)
        {
            return;
        }

        foreach (var h in hypotheses)
        {
            h.Engage(direction == GuideDirection.South ? 1 : -1);
        }

        stateKnown = true;
        lastDirection = direction;
        window = null;
        gate.ResetEvidence();
    }

    /// <summary>
    /// Forgets what was learned: the posterior goes back to the prior, which keeps a calibration's shape and size. For a
    /// play that may differ by far now, e.g. on the other pier side (OnStepX 2026-10-19: no play learned on pier West,
    /// 4.6 s on pier East, where the model kept the West's "none" and learned nothing in eight minutes).
    /// </summary>
    public void ForgetLearned()
    {
        Array.Copy(logPrior, logPosterior, logPrior.Length);
        LearnedWindows = 0;
        LearnCount++;
    }

    public void ResetState()
    {
        foreach (var h in hypotheses)
        {
            h.Centre();
        }

        stateKnown = false;
        lastDirection = null;
        window = null;
        expectedCorrectionPx = 0;
        lastRawOffset = double.NaN;
        gate.ResetEvidence();
    }

    // ---- hypotheses and prior ----

    private void Build(double[]? calibrated)
    {
        calibratedWeights = calibrated;
        var shapes = Shapes.Select(s => (s.Name, s.Weights, Prior: (calibrated is null ? 1.0 : 1 - CalibratedShapePrior) / Shapes.Count)).ToList();
        if (calibrated is not null)
        {
            shapes.Add(("Calibrated", calibrated, CalibratedShapePrior));
        }

        double logSigma = calibrated is null ? PriorLogSigma : PriorLogSigmaCalibrated;
        var sizeDensity = SizesMs.Select(s => Math.Exp(-0.5 * Math.Pow((Math.Log(s) - Math.Log(PriorFullReversalMs)) / logSigma, 2))).ToArray();
        double sizeSum = sizeDensity.Sum();

        var list = new List<Hypothesis> { Hypothesis.NoPlay() };
        var prior = new List<double> { NoPlayPrior };
        foreach (var (name, weights, shapePrior) in shapes)
        {
            for (int k = 0; k < SizesMs.Count; k++)
            {
                list.Add(new Hypothesis(name, weights, SizesMs[k]));
                prior.Add((1 - NoPlayPrior) * shapePrior * sizeDensity[k] / sizeSum);
            }
        }

        hypotheses = [.. list];
        double limit = calibrated is null ? double.PositiveInfinity : LimitAbove(PriorFullReversalMs);
        aboveLimit = hypotheses.Select(h => h.SizeMs > limit * (1 + 1e-9)).ToArray();
        logPrior = prior.Select(p => Math.Log(Math.Max(p, 1e-300))).ToArray();
        logPosterior = (double[])logPrior.Clone();
        ExcludeAboveLimit();
        stateKnown = false;
        lastDirection = null;
        window = null;
    }

    // the evidence of the windows learned so far, per (shape, size) key: posterior minus prior
    private Dictionary<(string, double), double> Evidence()
    {
        var evidence = new Dictionary<(string, double), double>();
        for (int k = 0; k < hypotheses.Length; k++)
        {
            evidence[(hypotheses[k].Shape, hypotheses[k].SizeMs)] = logPosterior[k] - logPrior[k];
        }

        return evidence;
    }

    private void ApplyEvidence(Dictionary<(string, double), double> evidence)
    {
        for (int k = 0; k < hypotheses.Length; k++)
        {
            logPosterior[k] = logPrior[k] + evidence.GetValueOrDefault((hypotheses[k].Shape, hypotheses[k].SizeMs));
        }

        ExcludeAboveLimit();
    }

    // the grid's steps are 19 %: a limit of exactly 1.2 × the measured size could leave no size above the measured one
    private static double LimitAbove(double measuredMs)
    {
        double target = Math.Log(MaxGrowthOverMeasured * measuredMs);
        return SizesMs.MinBy(s => Math.Abs(Math.Log(s) - target));
    }

    // the sizes above SizeLimitMs keep no weight, whatever the evidence: a small prior alone would be outgrown by a few
    // windows that fit a wider play better
    private void ExcludeAboveLimit()
    {
        for (int k = 0; k < logPosterior.Length; k++)
        {
            if (aboveLimit[k])
            {
                logPosterior[k] = -1e6;
            }
        }
    }

    private double[] Posterior()
    {
        double max = logPosterior.Max();
        var p = logPosterior.Select(v => Math.Exp(v - max)).ToArray();
        double sum = p.Sum();
        for (int k = 0; k < p.Length; k++)
        {
            p[k] /= sum;
        }

        return p;
    }

    private double Mean(double[] probabilities, Func<Hypothesis, double> value)
    {
        double sum = 0;
        for (int k = 0; k < probabilities.Length; k++)
        {
            if (probabilities[k] > 1e-9)
            {
                sum += probabilities[k] * value(hypotheses[k]);
            }
        }

        return sum;
    }

    // ---- learning ----

    // Every frame of the window is a measurement of the motion since its start: rⱼ = observedⱼ − predictedⱼ, with the
    // covariance of white noise and wander below; log L = −½ rᵀ C⁻¹ r (Cholesky). The frames in between tell a soft take-up
    // from a hard one, which the total alone cannot.
    private void Learn(Window w)
    {
        int n = w.Points.Count;
        if (n < 1 || !double.IsFinite(yRatePxPerMs))
        {
            return;
        }

        int sign = w.Direction == GuideDirection.South ? 1 : -1;
        var observed = w.Points.Select(p => sign * (w.StartOffset - p.Offset) / yRatePxPerMs).ToArray();

        // covariance of the residuals (ms²): the white noise of each frame with the start's common to all, σ_w²(I + 11ᵀ),
        // and the wander since the start, a random walk q·min(i, j); on a real night the second dominates (the Dec offset
        // of the 2026-10-12 log had a lag-1 autocorrelation of 0.84)
        var (white, wander) = gate.NoiseComponents;
        white = white > 0 ? white : 0.01;
        wander = Math.Max(wander, WanderPxPerFrame * WanderPxPerFrame);
        double scale = 1 / (yRatePxPerMs * yRatePxPerMs);
        var covariance = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                covariance[i, j] = scale * (white * ((i == j ? 1 : 0) + 1) + wander * (Math.Min(i, j) + 1));
            }
        }

        if (!Cholesky(covariance, n))
        {
            return;
        }

        // a window of weight w counts as that share of a window: its likelihood tempered by w, the older evidence forgotten by
        // Forgetting^w
        double forgetting = Math.Pow(Forgetting, w.Weight);
        for (int k = 0; k < hypotheses.Length; k++)
        {
            var state = (double[])w.Start[k].Clone();
            double predicted = 0;
            int sent = 0;
            var residuals = new double[n];
            for (int j = 0; j < n; j++)
            {
                for (; sent < w.Points[j].Pulses; sent++)
                {
                    var (s, ms) = w.Pulses[sent];
                    predicted += s * sign * hypotheses[k].Forward(state, s, ms);
                    hypotheses[k].Move(state, s * (double)ms);
                }

                residuals[j] = observed[j] - predicted;
            }

            double logLikelihood = -0.5 * MahalanobisSquared(covariance, residuals, n);
            logPosterior[k] = logPrior[k] + forgetting * (logPosterior[k] - logPrior[k]) + w.Weight * logLikelihood;
        }

        ExcludeAboveLimit();
        LearnedWindows = forgetting * LearnedWindows + w.Weight;
        LearnCount++;
        double max = logPosterior.Max();
        for (int k = 0; k < logPosterior.Length; k++)
        {
            logPosterior[k] -= max;
        }
    }

    // ---- helpers ----

    // in place: the lower triangle becomes L with A = L·Lᵀ; false when A is not positive definite
    private static bool Cholesky(double[,] a, int n)
    {
        for (int j = 0; j < n; j++)
        {
            double d = a[j, j];
            for (int k = 0; k < j; k++)
            {
                d -= a[j, k] * a[j, k];
            }

            if (!(d > 0))
            {
                return false;
            }

            a[j, j] = Math.Sqrt(d);
            for (int i = j + 1; i < n; i++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++)
                {
                    s -= a[i, k] * a[j, k];
                }

                a[i, j] = s / a[j, j];
            }
        }

        return true;
    }

    // rᵀ A⁻¹ r with A = L·Lᵀ from Cholesky: |L⁻¹ r|²
    private static double MahalanobisSquared(double[,] l, double[] r, int n)
    {
        var z = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double s = r[i];
            for (int k = 0; k < i; k++)
            {
                s -= l[i, k] * z[k];
            }

            z[i] = s / l[i, i];
            sum += z[i] * z[i];
        }

        return sum;
    }

    private static double WeightedQuantile(double[] values, double[] weights, double q)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        double cumulative = 0;
        foreach (int i in order)
        {
            cumulative += weights[i];
            if (cumulative >= q)
            {
                return values[i];
            }
        }

        return values[order[^1]];
    }

    private static GuideDirection Opposite(GuideDirection d) => d == GuideDirection.North ? GuideDirection.South : GuideDirection.North;

    private static bool ValidWeights(double[]? w, bool allowNull) =>
        w is null ? allowNull : w.Length >= StoredWeightsBefore6000Ms && w.Length <= BaseWidthsMs.Count && w.All(v => v >= 0 && double.IsFinite(v)) && w.Sum() > 0.5;

    // states stored before the 6000 and 8000 ms widths have weights for the first ten
    private const int StoredWeightsBefore6000Ms = 10;

    // the widest operator a calibration's or a probe's fit always uses (the widest one before 6000 and 8000 ms were added)
    private const double FitWidthMs = 4000;

    private static double[] Padded(double[] w) => w.Length == BaseWidthsMs.Count ? w : [.. w, .. new double[BaseWidthsMs.Count - w.Length]];

    private static double[] Normalised(double[] w)
    {
        double sum = w.Sum();
        return w.Select(v => v / sum).ToArray();
    }

    // least squares min ‖A·w − y‖² over the simplex (w ≥ 0, Σ w = 1) by projected gradient with a fixed step 1/L
    private static double[]? FitOnSimplex(double[][] a, double[] y, int n)
    {
        double lipschitz = 0;
        foreach (var row in a)
        {
            lipschitz += row.Sum(v => v * v);
        }

        if (!(lipschitz > 0) || !double.IsFinite(lipschitz))
        {
            return null;
        }

        var w = Enumerable.Repeat(1.0 / n, n).ToArray();
        var grad = new double[n];
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            Array.Clear(grad);
            for (int r = 0; r < a.Length; r++)
            {
                double residual = -y[r];
                for (int i = 0; i < n; i++)
                {
                    residual += a[r][i] * w[i];
                }

                for (int i = 0; i < n; i++)
                {
                    grad[i] += 2 * a[r][i] * residual;
                }
            }

            for (int i = 0; i < n; i++)
            {
                w[i] -= grad[i] / (2 * lipschitz);
            }

            ProjectOnSimplex(w);
        }

        return w.All(double.IsFinite) ? w : null;
    }

    // Euclidean projection onto { w ≥ 0, Σ w = 1 } (Duchi et al. 2008, Fig. 1)
    private static void ProjectOnSimplex(double[] w)
    {
        var sorted = w.OrderByDescending(v => v).ToArray();
        double cumulative = 0;
        double theta = 0;
        for (int j = 0; j < sorted.Length; j++)
        {
            cumulative += sorted[j];
            double t = (cumulative - 1) / (j + 1);
            if (sorted[j] - t > 0)
            {
                theta = t;
            }
        }

        for (int i = 0; i < w.Length; i++)
        {
            w[i] = Math.Max(0, w[i] - theta);
        }
    }

    /// <summary>One play hypothesis: a shape scaled to a size, with its own gear position.</summary>
    private sealed class Hypothesis
    {
        private readonly double[] weights;
        private readonly double[] widths;
        private readonly double[] play; // motor position relative to each operator's centre, ±width/2

        public Hypothesis(string shape, double[] shapeWeights, double sizeMs)
        {
            Shape = shape;
            SizeMs = sizeMs;
            double mean = 0;
            for (int i = 0; i < shapeWeights.Length; i++)
            {
                mean += shapeWeights[i] * BaseWidthsMs[i];
            }

            // the widths are scaled so that the full reversal (the weighted mean width) is sizeMs
            double scale = mean > 0 ? sizeMs / mean : 0;
            weights = [.. shapeWeights];
            widths = BaseWidthsMs.Select(r => r * scale).ToArray();
            play = new double[widths.Length];
        }

        public static Hypothesis NoPlay() => new("None", [1, .. new double[BaseWidthsMs.Count - 1]], 0);

        public string Shape { get; }

        public double SizeMs { get; }

        public double[] Snapshot() => (double[])play.Clone();

        public void Centre() => Array.Clear(play);

        public void Engage(int sign)
        {
            for (int i = 0; i < play.Length; i++)
            {
                play[i] = sign * widths[i] / 2;
            }
        }

        /// <summary>Weighted play left before the star follows a pulse in <paramref name="direction"/> fully (ms).</summary>
        public double Slack(GuideDirection direction)
        {
            int sign = direction == GuideDirection.South ? 1 : -1;
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i] * (widths[i] / 2 - sign * play[i]);
            }

            return sum;
        }

        /// <summary>Star motion (engaged-equivalent ms) of a motor travel in a direction from the current position.</summary>
        public double Forward(GuideDirection direction, double motorMs) => Forward(play, direction == GuideDirection.South ? 1 : -1, motorMs);

        public double Forward(double[] state, int sign, double motorMs)
        {
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                double slack = widths[i] / 2 - sign * state[i];
                sum += weights[i] * Math.Max(0, motorMs - slack);
            }

            return sum;
        }

        /// <summary>Motor time whose motion is <paramref name="targetMs"/> (the piecewise linear inverse of <see cref="Forward(GuideDirection, double)"/>).</summary>
        public double Inverse(GuideDirection direction, double targetMs)
        {
            int sign = direction == GuideDirection.South ? 1 : -1;
            var slacks = new (double Slack, double Weight)[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                slacks[i] = (Math.Max(0, widths[i] / 2 - sign * play[i]), weights[i]);
            }

            Array.Sort(slacks, (a, b) => a.Slack.CompareTo(b.Slack));

            // between consecutive slacks the motion grows with the summed weight of the operators already engaged
            double motion = 0;
            double slope = 0;
            double u = 0;
            foreach (var (slack, weight) in slacks)
            {
                if (slope > 0 && motion + slope * (slack - u) >= targetMs)
                {
                    return u + (targetMs - motion) / slope;
                }

                motion += slope * (slack - u);
                u = slack;
                slope += weight;
            }

            return slope > 0 ? u + (targetMs - motion) / slope : u + targetMs;
        }

        public void Move(double motorMs) => Move(play, motorMs);

        public void Move(double[] state, double motorMs)
        {
            for (int i = 0; i < state.Length; i++)
            {
                double half = widths[i] / 2;
                state[i] = Math.Clamp(state[i] + motorMs, -half, half);
            }
        }
    }

    private sealed class Window(GuideDirection direction, double startOffset, double[][] start, double weight)
    {
        public GuideDirection Direction { get; } = direction;

        /// <summary>How much the window counts: 1, or <see cref="ProbeWindowWeight"/>.</summary>
        public double Weight { get; } = weight;

        public double StartOffset { get; } = startOffset;

        /// <summary>Play state of every hypothesis before the take-up pulse.</summary>
        public double[][] Start { get; } = start;

        public List<(int Sign, int Ms)> Pulses { get; } = [];

        /// <summary>The frames measured since: their offset and how many of <see cref="Pulses"/> went out before them.</summary>
        public List<(double Offset, int Pulses)> Points { get; } = [];
    }
}
