// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/mount.cpp (Mount::MoveOffset, Notify*) and src/scope.cpp (Scope::MoveAxis) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// Result of one correction computation, carrying PHD2's <c>GuideStepInfo</c> move fields.
/// </summary>
public sealed record GuideCorrection
{
    public MoveOptions Options { get; init; }

    /// <summary>Mount-axis RA offset fed in (px); for deduced moves the deduced amount.</summary>
    public double RADistanceRaw { get; init; }

    /// <summary>Mount-axis Dec offset fed in (px); for deduced moves the deduced amount.</summary>
    public double DECDistanceRaw { get; init; }

    /// <summary>RA distance after the guide algorithm (px).</summary>
    public double RADistanceGuide { get; init; }

    /// <summary>Dec distance after the guide algorithm (px).</summary>
    public double DECDistanceGuide { get; init; }

    /// <summary>RA pulse actually issued (ms, after clamp). 0 = no pulse.</summary>
    public int RADuration { get; init; }

    /// <summary>RA direction: West when <see cref="RADistanceGuide"/> &gt; 0, else East.</summary>
    public GuideDirection RADirection { get; init; }

    /// <summary>Dec pulse actually issued (ms, incl. backlash comp, after mode filter and clamp).</summary>
    public int DECDuration { get; init; }

    /// <summary>Dec direction: South when <see cref="DECDistanceGuide"/> &gt; 0, else North.</summary>
    public GuideDirection DECDirection { get; init; }

    public bool RALimited { get; init; }

    public bool DecLimited { get; init; }

    /// <summary>Backlash compensation added to the requested Dec pulse (ms; Dec guide mode Auto only), before mode filter/clamp.</summary>
    public int BacklashCompMs { get; init; }

    /// <summary>True when the Dec guide mode suppressed a non-zero Dec request.</summary>
    public bool DecSuppressedByMode { get; init; }

    /// <summary>True when the backlash play model held back a Dec pulse that would only have taken up play (no Dec pulse).</summary>
    public bool DecHeldByBacklash { get; init; }

    /// <summary>
    /// Pulses to issue in order (RA first, then Dec); zero-duration axes omitted. Durations are the
    /// final clamped values.
    /// </summary>
    public IReadOnlyList<PulseCommand> Pulses { get; init; } = [];

    /// <summary>Limit-reached alerts raised by this step (0..2).</summary>
    public IReadOnlyList<PulseLimitAlert> Alerts { get; init; } = [];

    /// <summary>
    /// Estimated RA correction applied (px) with the sign of <see cref="RADistanceGuide"/>, i.e. the
    /// amount by which a positive offset is reduced: <c>±RADuration · xRate</c>.
    /// </summary>
    public double RaCorrectionPx { get; init; }

    /// <summary>
    /// Estimated Dec correction applied (px): <c>±max(0, DECDuration − BacklashCompMs) · yRate</c> with the fixed
    /// compensation (it is assumed to only take up slack), the motion the play model expects with it.
    /// </summary>
    public double DecCorrectionPx { get; init; }
}

/// <summary>
/// Correction pipeline for a mount (non-AO) guider: guide algorithms → minimum pulse → Dec backlash compensation (in
/// Auto) → Dec guide mode → max-duration clamp and limit alerts, following PHD2's
/// <c>Mount::MoveOffset</c> + <c>Scope::MoveAxis</c> order (the minimum pulse is not in PHD2). Computes pulses only;
/// issuing them is up to the caller.
/// </summary>
/// <remarks>
/// <para>Direction convention (identical to PHD2): inputs are the star's offset from the lock position
/// transformed to mount axes (PHD2 <c>mountOfs</c>, px). The mount X axis is the direction a star moves
/// under an <em>East</em> pulse (PHD2 calibrates <c>xAngle = start.Angle(end)</c> after West pulses,
/// i.e. from the end point back to the start), and the Y axis is the direction a star moves under a
/// <em>South</em> pulse. Hence a positive RA distance is corrected with a <b>West</b> pulse (PHD2
/// <c>LEFT</c>) and a negative one with East (<c>RIGHT</c>); a positive Dec distance is corrected with a
/// <b>South</b> pulse (<c>DOWN</c>) and a negative one with North (<c>UP</c>). A zero distance reports
/// East/North with duration 0.</para>
/// <para>Durations are <c>ROUND(|distance / rate|)</c> with <c>ROUND(x) = floor(x + 0.5)</c> and rates in
/// px/ms (RA rate already declination-compensated by the caller, as PHD2's <c>m_xRate</c>).</para>
/// </remarks>
public sealed class AxisCorrector
{
    public AxisCorrector(IGuideAlgorithm raAlgorithm, IGuideAlgorithm decAlgorithm, PulseLimiter? limiter = null, IBacklashCompensation? backlash = null)
    {
        RaAlgorithm = raAlgorithm;
        DecAlgorithm = decAlgorithm;
        Limiter = limiter ?? new PulseLimiter();
        Backlash = backlash ?? new BacklashCompensation(limiter: Limiter);
    }

    /// <summary>Creates a corrector with PHD2 defaults (Hysteresis RA, ResistSwitch Dec) and the given min-move.</summary>
    public static AxisCorrector CreateDefault(double minMove)
    {
        return new AxisCorrector(
            GuideAlgorithmFactory.Create(GuideAlgorithmFactory.DefaultRaAlgorithm, GuideAxis.Ra, minMove),
            GuideAlgorithmFactory.Create(GuideAlgorithmFactory.DefaultDecAlgorithm, GuideAxis.Dec, minMove));
    }

    public IGuideAlgorithm RaAlgorithm { get; set; }

    public IGuideAlgorithm DecAlgorithm { get; set; }

    public PulseLimiter Limiter { get; }

    public IBacklashCompensation Backlash { get; }

    private DecGuideMode decGuideMode = DecGuideMode.Auto;
    private GuideDirection? driftDecDirection;

    /// <summary>Dec guide mode. PHD2's mount default is Auto.</summary>
    public DecGuideMode DecGuideMode
    {
        get => decGuideMode;
        set
        {
            var was = EffectiveDecGuideMode;
            decGuideMode = value;
            EffectiveDecGuideModeChanged(was);
        }
    }

    // Deviation from PHD2: the guider rounds pulses under 20 ms by default; PHD2 sends any length (0).
    /// <summary>Default of <see cref="MinPulseMs"/> for the guider (the corrector itself defaults to 0, like PHD2).</summary>
    public const int DefaultMinPulseMs = 20;

    /// <summary>Upper bound of <see cref="MinPulseMs"/>: the smallest allowed max duration.</summary>
    public const int MaxMinPulseMs = PulseLimiter.MinMaxDurationMs;

    private int minPulseMs;

    /// <summary>
    /// Shortest pulse of an algorithm or deduced move (ms); 0 sends any length (PHD2). A shorter pulse is rounded to
    /// 0 or to the minimum, whichever is nearer, and the step reports what is sent. The rounding comes before the Dec
    /// backlash compensation: a request rounded to nothing is no reversal for it. Mount drivers drop or mishandle very
    /// short pulses (EQMod, for one, ignores pulses under 10 ms by default).
    /// </summary>
    public int MinPulseMs
    {
        get => minPulseMs;
        set => minPulseMs = Math.Clamp(value, 0, MaxMinPulseMs);
    }

    /// <summary>
    /// With <see cref="DecGuideMode.Drift"/>: the only Dec direction algorithm and deduced moves may use, null for both
    /// (set by the guider before each step). Ignored in the other modes.
    /// </summary>
    public GuideDirection? DriftDecDirection
    {
        get => driftDecDirection;
        set
        {
            var was = EffectiveDecGuideMode;
            driftDecDirection = value;
            EffectiveDecGuideModeChanged(was);
        }
    }

    /// <summary>
    /// The PHD2 mode the Dec moves are filtered with: Drift narrowed to its direction, the others as set. The Dec backlash
    /// compensation works in <see cref="DecGuideMode.Auto"/> only.
    /// </summary>
    public DecGuideMode EffectiveDecGuideMode => DecGuideMode != DecGuideMode.Drift ? DecGuideMode : DriftDecDirection switch
    {
        GuideDirection.North => DecGuideMode.North,
        GuideDirection.South => DecGuideMode.South,
        _ => DecGuideMode.Auto,
    };

    // PHD2 Scope::SetDecGuideMode: the backlash compensation is off while one direction is guided ("there's no recovery
    // from over-shoots") and starts over with the next mode, as when it is enabled again (ResetBLCState)
    private void EffectiveDecGuideModeChanged(DecGuideMode was)
    {
        if (EffectiveDecGuideMode != was)
        {
            Backlash.ResetState();
        }
    }

    /// <summary>
    /// When false, non-manual moves produce no pulses (algorithms still run and see the offsets), like
    /// PHD2's "guiding output disabled".
    /// </summary>
    public bool GuidingEnabled { get; private set; } = true;

    /// <summary>Enables/disables guide output (PHD2 <c>Mount::SetGuidingEnabled</c>).</summary>
    public void SetGuidingEnabled(bool enabled, DateTimeOffset now)
    {
        if (enabled == GuidingEnabled)
            return;
        GuidingEnabled = enabled;
        if (enabled)
        {
            Notify(a =>
            {
                if (a is GuideAlgorithmBase b)
                    b.GuidingEnabled();
                else
                    a.Reset();
            });

            // avoid sending false positive alerts after guiding is re-enabled
            Limiter.DeferAlertCheck(now);
        }
        else
        {
            Notify(a => (a as GuideAlgorithmBase)?.GuidingDisabled());
        }
    }

    /// <summary>Normal guide step: offsets go through the guide algorithms.</summary>
    public GuideCorrection GuideStep(double raDistancePx, double decDistancePx, double xRate, double yRate, DateTimeOffset now)
        => Move(raDistancePx, decDistancePx, xRate, yRate, MoveOptions.GuideStep, now)!;

    /// <summary>
    /// Dead-reckoning move from <see cref="IGuideAlgorithm.DeduceResult"/> (star lost / paused). Returns
    /// null when both algorithms deduce 0, as PHD2 then issues no move and logs no step.
    /// </summary>
    public GuideCorrection? DeducedStep(double xRate, double yRate, DateTimeOffset now)
        => Move(0, 0, xRate, yRate, MoveOptions.DeducedMove, now);

    /// <summary>
    /// General move (PHD2 <c>MoveOffset</c> with explicit options), e.g. <see cref="MoveOptions.RecoveryMove"/>
    /// for fast recenter. Returns null only for deduced moves with nothing to do.
    /// </summary>
    public GuideCorrection? Move(double raDistancePx, double decDistancePx, double xRate, double yRate, MoveOptions options, DateTimeOffset now)
    {
        double xDistance;
        double yDistance;
        double rawX;
        double rawY;

        if ((options & MoveOptions.AlgoDeduce) != 0)
        {
            xDistance = RaAlgorithm.DeduceResult();
            yDistance = DecAlgorithm.DeduceResult();
            if (xDistance == 0.0 && yDistance == 0.0)
                return null;
            rawX = xDistance;
            rawY = yDistance;
        }
        else
        {
            rawX = xDistance = raDistancePx;
            rawY = yDistance = decDistancePx;

            // Let BLC track the raw offsets in Dec
            Backlash.TrackResults(options, yDistance, DecAlgorithm.MinMove, yRate, now);

            if ((options & MoveOptions.AlgoResult) != 0)
            {
                // Feed the raw distances to the guide algorithms
                xDistance = RaAlgorithm.Result(xDistance, now);
                yDistance = DecAlgorithm.Result(yDistance, now);
            }
        }

        // Figure out the guide directions based on the (possibly) updated distances
        GuideDirection xDirection = xDistance > 0.0 ? GuideDirection.West : GuideDirection.East;
        GuideDirection yDirection = yDistance > 0.0 ? GuideDirection.South : GuideDirection.North;

        var alerts = new List<PulseLimitAlert>(2);

        int requestedXAmount = MinimumPulse(DurationFor(xDistance, xRate), options);
        var x = MoveAxis(xDirection, requestedXAmount, options, now, alerts, out _);

        // Deviation from PHD2: the minimum pulse comes before the backlash compensation, and a request it rounds to nothing
        // is no reversal. Rounded after it, a short reversal could be dropped whole, compensation included, after the
        // compensation had counted it, and the next pulse that way went out without compensation. As in PHD2 the
        // compensation works in Auto only (see EffectiveDecGuideModeChanged).
        // the play model holds back a pulse that would only take up play the offset does not need (Auto only, as the compensation)
        bool decHeld = EffectiveDecGuideMode == DecGuideMode.Auto && yDistance != 0.0 && Backlash.HoldMove(options, yDistance, yRate, now);
        int requestedYAmount = decHeld ? 0 : MinimumPulse(DurationFor(yDistance, yRate), options);
        int blc = !decHeld && EffectiveDecGuideMode == DecGuideMode.Auto && (requestedYAmount > 0 || minPulseMs == 0)
            ? Backlash.Apply(options, yDistance, ref requestedYAmount, now)
            : 0;
        var y = MoveAxis(yDirection, requestedYAmount, options, now, alerts, out bool decSuppressed);

        // the gear moves with every Dec pulse; a recovery pulse is clamped to the max duration by the caller
        bool algoOrDeduce = (options & (MoveOptions.AlgoResult | MoveOptions.AlgoDeduce)) != 0;
        int decSentMs = algoOrDeduce ? y.DurationMs : Math.Min(y.DurationMs, Limiter.MaxDecDurationMs);
        double decEffectiveMs = y.DurationMs > 0 ? Backlash.EffectiveMs(yDirection, y.DurationMs, blc) : 0;
        if (decSentMs > 0)
        {
            Backlash.PulseSent(yDirection, decSentMs);
        }

        var pulses = new List<PulseCommand>(2);
        if (x.DurationMs > 0)
            pulses.Add(new PulseCommand(xDirection, x.DurationMs));
        if (y.DurationMs > 0)
            pulses.Add(new PulseCommand(yDirection, y.DurationMs));

        double raSign = xDirection == GuideDirection.West ? 1.0 : -1.0;
        double decSign = yDirection == GuideDirection.South ? 1.0 : -1.0;
        double raCorr = ValidRate(xRate) ? raSign * x.DurationMs * xRate : 0.0;
        double decCorr = ValidRate(yRate) ? decSign * decEffectiveMs * yRate : 0.0;

        return new GuideCorrection
        {
            Options = options,
            RADistanceRaw = rawX,
            DECDistanceRaw = rawY,
            RADistanceGuide = xDistance,
            DECDistanceGuide = yDistance,
            RADuration = x.DurationMs,
            RADirection = xDirection,
            DECDuration = y.DurationMs,
            DECDirection = yDirection,
            RALimited = x.Limited,
            DecLimited = y.Limited,
            BacklashCompMs = blc,
            DecSuppressedByMode = decSuppressed,
            DecHeldByBacklash = decHeld,
            Pulses = pulses,
            Alerts = alerts,
            RaCorrectionPx = raCorr == 0.0 ? 0.0 : raCorr,
            DecCorrectionPx = decCorr == 0.0 ? 0.0 : decCorr,
        };
    }

    // Port of Scope::MoveAxis duration logic (without issuing the pulse).
    private LimitedPulse MoveAxis(GuideDirection direction, int duration, MoveOptions options, DateTimeOffset now, List<PulseLimitAlert> alerts, out bool suppressedByMode)
    {
        suppressedByMode = false;
        if (!GuidingEnabled && (options & MoveOptions.Manual) == 0)
        {
            // PHD2 throws "Guiding disabled" before any limit handling; the move reports 0 / not limited.
            return new LimitedPulse(direction, 0, false, null);
        }

        bool algoOrDeduce = (options & (MoveOptions.AlgoResult | MoveOptions.AlgoDeduce)) != 0;
        if (!algoOrDeduce)
            return new LimitedPulse(direction, duration, false, null);

        if (direction.Axis() == GuideAxis.Dec)
        {
            int filtered = DecGuideModeFilter.Apply(EffectiveDecGuideMode, direction, duration);
            suppressedByMode = filtered == 0 && duration > 0;
            duration = filtered;
        }

        var limited = Limiter.Limit(direction, duration, now);
        if (limited.Alert is not null)
            alerts.Add(limited.Alert);
        return limited;
    }

    // Deviation from PHD2: an algorithm or deduced pulse shorter than MinPulseMs is rounded to 0 or to the minimum,
    // whichever is nearer (PHD2 sends any length; see MinPulseMs).
    private int MinimumPulse(int duration, MoveOptions options)
    {
        bool algoOrDeduce = (options & (MoveOptions.AlgoResult | MoveOptions.AlgoDeduce)) != 0;
        if (!algoOrDeduce || duration <= 0 || duration >= minPulseMs)
            return duration;
        return duration * 2 >= minPulseMs ? minPulseMs : 0;
    }

    private static bool ValidRate(double rate) => rate > 0 && double.IsFinite(rate);

    /// <summary><c>ROUND(|distance / rate|)</c>; 0 for invalid rates and non-finite distances.</summary>
    public static int DurationFor(double distancePx, double ratePxPerMs)
    {
        // Deviation from PHD2: an invalid (0/NaN/negative) rate or a non-finite distance yields 0 instead of undefined
        // behaviour ((int)NaN differs by platform), and durations saturate at int.MaxValue.
        if (!ValidRate(ratePxPerMs) || !double.IsFinite(distancePx))
            return 0;
        double v = Math.Floor(Math.Abs(distancePx / ratePxPerMs) + 0.5);
        return v >= int.MaxValue ? int.MaxValue : (int)v;
    }

    /// <summary>PHD2 <c>NotifyGuidingStarted</c>.</summary>
    public void GuidingStarted() => Notify(a => a.GuidingStarted());

    /// <summary>PHD2 <c>NotifyGuidingStopped</c>: algorithms reset and backlash state cleared.</summary>
    public void GuidingStopped()
    {
        Notify(a => a.GuidingStopped());
        Backlash.ResetState();
    }

    public void GuidingPaused() => Notify(a => a.GuidingPaused());

    public void GuidingResumed() => Notify(a => a.GuidingResumed());

    /// <summary>
    /// PHD2 <c>NotifyGuidingDithered</c> with the dither in mount coordinates (px). Also starts the
    /// limit-alert grace period.
    /// </summary>
    public void GuidingDithered(double raPx, double decPx, DateTimeOffset now)
    {
        RaAlgorithm.GuidingDithered(raPx);
        DecAlgorithm.GuidingDithered(decPx);

        // Deviation from PHD2: PHD2 only defers limit alerts when guiding output is re-enabled; the
        // engine also defers them after a dither (fast-recenter/settle can legitimately saturate).
        Limiter.DeferAlertCheck(now);
    }

    public void GuidingDitherSettleDone(bool success) => Notify(a => a.GuidingDitherSettleDone(success));

    /// <summary>PHD2 <c>NotifyDirectMove</c> (mount px).</summary>
    public void DirectMoveApplied(double raPx, double decPx)
    {
        RaAlgorithm.DirectMoveApplied(raPx);
        DecAlgorithm.DirectMoveApplied(decPx);
    }

    /// <summary>
    /// The corrections that actually went out for the last step, per axis (mount px, the amount by which a positive
    /// offset is reduced): see <see cref="IGuideAlgorithm.CorrectionApplied"/>.
    /// </summary>
    public void CorrectionsApplied(double raPx, double decPx)
    {
        RaAlgorithm.CorrectionApplied(raPx);
        DecAlgorithm.CorrectionApplied(decPx);
    }

    /// <summary>
    /// The correction the <paramref name="pulses"/> of a step apply (mount px, same sign as
    /// <see cref="GuideCorrection.RaCorrectionPx"/>); the backlash compensation of a Dec pulse only takes up slack.
    /// </summary>
    public static (double RaPx, double DecPx) CorrectionOf(IEnumerable<PulseCommand> pulses, double xRate, double yRate, int backlashCompMs)
    {
        double ra = 0;
        double dec = 0;
        foreach (var p in pulses)
        {
            switch (p.Direction)
            {
                case GuideDirection.West:
                    ra += p.DurationMs * xRate;
                    break;
                case GuideDirection.East:
                    ra -= p.DurationMs * xRate;
                    break;
                case GuideDirection.South:
                    dec += Math.Max(0, p.DurationMs - backlashCompMs) * yRate;
                    break;
                case GuideDirection.North:
                    dec -= Math.Max(0, p.DurationMs - backlashCompMs) * yRate;
                    break;
            }
        }

        return (ValidRate(xRate) ? ra : 0, ValidRate(yRate) ? dec : 0);
    }

    /// <summary>
    /// The correction the <paramref name="pulses"/> of <paramref name="corr"/> apply as the caller sends them (after its own
    /// clamps or removals). With the play model the Dec part is the motion it expected for the pulse as computed; a Dec pulse
    /// the caller dropped leaves the model's gear position unknown.
    /// </summary>
    public (double RaPx, double DecPx) CorrectionOf(GuideCorrection corr, IReadOnlyList<PulseCommand> pulses, double xRate, double yRate)
    {
        var fixedCorrection = CorrectionOf(pulses, xRate, yRate, corr.BacklashCompMs);
        if (Backlash is not PlayModelBacklash)
        {
            return fixedCorrection;
        }

        var decPulses = pulses.Where(p => p.Direction.Axis() == GuideAxis.Dec).ToList();
        if (decPulses.Count == 0)
        {
            if (corr.DECDuration > 0)
            {
                Backlash.ResetState();
            }

            return (fixedCorrection.RaPx, 0);
        }

        var decPulse = decPulses[0];
        return decPulses.Count == 1 && decPulse.Direction == corr.DECDirection && decPulse.DurationMs == corr.DECDuration
            ? (fixedCorrection.RaPx, corr.DecCorrectionPx)
            : fixedCorrection;
    }

    private void Notify(Action<IGuideAlgorithm> action)
    {
        action(RaAlgorithm);
        action(DecAlgorithm);
    }
}
