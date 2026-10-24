// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Coach;

/// <summary>Verdict for one tested pulse duration.</summary>
public enum PulseVerdict
{
    /// <summary>The mean move reached at least 50 % of the expected move, and the expected move stands clear of the noise.</summary>
    Effective,

    /// <summary>The expected move is at least 3 σ of the measurement and the mean move stayed below 50 % of it.</summary>
    Ineffective,

    /// <summary>The expected move is too small compared with the measurement noise: no conclusion.</summary>
    Inconclusive,
}

/// <summary>Pulse-response result of one duration on one axis (both directions, all repetitions).</summary>
public sealed record PulseSizeResult(int DurationMs, int Count, double ExpectedArcsec, double MovedArcsec, double SigmaArcsec, PulseVerdict Verdict);

/// <summary>Noise-aware evaluation of the pulse-response test (docs/COACH.md §2.3).</summary>
public static class MountResponseAnalyzer
{
    /// <summary>A pulse is effective when it moves at least this fraction of the expected distance.</summary>
    public const double EffectiveRatio = 0.5;

    /// <summary>The expected move must be at least this many σ of the measurement for a verdict.</summary>
    public const double ConclusiveSigmas = 3.0;

    /// <summary>Pulses at least this long are used for rate and asymmetry (short pulses are dominated by noise and stiction).</summary>
    public const int RateMinPulseMs = 500;

    public static bool IsRa(string direction) => direction is "East" or "West";

    /// <summary>
    /// Verdict per pulse duration of an axis. <paramref name="sigmaMoveArcsec"/> is the σ of one measured move (before/after
    /// averages); the σ of a duration's mean move is σ/√count. With σ = 0 every duration is conclusive.
    /// </summary>
    public static IReadOnlyList<PulseSizeResult> Classify(IEnumerable<CoachPulse> pulses, bool ra, double sigmaMoveArcsec = 0)
    {
        double sigma = double.IsFinite(sigmaMoveArcsec) ? Math.Max(0, sigmaMoveArcsec) : 0;
        return pulses.Where(p => IsRa(p.Direction) == ra).GroupBy(p => p.DurationMs).OrderBy(g => g.Key).Select(g =>
        {
            int n = g.Count();
            double expected = g.Average(p => p.ExpectedArcsec);
            double moved = g.Average(p => p.MovedArcsec);
            double sigmaMean = sigma / Math.Sqrt(n);
            var verdict = expected < ConclusiveSigmas * sigmaMean || expected <= 0 ? PulseVerdict.Inconclusive
                : moved < EffectiveRatio * expected ? PulseVerdict.Ineffective
                : PulseVerdict.Effective;
            return new PulseSizeResult(g.Key, n, expected, moved, sigmaMean, verdict);
        }).ToList();
    }

    /// <summary>
    /// Smallest pulse duration of the axis that is conclusively effective while no longer duration is ineffective; null when
    /// no duration is conclusively effective.
    /// </summary>
    public static int? MinEffectivePulse(IEnumerable<CoachPulse> pulses, bool ra, double sigmaMoveArcsec = 0)
    {
        var sizes = Classify(pulses, ra, sigmaMoveArcsec);
        int? result = null;
        for (int i = sizes.Count - 1; i >= 0; i--)
        {
            if (sizes[i].Verdict == PulseVerdict.Ineffective)
            {
                break;
            }

            if (sizes[i].Verdict == PulseVerdict.Effective)
            {
                result = sizes[i].DurationMs;
            }
        }

        return result;
    }

    /// <summary>
    /// Stiction: the minimum effective pulse when a shorter duration was conclusively ineffective; null otherwise (no
    /// stiction, or the short pulses were inconclusive in the noise).
    /// </summary>
    public static int? StictionPulse(IEnumerable<CoachPulse> pulses, bool ra, double sigmaMoveArcsec)
    {
        var list = pulses.ToList();
        int? min = MinEffectivePulse(list, ra, sigmaMoveArcsec);
        if (min is not { } m)
        {
            return null;
        }

        return Classify(list, ra, sigmaMoveArcsec).Any(x => x.DurationMs < m && x.Verdict == PulseVerdict.Ineffective) ? m : null;
    }

    /// <summary>Ratio of the mean moves (moved/expected) of two directions for pulses ≥ <paramref name="minMs"/>; null when a direction is missing.</summary>
    public static double? Asymmetry(IEnumerable<CoachPulse> pulses, string numerator, string denominator, int minMs)
    {
        var list = pulses.Where(p => p.DurationMs >= minMs).ToList();
        var a = list.Where(p => p.Direction == numerator).Select(p => p.Ratio).ToList();
        var b = list.Where(p => p.Direction == denominator).Select(p => p.Ratio).ToList();
        if (a.Count == 0 || b.Count == 0 || b.Average() <= 0.05)
        {
            return null;
        }

        return a.Average() / b.Average();
    }

    /// <summary>Measured/calibrated rate of an axis: mean ratio of both directions for pulses ≥ <paramref name="minMs"/>.</summary>
    public static double? RateRatio(IEnumerable<CoachPulse> pulses, bool ra, int minMs)
    {
        var list = pulses.Where(p => IsRa(p.Direction) == ra && p.DurationMs >= minMs).ToList();
        return list.Count == 0 ? null : list.Average(p => p.Ratio);
    }

    /// <summary>Pulse length used for rate/asymmetry: the effective minimum, at least 500 ms (or the longest tested).</summary>
    public static int RateBasis(IEnumerable<CoachPulse> pulses, bool ra, int? minEffective)
    {
        var sizes = pulses.Where(p => IsRa(p.Direction) == ra).Select(p => p.DurationMs).Distinct().ToList();
        if (sizes.Count == 0)
        {
            return RateMinPulseMs;
        }

        int basis = Math.Max(RateMinPulseMs, minEffective ?? RateMinPulseMs);
        return sizes.Any(s => s >= basis) ? basis : sizes.Max();
    }
}

/// <summary>Result of the mount response measurement.</summary>
/// <param name="StictionRaMs">Minimum effective RA pulse when stiction was significant, else null (likewise Dec).</param>
/// <param name="RaRatePxPerMs">Calibrated rates used for the expected moves (px/ms).</param>
/// <param name="SigmaRaArcsec">σ of one measured RA move (before/after averages), arcsec (likewise Dec).</param>
internal sealed record MountResponseResult(CoachResponse Response, bool StarLost, bool Truncated, double? BacklashPxPerMs, int? StictionRaMs,
    int? StictionDecMs, double RaRatePxPerMs, double DecRatePxPerMs, double SigmaRaArcsec, double SigmaDecArcsec);

/// <summary>
/// Frame-driven mount response measurement (guiding output off): RA pulse response (W/E pairs), Dec backlash with an own
/// unbiased method, Dec pulse response per direction with the gear engaged, then a recenter towards the lock position.
/// </summary>
/// <remarks>
/// <para>Backlash: pulse North with fixed pulses P until two consecutive moves are substantial (≥ 40 % of the calibrated
/// move) and agree within 35 % (gear engaged North). Then pulse South with the same P and record the drift-corrected Dec
/// position y'ₖ after each pulse. Once three moves in a row are substantial and the last two agree, the motion started no
/// later than pulse k−2, so the last two pulses moved at the full South rate r (their mean move / P, which may differ from
/// the North rate) and y'₀ − y'ⱼ = r·(j·P − D) holds for j = k−1, k. The dead band is D = j·P − (y'₀ − y'ⱼ)/r, averaged
/// over both points. Unlike PHD2's method (which compares against 90 % of the median North move and so underestimates by
/// about 10 % of the travel) this is unbiased, also with asymmetric Dec rates.</para>
/// <para>That dead band is the lost motion after a long move. Guiding reverses after small moves, and on a mount with elastic
/// (soft) play that loses less, and the more the longer the move before. So after the Dec response pulses a reversal test
/// alternates South/North pulses whose length climbs a ladder (<see cref="ReversalLadder"/> × the large-move value, each
/// pulse measured like a response pulse). Each step starts with a lead-in pulse that only leaves the previous swing. The
/// first step whose <see cref="ReversalsPerStep"/> reversals move the star gives the backlash reported and compensated: their mean lost time
/// P − move/r (r the direction's measured rate). That is the compensation at which a guiding reversal just lands. A hard
/// dead band lets no step below it move the star; the large-move value then stays.</para>
/// <para>A frame that arrived sooner than pulse + exposure after a pulse was issued began its exposure during the pulse (the
/// ASI120 starts exposures early) and is not used. See docs/notes/COACH-SUGGESTIONS.md.</para>
/// <para>Each response pulse is measured between the mean position of the 3 frames before and the 3 frames after it,
/// corrected for the linear drift measured in the Drift step. The σ of a measured move comes from the drift step's seeing
/// (else from the scatter within the averaging windows); short pulses whose expected move is within 3 σ are inconclusive
/// and never reported as stiction. Pulses are capped at the max pulse durations and skipped when the star would get too
/// close to the frame edge.</para>
/// </remarks>
internal sealed class MountResponseProcedure : ICoachFrameHook
{
    private const int MaxClearingPulses = 15;
    private const int MaxRecenterSteps = 10;
    private const int MaxRedos = 3;

    /// <summary>Frames averaged before and after each response pulse.</summary>
    public const int AverageFrames = 3;
    /// <summary>A backlash-test move counts as motion when it reaches this fraction of the calibrated move.</summary>
    private const double MoveRatio = 0.4;

    /// <summary>
    /// Reversal-test pulse lengths as fractions of the large-move backlash: a ladder from guide-sized reversals up (a hard dead
    /// band of the large-move value moves the star at none of them; 0.85 still catches an overestimated large-move value).
    /// </summary>
    public static readonly IReadOnlyList<double> ReversalLadder = [0.25, 0.45, 0.65, 0.85];

    /// <summary>Evaluated reversals per ladder step, after one lead-in pulse that leaves the previous step's swing.</summary>
    public const int ReversalsPerStep = 2;

    /// <summary>The reversal test runs only for a large-move backlash at least this long (shorter needs no compensation).</summary>
    public const double ReversalMinBacklashMs = 100;

    /// <summary>A ladder step's reversals move the star when their mean move reaches this many σ of the mean.</summary>
    private const double ReversalConclusiveSigmas = 3.0;

    private readonly object gate = new();
    private readonly TaskCompletionSource<MountResponseResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly double raDriftPxPerSec;
    private readonly double decDriftPxPerSec;
    private readonly int[] sizes;
    private readonly int repetitions;
    private readonly bool measureBacklash;
    private readonly double lostTimeoutSec;
    private readonly Action<MountResponseProcedure>? onProgress;
    private readonly Queue<Action> plan = new();
    private readonly List<CoachFrame> sincePulse = [];
    private readonly double? seeingRaPx;
    private readonly double? seeingDecPx;
    private readonly List<CoachPulse> pulses = [];
    private readonly List<CoachPoint> backlashPoints = [];
    private readonly List<double> clearMoves = [];
    private readonly List<(double Y, double T)> southPositions = [];
    private readonly List<double> southMoves = [];

    private Pending? pending;
    private DateTimeOffset? lostSince;
    private bool skipRequested;
    private bool finished;
    private bool truncated;
    private bool starLost;
    private int issued;
    private int estimatedSteps;
    private string phase = "pulses";
    private CoachFrame? frame;
    private PulseCommand? currentPulse;
    private IReadOnlyList<PulseCommand> next = [];

    // backlash state
    private int backlashPulseMs;
    private double backlashCumulativeMs;
    private double backlashStartY;
    private bool backlashStartSet;
    private double? northRate;
    private double? southRate;
    private double? backlashMs;
    private string backlashState = CoachBacklashStates.Skipped;
    private GuideDirection? lastDecDirection;
    private int clearCount;
    private int recenterSteps;
    private DateTimeOffset? southStartTime;
    private Action? lastAction;
    private IssueKind lastIssueKind;
    private bool pendingDisturbed;
    private bool redoing;
    private int redoCount;
    private int backlashRestarts;

    // reversal test state (guide-scale backlash)
    private int[] reversalSteps = [];
    private int reversalStep;
    private int reversalStepIssued;
    private int reversalRestarts;
    private bool reversalDone;
    private double? guideBacklashMs;
    private int? reversalOnsetMs;
    private readonly List<(GuideDirection Direction, double MovedPx)> reversalMoves = [];
    private readonly List<CoachPulse> reversalPulses = [];

    // the last pulse issued through Issue: frames exposed while it ran are skipped
    private (DateTimeOffset Time, int Ms)? lastIssue;
    private bool waitRequested;
    private DateTimeOffset? origin;
    private double windowSsRa;
    private double windowSsDec;
    private int windowDof;

    /// <param name="raDriftPxPerSec">Linear RA drift (mount px/s) removed from the measured moves; likewise Dec.</param>
    /// <param name="seeingRaPx">Per-frame RA position noise (drift step seeing, px); null = estimate from the averaging windows.</param>
    /// <param name="seeingDecPx">Per-frame Dec position noise, px; null = estimate.</param>
    public MountResponseProcedure(double raDriftPxPerSec, double decDriftPxPerSec, IReadOnlyList<int>? pulseSizes = null, int repetitions = 2,
        bool measureBacklash = true, double lostTimeoutSec = 30, Action<MountResponseProcedure>? onProgress = null, double? seeingRaPx = null,
        double? seeingDecPx = null)
    {
        this.seeingRaPx = seeingRaPx is > 0 ? seeingRaPx : null;
        this.seeingDecPx = seeingDecPx is > 0 ? seeingDecPx : null;
        this.raDriftPxPerSec = double.IsFinite(raDriftPxPerSec) ? raDriftPxPerSec : 0;
        this.decDriftPxPerSec = double.IsFinite(decDriftPxPerSec) ? decDriftPxPerSec : 0;
        sizes = (pulseSizes ?? [100, 250, 500, 1000]).Where(s => s > 0).Distinct().Order().ToArray();
        this.repetitions = Math.Max(1, repetitions);
        this.measureBacklash = measureBacklash;
        this.lostTimeoutSec = lostTimeoutSec;
        this.onProgress = onProgress;
        backlashState = measureBacklash ? CoachBacklashStates.Measuring : CoachBacklashStates.Skipped;
    }

    public bool SuspendsGuiding => true;

    public Task<MountResponseResult> Completion => tcs.Task;

    /// <summary>Current sub-phase: pulses (RA), backlash, pulses (Dec), recenter.</summary>
    public string Phase
    {
        get
        {
            lock (gate)
            {
                return phase;
            }
        }
    }

    /// <summary>Last response pulse issued (for the step detail), null before the first one.</summary>
    public PulseCommand? CurrentPulse
    {
        get
        {
            lock (gate)
            {
                return currentPulse;
            }
        }
    }

    public double Progress
    {
        get
        {
            lock (gate)
            {
                return finished ? 1.0 : Math.Min(0.99, issued / (double)Math.Max(1, estimatedSteps));
            }
        }
    }

    /// <summary>Partial response data for live status.</summary>
    public CoachResponse Snapshot()
    {
        lock (gate)
        {
            return BuildResponse();
        }
    }

    /// <summary>Stops after the current frame; results so far are kept.</summary>
    public void Skip()
    {
        lock (gate)
        {
            skipRequested = true;
        }
    }

    public bool OnGuidingFailed(GuideErrorCode code)
    {
        lock (gate)
        {
            if (code == GuideErrorCode.StarReacquireTimeout)
            {
                starLost = true;
                FinishLocked();
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<PulseCommand> OnFrame(CoachFrame f)
    {
        IReadOnlyList<PulseCommand> result;
        lock (gate)
        {
            result = OnFrameLocked(f);
        }

        onProgress?.Invoke(this);
        return result;
    }

    private IReadOnlyList<PulseCommand> OnFrameLocked(CoachFrame f)
    {
        if (finished)
        {
            return [];
        }

        if (skipRequested)
        {
            FinishLocked();
            return [];
        }

        if (!f.StarFound)
        {
            lostSince ??= f.Time;
            if (pending is not null)
            {
                Disrupt();
            }

            sincePulse.Clear();
            if ((f.Time - lostSince.Value).TotalSeconds > lostTimeoutSec)
            {
                starLost = true;
                FinishLocked();
            }

            return [];
        }

        lostSince = null;
        if (f.PulsesDropped && frame is not null)
        {
            // the pulses requested after the previous frame were not sent (the mount turned busy): redo them
            Disrupt();
        }

        if (f.MountBusy)
        {
            // the mount reports slewing/tracking off (possibly spuriously after a pulse): skip the frame, don't pulse
            pendingDisturbed |= pending is not null;
            return [];
        }

        frame = f;
        origin ??= f.Time;
        if (estimatedSteps == 0)
        {
            BuildPlan(f);
        }

        if (lastIssue is { } li && (f.Time - li.Time).TotalMilliseconds < li.Ms + f.ExposureMs)
        {
            // arrived too soon to be exposed wholly after the last pulse (cameras can start the next exposure early): its
            // position lies partly before the move, so it counts neither before nor after it
            return [];
        }

        sincePulse.Add(f);
        if (pending is { } p)
        {
            if (sincePulse.Count < p.FramesNeeded)
            {
                // still collecting the frames after the pulse
                return [];
            }

            if (pendingDisturbed && MovedDuringCondition(p, f))
            {
                // the mount really moved while it reported slewing: don't use a move measured across it
                Disrupt();
            }
            else
            {
                pending = null;
                pendingDisturbed = false;
                redoCount = 0;
                p.Complete(sincePulse.Skip(sincePulse.Count - p.FramesNeeded).ToList());
            }
        }

        next = [];
        while (next.Count == 0 && !finished)
        {
            if (plan.Count == 0)
            {
                FinishLocked();
                break;
            }

            var action = plan.Dequeue();
            action();
            if (waitRequested)
            {
                // the action needs more frames (averaging window): retry it on the next frame
                waitRequested = false;
                PrependFirst(action);
                break;
            }

            if (next.Count > 0)
            {
                if (!redoing)
                {
                    redoCount = 0;
                }

                redoing = false;
                lastAction = action;
            }
        }

        return next;
    }

    private void BuildPlan(CoachFrame f)
    {
        var raSizes = sizes.Select(s => Math.Min(s, f.MaxRaDurationMs)).Distinct().ToArray();
        var decSizes = sizes.Select(s => Math.Min(s, f.MaxDecDurationMs)).Distinct().ToArray();
        backlashPulseMs = BacklashPulse(f);

        foreach (int s in raSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.West, s));
                plan.Enqueue(() => ResponsePulse(GuideDirection.East, s));
            }
        }

        plan.Enqueue(() => phase = "backlash");
        if (measureBacklash)
        {
            plan.Enqueue(ClearNorthStep);
        }
        else
        {
            plan.Enqueue(() => Engage(GuideDirection.South));
        }

        plan.Enqueue(() => phase = "pulses");
        foreach (int s in decSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.South, s));
            }
        }

        plan.Enqueue(() => Engage(GuideDirection.North));
        foreach (int s in decSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.North, s));
            }
        }

        if (measureBacklash)
        {
            // the backlash guiding meets: reversals after small moves (runs only when the large-move backlash is worth it)
            plan.Enqueue(() => phase = "backlash");
            plan.Enqueue(ReversalStep);
        }

        plan.Enqueue(() => phase = "recenter");
        plan.Enqueue(RecenterStep);
        estimatedSteps = (raSizes.Length * repetitions * 2 + decSizes.Length * repetitions * 2) * AverageFrames
            + (measureBacklash ? 10 + ReversalLadder.Count * (1 + ReversalsPerStep) * AverageFrames : 4) + 3;
    }

    private static int BacklashPulse(CoachFrame f)
    {
        double rate = f.Transform.YRate;
        if (!(rate > 0))
        {
            return 500;
        }

        // about 4 px per pulse (PHD2's backlash tool uses 4 px moves) but at most 60 % of the search region
        double ms = Math.Min(4.0 / rate, 0.6 * f.SearchRegion / rate);
        return (int)Math.Clamp(Math.Round(ms / 50) * 50, 200, f.MaxDecDurationMs);
    }

    private bool HasRoom(GuideDirection dir, int ms)
    {
        var f = frame!;
        double rate = dir.Axis() == GuideAxis.Ra ? f.Transform.XRate : f.Transform.YRate;
        double move = ms * Math.Max(rate, 0);
        double margin = f.SearchRegion + move + 5;
        var c = f.StarPosition;
        bool ok = c.X >= margin && c.Y >= margin && c.X <= f.FrameWidth - 1 - margin && c.Y <= f.FrameHeight - 1 - margin;
        if (!ok)
        {
            truncated = true;
        }

        return ok;
    }

    /// <summary>Issues a pulse after the current frame; <paramref name="complete"/> gets the next <paramref name="framesAfter"/> frames.</summary>
    private void Issue(GuideDirection dir, int ms, int framesAfter, Action<IReadOnlyList<CoachFrame>>? complete, IssueKind kind = IssueKind.Other)
    {
        next = [new PulseCommand(dir, ms)];
        issued++;
        sincePulse.Clear();
        lastIssue = (frame!.Time, ms);
        lastIssueKind = kind;
        pendingDisturbed = false;
        if (dir.Axis() == GuideAxis.Dec)
        {
            lastDecDirection = dir;
        }

        var f = frame!;
        double rate = dir.Axis() == GuideAxis.Ra ? f.Transform.XRate : f.Transform.YRate;
        double expectedArcmin = ms * Math.Max(rate, 0) * (f.PixelScale > 0 ? f.PixelScale : 1.0) / 60.0;
        pending = complete is null ? null : new Pending(Math.Max(1, framesAfter), complete, f, expectedArcmin);
    }

    /// <summary>
    /// A pulse or its measurement was disturbed (not sent, star lost, real mount motion): redo a response pulse or take-up
    /// (at most 3 times), restart the backlash test (at most twice, then it is unreliable).
    /// </summary>
    private void Disrupt()
    {
        pending = null;
        pendingDisturbed = false;
        sincePulse.Clear();
        switch (lastIssueKind)
        {
            case IssueKind.Backlash:
                RestartBacklash();
                break;
            case IssueKind.Reversal:
                RestartReversal();
                break;
            default:
                if (lastAction is { } a && redoCount < MaxRedos)
                {
                    redoCount++;
                    redoing = true;
                    if (lastIssueKind == IssueKind.Other)
                    {
                        // a take-up that was not sent did not engage the gear
                        lastDecDirection = null;
                    }

                    PrependFirst(a);
                }

                break;
        }

        lastIssueKind = IssueKind.None;
    }

    private void RestartBacklash()
    {
        clearMoves.Clear();
        southPositions.Clear();
        southMoves.Clear();
        southStartTime = null;
        clearCount = 0;
        if (backlashRestarts++ < 2)
        {
            backlashPoints.Clear();
            backlashCumulativeMs = 0;
            backlashStartSet = false;
            PrependFirst(ClearNorthStep);
        }
        else
        {
            backlashState = CoachBacklashStates.Unreliable;
            lastDecDirection = null;
            PrependEngage(GuideDirection.South);
        }
    }

    /// <summary>Whether the mount-reported pointing moved more than the pulse explains while a condition was active.</summary>
    private static bool MovedDuringCondition(Pending p, CoachFrame now)
    {
        var sep = MountStateWatcher.SeparationArcmin(
            new MountSnapshot { RightAscensionHours = p.IssueFrame.RightAscensionHours, DeclinationDeg = p.IssueFrame.DeclinationDeg },
            new MountSnapshot { RightAscensionHours = now.RightAscensionHours, DeclinationDeg = now.DeclinationDeg });
        return sep is { } s && s > Math.Max(0.5, 3 * p.ExpectedArcmin);
    }

    /// <summary>Mean mount position and time (s since the start) of frames.</summary>
    private (double X, double Y, double T) Mean(IReadOnlyList<CoachFrame> frames)
    {
        double x = 0, y = 0, t = 0;
        foreach (var f in frames)
        {
            x += f.MountPosition.X;
            y += f.MountPosition.Y;
            t += (f.Time - origin!.Value).TotalSeconds;
        }

        return (x / frames.Count, y / frames.Count, t / frames.Count);
    }

    /// <summary>Move along the pulse direction between two positions, drift-corrected (px).</summary>
    private double Move(GuideDirection dir, (double X, double Y, double T) before, (double X, double Y, double T) after)
    {
        bool ra = dir.Axis() == GuideAxis.Ra;
        double delta = ra ? after.X - before.X : after.Y - before.Y;
        double drift = (ra ? raDriftPxPerSec : decDriftPxPerSec) * (after.T - before.T);
        double sign = dir is GuideDirection.East or GuideDirection.North ? 1 : -1;
        return sign * (delta - drift);
    }

    private void AccumulateWindow(IReadOnlyList<CoachFrame> window)
    {
        if (window.Count < 2)
        {
            return;
        }

        double mx = window.Average(f => f.MountPosition.X), my = window.Average(f => f.MountPosition.Y);
        windowSsRa += window.Sum(f => (f.MountPosition.X - mx) * (f.MountPosition.X - mx));
        windowSsDec += window.Sum(f => (f.MountPosition.Y - my) * (f.MountPosition.Y - my));
        windowDof += window.Count - 1;
    }

    /// <summary>σ of one measured move (difference of two means of <see cref="AverageFrames"/> frames), px.</summary>
    private double MoveSigmaPx(bool ra)
    {
        double? perFrame = ra ? seeingRaPx : seeingDecPx;
        perFrame ??= windowDof > 0 ? Math.Sqrt((ra ? windowSsRa : windowSsDec) / windowDof) : null;
        return (perFrame ?? 0) * Math.Sqrt(2.0 / AverageFrames);
    }

    private void ResponsePulse(GuideDirection dir, int ms)
    {
        if (!HasRoom(dir, ms))
        {
            return;
        }

        if (sincePulse.Count < AverageFrames)
        {
            waitRequested = true;
            return;
        }

        var f = frame!;
        double rate = dir.Axis() == GuideAxis.Ra ? f.Transform.XRate : f.Transform.YRate;
        double scale = f.PixelScale > 0 ? f.PixelScale : 1.0;
        var before = Mean(sincePulse.Skip(sincePulse.Count - AverageFrames).ToList());
        currentPulse = new PulseCommand(dir, ms);
        Issue(dir, ms, AverageFrames, kind: IssueKind.Response, complete: afterFrames =>
        {
            AccumulateWindow(afterFrames);
            double moved = Move(dir, before, Mean(afterFrames));
            double expected = ms * rate;
            pulses.Add(new CoachPulse
            {
                Direction = dir.ToString(),
                DurationMs = ms,
                ExpectedArcsec = Math.Round(expected * scale, 3),
                MovedArcsec = Math.Round(moved * scale, 3),
                Ratio = expected > 0 ? Math.Round(moved / expected, 3) : 0,
            });
        });
    }

    private void AddBacklashPoint(CoachFrame f)
    {
        if (!backlashStartSet)
        {
            backlashStartSet = true;
            backlashStartY = f.MountPosition.Y;
        }

        double scale = f.PixelScale > 0 ? f.PixelScale : 1.0;
        backlashPoints.Add(new CoachPoint(Math.Round(backlashCumulativeMs, 1), Math.Round((f.MountPosition.Y - backlashStartY) * scale, 3)));
    }

    private void ClearNorthStep()
    {
        var f = frame!;
        double rate = f.Transform.YRate;
        if (!(rate > 0))
        {
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        // too much Dec drift per frame cycle: the dead band cannot be separated from the drift
        double cycleSec = f.ExposureMs / 1000.0 + backlashPulseMs / 1000.0 + 0.5;
        if (Math.Abs(decDriftPxPerSec) * cycleSec > 0.35 * backlashPulseMs * rate)
        {
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        if (clearCount == 0)
        {
            AddBacklashPoint(f);
        }

        if (clearCount >= MaxClearingPulses || !HasRoom(GuideDirection.North, backlashPulseMs))
        {
            // the mount never moved consistently North (or no room): no backlash result
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        clearCount++;
        var before = Mean([f]);
        Issue(GuideDirection.North, backlashPulseMs, 1, kind: IssueKind.Backlash, complete: frames =>
        {
            backlashCumulativeMs += backlashPulseMs;
            var after = frames[0];
            AddBacklashPoint(after);
            clearMoves.Add(Move(GuideDirection.North, before, Mean(frames)));
            int n = clearMoves.Count;
            double min = MoveRatio * backlashPulseMs * rate;
            if (n >= 2 && clearMoves[n - 1] >= min && clearMoves[n - 2] >= min && Consistent(clearMoves[n - 1], clearMoves[n - 2]))
            {
                // engaged North: the actual North rate from the two consistent moves
                northRate = (clearMoves[n - 1] + clearMoves[n - 2]) / 2 / backlashPulseMs;
                southPositions.Clear();
                southMoves.Clear();
                PrependFirst(SouthStep);
            }
            else
            {
                PrependFirst(ClearNorthStep);
            }
        });
    }

    private void SouthStep()
    {
        var f = frame!;
        double rNorth = northRate ?? f.Transform.YRate;
        if (southPositions.Count == 0)
        {
            southPositions.Add((f.MountPosition.Y, 0));
        }

        if (southMoves.Count >= MaxClearingPulses || !HasRoom(GuideDirection.South, backlashPulseMs))
        {
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        var southOrigin = southStartTime ??= f.Time;
        Issue(GuideDirection.South, backlashPulseMs, 1, kind: IssueKind.Backlash, complete: frames =>
        {
            backlashCumulativeMs += backlashPulseMs;
            var after = frames[0];
            AddBacklashPoint(after);
            double t = (after.Time - southOrigin).TotalSeconds;
            double y = after.MountPosition.Y - decDriftPxPerSec * t;
            southMoves.Add(southPositions[^1].Y - y);
            southPositions.Add((y, t));

            // engaged South once three moves in a row are substantial and the last two agree: motion then started
            // no later than pulse k-2, so pulses k-1 and k moved at the full South rate (which may differ from North)
            int k = southMoves.Count;
            double min = MoveRatio * backlashPulseMs * rNorth;
            if (k >= 3 && southMoves[k - 3] >= min && southMoves[k - 2] >= min && southMoves[k - 1] >= min && Consistent(southMoves[k - 1], southMoves[k - 2]))
            {
                double rSouth = (southMoves[k - 1] + southMoves[k - 2]) / 2 / backlashPulseMs;
                double y0 = southPositions[0].Y;
                double d1 = k * backlashPulseMs - (y0 - southPositions[k].Y) / rSouth;
                double d2 = (k - 1) * backlashPulseMs - (y0 - southPositions[k - 1].Y) / rSouth;
                double d = (d1 + d2) / 2;
                backlashMs = Math.Max(0, d);
                southRate = rSouth;
                backlashState = d >= 50 ? CoachBacklashStates.Measured : CoachBacklashStates.None;
            }
            else
            {
                PrependFirst(SouthStep);
            }
        });
    }

    private static bool Consistent(double a, double b) => Math.Max(a, b) <= 1.35 * Math.Min(a, b);

    /// <summary>
    /// Reversal test: alternating Dec pulses whose length climbs a ladder (<see cref="ReversalLadder"/> × the large-move
    /// backlash), each measured like a response pulse. Each step starts with a lead-in pulse that only leaves the previous
    /// swing (the long North series, or the shorter pulses of the step before, which a longer pulse overruns), then evaluates
    /// <see cref="ReversalsPerStep"/> reversals after a move of the same size, as in guiding. The first step whose reversals
    /// move the star gives the backlash guiding meets.
    /// </summary>
    private void ReversalStep()
    {
        if (reversalDone)
        {
            return;
        }

        var f = frame!;
        if (reversalSteps.Length == 0)
        {
            if (backlashState != CoachBacklashStates.Measured || backlashMs is not { } large || large < ReversalMinBacklashMs
                || !(f.Transform.YRate > 0))
            {
                reversalDone = true;
                return;
            }

            int max = Math.Max(100, f.MaxDecDurationMs);
            reversalSteps = ReversalLadder.Select(x => (int)Math.Clamp(Math.Round(x * large / 50) * 50, 100, max)).Distinct().ToArray();
        }

        if (reversalStep >= reversalSteps.Length)
        {
            // no step moved the star: a hard dead band of about the large-move value, which stays
            reversalDone = true;
            return;
        }

        int ms = reversalSteps[reversalStep];
        var dir = lastDecDirection == GuideDirection.North ? GuideDirection.South : GuideDirection.North;
        if (!HasRoom(dir, ms))
        {
            reversalDone = true;
            return;
        }

        if (sincePulse.Count < AverageFrames)
        {
            waitRequested = true;
            return;
        }

        var before = Mean(sincePulse.Skip(sincePulse.Count - AverageFrames).ToList());
        bool evaluated = reversalStepIssued > 0;
        reversalStepIssued++;
        Issue(dir, ms, AverageFrames, kind: IssueKind.Reversal, complete: afterFrames =>
        {
            AccumulateWindow(afterFrames);
            if (evaluated)
            {
                double moved = Move(dir, before, Mean(afterFrames));
                double expected = ms * f.Transform.YRate;
                double scale = f.PixelScale > 0 ? f.PixelScale : 1.0;
                reversalMoves.Add((dir, moved));
                reversalPulses.Add(new CoachPulse
                {
                    Direction = dir.ToString(),
                    DurationMs = ms,
                    ExpectedArcsec = Math.Round(expected * scale, 3),
                    MovedArcsec = Math.Round(moved * scale, 3),
                    Ratio = expected > 0 ? Math.Round(moved / expected, 3) : 0,
                });
                if (reversalMoves.Count == ReversalsPerStep)
                {
                    EvaluateReversalStep(ms);
                }
            }

            if (!reversalDone)
            {
                PrependFirst(ReversalStep);
            }
        });
    }

    /// <summary>
    /// A ladder step's reversals move the star when their mean move reaches 3 σ of the mean: the backlash guiding meets is
    /// then their mean lost time, P − move/r (r the direction's measured rate), at most the large-move value. Otherwise the
    /// next, longer step follows.
    /// </summary>
    private void EvaluateReversalStep(int ms)
    {
        double sigmaMean = MoveSigmaPx(false) / Math.Sqrt(reversalMoves.Count);
        double mean = reversalMoves.Average(m => m.MovedPx);
        if (mean > 0 && mean >= ReversalConclusiveSigmas * sigmaMean && backlashMs is { } large)
        {
            double lost = reversalMoves.Average(m => ms - m.MovedPx / DecRate(m.Direction));
            guideBacklashMs = Math.Clamp(lost, 0, large);
            reversalOnsetMs = ms;
            reversalDone = true;
            return;
        }

        reversalMoves.Clear();
        reversalStep++;
        reversalStepIssued = 0;
    }

    /// <summary>Dec rate of a direction, px/ms: the calibrated rate times its ≥ 500 ms response pulses' mean ratio (0.5–1.5).</summary>
    private double DecRate(GuideDirection dir)
    {
        string name = dir.ToString();
        var ratios = pulses.Where(p => p.Direction == name && p.DurationMs >= MountResponseAnalyzer.RateMinPulseMs).Select(p => p.Ratio).ToList();
        double ratio = ratios.Count > 0 ? Math.Clamp(ratios.Average(), 0.5, 1.5) : 1.0;
        return frame!.Transform.YRate * ratio;
    }

    /// <summary>A reversal pulse was disturbed: restart the reversal test once, then keep the large-move value.</summary>
    private void RestartReversal()
    {
        reversalMoves.Clear();
        reversalPulses.Clear();
        reversalStep = 0;
        reversalStepIssued = 0;
        if (reversalRestarts++ < 1)
        {
            PrependFirst(ReversalStep);
        }
        else
        {
            reversalDone = true;
        }
    }

    /// <summary>Takes up the Dec gear slack in <paramref name="dir"/> before response pulses in that direction.</summary>
    private void Engage(GuideDirection dir)
    {
        if (lastDecDirection == dir)
        {
            return;
        }

        var f = frame!;
        int pulse = backlashPulseMs > 0 ? backlashPulseMs : BacklashPulse(f);
        int ms = backlashMs is { } d ? (int)Math.Min(f.MaxDecDurationMs, d + pulse) : Math.Min(f.MaxDecDurationMs, 2 * pulse);
        if (!HasRoom(dir, pulse))
        {
            return;
        }

        Issue(dir, ms, 0, null);
        if (backlashMs is { } dd && dd + pulse > f.MaxDecDurationMs)
        {
            // the take-up exceeds the max pulse: continue in further pulses
            double remaining = dd + pulse - f.MaxDecDurationMs;
            PrependFirst(() => EngageRemainder(dir, remaining));
        }
    }

    private void EngageRemainder(GuideDirection dir, double remainingMs)
    {
        var f = frame!;
        int ms = (int)Math.Min(f.MaxDecDurationMs, Math.Ceiling(remainingMs));
        if (ms <= 0 || !HasRoom(dir, ms))
        {
            return;
        }

        Issue(dir, ms, 0, null);
        if (remainingMs > ms)
        {
            PrependFirst(() => EngageRemainder(dir, remainingMs - ms));
        }
    }

    private void RecenterStep()
    {
        var f = frame!;
        var ofs = f.MountOffset;
        if (!ofs.IsValid || recenterSteps >= MaxRecenterSteps || (Math.Abs(ofs.X) < 1.0 && Math.Abs(ofs.Y) < 1.0))
        {
            return;
        }

        recenterSteps++;
        var list = new List<PulseCommand>(2);
        var ra = f.Transform.RaPulse(ofs.X);
        if (Math.Abs(ofs.X) >= 1.0 && ra.DurationMs > 0)
        {
            list.Add(ra with { DurationMs = Math.Min(ra.DurationMs, f.MaxRaDurationMs) });
        }

        var dec = f.Transform.DecPulse(ofs.Y);
        if (Math.Abs(ofs.Y) >= 1.0 && dec.DurationMs > 0)
        {
            int extra = lastDecDirection is { } last && last != dec.Direction && (guideBacklashMs ?? backlashMs) is { } d ? (int)d : 0;
            list.Add(dec with { DurationMs = Math.Min(dec.DurationMs + extra, f.MaxDecDurationMs) });
            lastDecDirection = dec.Direction;
        }

        if (list.Count > 0)
        {
            next = list;
            issued++;
            lastIssue = (f.Time, list.Max(p => p.DurationMs));
            PrependFirst(RecenterStep);
        }
    }

    private void PrependEngage(GuideDirection dir) => PrependFirst(() => Engage(dir));

    private void PrependFirst(Action a)
    {
        var rest = plan.ToArray();
        plan.Clear();
        plan.Enqueue(a);
        foreach (var x in rest)
        {
            plan.Enqueue(x);
        }
    }

    private void FinishLocked()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        if (backlashState == CoachBacklashStates.Measuring)
        {
            // ended before the backlash test did (skipped, star lost)
            backlashState = CoachBacklashStates.Unreliable;
        }

        pending = null;
        double scale = frame?.PixelScale is > 0 and var sc ? sc : 1.0;
        double sigmaRa = MoveSigmaPx(true) * scale, sigmaDec = MoveSigmaPx(false) * scale;
        tcs.TrySetResult(new MountResponseResult(BuildResponse(), starLost, truncated, northRate,
            MountResponseAnalyzer.StictionPulse(pulses, true, sigmaRa), MountResponseAnalyzer.StictionPulse(pulses, false, sigmaDec),
            frame?.Transform.XRate ?? 0, frame?.Transform.YRate ?? 0, sigmaRa, sigmaDec));
    }

    private CoachResponse BuildResponse()
    {
        double scale = frame?.PixelScale is > 0 and var s ? s : 1.0;
        double? rate = southRate ?? northRate ?? frame?.Transform.YRate;
        int? minRa = MountResponseAnalyzer.MinEffectivePulse(pulses, ra: true, MoveSigmaPx(true) * scale);
        int? minDec = MountResponseAnalyzer.MinEffectivePulse(pulses, ra: false, MoveSigmaPx(false) * scale);
        int raBasis = MountResponseAnalyzer.RateBasis(pulses, true, minRa);
        int decBasis = MountResponseAnalyzer.RateBasis(pulses, false, minDec);
        // while the reversal ladder is still to come, the large-move value is not the result: no value until it is
        bool ladderPending = !finished && !reversalDone && backlashState == CoachBacklashStates.Measured
            && backlashMs is >= ReversalMinBacklashMs;
        string state = ladderPending ? CoachBacklashStates.Measuring : backlashState;
        bool known = state is CoachBacklashStates.Measured or CoachBacklashStates.None;
        double? guide = known ? guideBacklashMs ?? backlashMs : null;
        double? large = known ? backlashMs : null;
        return new CoachResponse
        {
            BacklashMs = guide is { } b ? Math.Round(b) : null,
            BacklashArcsec = guide is { } b2 && rate is { } r ? Math.Round(b2 * r * scale, 2) : null,
            BacklashState = state,
            BacklashPoints = backlashPoints.ToList(),
            LargeMoveBacklashMs = large is { } l ? Math.Round(l) : null,
            LargeMoveBacklashArcsec = large is { } l2 && rate is { } r2 ? Math.Round(l2 * r2 * scale, 2) : null,
            ReversalPulseMs = reversalOnsetMs,
            ReversalMoves = reversalPulses.ToList(),
            Pulses = pulses.ToList(),
            MinEffectivePulseRaMs = minRa,
            MinEffectivePulseDecMs = minDec,
            AsymmetryRa = Round(MountResponseAnalyzer.Asymmetry(pulses, "West", "East", raBasis)),
            AsymmetryDec = Round(MountResponseAnalyzer.Asymmetry(pulses, "North", "South", decBasis)),
            RateRatioRa = Round(MountResponseAnalyzer.RateRatio(pulses, true, raBasis)),
            RateRatioDec = Round(MountResponseAnalyzer.RateRatio(pulses, false, decBasis)),
        };
    }

    private static double? Round(double? v) => v is { } d ? Math.Round(d, 3) : null;

    private sealed class Pending(int framesNeeded, Action<IReadOnlyList<CoachFrame>> complete, CoachFrame issueFrame, double expectedArcmin)
    {
        public int FramesNeeded { get; } = framesNeeded;

        public CoachFrame IssueFrame { get; } = issueFrame;

        public double ExpectedArcmin { get; } = expectedArcmin;

        public void Complete(IReadOnlyList<CoachFrame> after) => complete(after);
    }

    private enum IssueKind
    {
        None,
        Response,
        Backlash,
        Reversal,
        Other,
    }
}
