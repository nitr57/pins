// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>What happened to the Dec direction of <see cref="DecGuideMode.Drift"/>.</summary>
public enum DecDirectionNoteKind
{
    /// <summary>
    /// The direction changed: one was picked, the drift reversed, or it went back to both directions (the drift faded or
    /// became too weak).
    /// </summary>
    Switch,

    /// <summary>The safety valve opened: both directions until a large error on the other side is back.</summary>
    ValveOpened,

    /// <summary>The safety valve closed: one direction again.</summary>
    ValveClosed,

    /// <summary>Drift and direction forgotten (guiding started, a large move, a meridian flip).</summary>
    Reset,

    /// <summary>Periodic summary (debug log).</summary>
    Summary,
}

/// <summary>A note of the Dec direction logic for the logs.</summary>
internal sealed record DecDirectionNote(DecDirectionNoteKind Kind, string Message);

/// <summary>State of <see cref="DecGuideMode.Drift"/> for hosts and UIs.</summary>
public sealed record DecDirectionState
{
    /// <summary>The direction Dec guides in: the one that counters the drift, Both while no clear drift is measured.</summary>
    public DecGuideDirection Direction { get; init; }

    /// <summary>True while the safety valve lets both directions bring back a large error on the other side.</summary>
    public bool ValveOpen { get; init; }

    /// <summary>The Dec drift (sign as <see cref="DriftEstimate.PxPerSec"/>), null before there is enough data.</summary>
    public double? DriftPxPerSec { get; init; }

    /// <summary>Standard error of <see cref="DriftPxPerSec"/>.</summary>
    public double? DriftSigmaPxPerSec { get; init; }

    /// <summary>Direction changes since guiding started (the first pick included).</summary>
    public int Switches { get; init; }

    /// <summary>Times the safety valve opened since guiding started.</summary>
    public int ValveOpenings { get; init; }
}

/// <summary>
/// Picks the Dec guide direction of <see cref="DecGuideMode.Drift"/>: both directions until a Dec drift has held for
/// <see cref="HoldFor"/>, then only the pulse direction that counters it, with a safety valve for a large error on the
/// other side.
/// </summary>
/// <remarks>
/// A positive drift (the offset grows positive) is corrected by South pulses, a negative one by North pulses, as in
/// <see cref="AxisCorrector"/>. The guider allows both directions while it settles (after a dither, at the start) and
/// calibration never filters; neither is decided here. The rules and their reasons are in docs/ALGORITHMS.md, "Dec guide
/// mode Drift".
/// </remarks>
internal sealed class DecDirectionPolicy
{
    /// <summary>A drift counts once it is this many standard errors from 0.</summary>
    public const double SignificanceSigmas = 2.5;

    /// <summary>
    /// A drift holds only when it moves the star by the noise of a frame (σ) within this many seconds: slower, the star
    /// would wait long on the side the direction cannot correct after every correction that overshoots.
    /// </summary>
    public const double DriftFloorSec = 30;

    /// <summary>The drift minus this many standard errors must reach the floor (<see cref="DriftFloorSec"/>).</summary>
    public const double FloorSigmas = 1;

    /// <summary>
    /// The direction stays while the drift is at least this many standard errors in its favour, and that many stronger
    /// would reach the floor.
    /// </summary>
    public const double KeepSigmas = 1;

    /// <summary>A drift must hold for this long before one direction is guided.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The drift must stop being in favour of the direction for this long before the direction changes: to the opposite
    /// direction when that drift holds then, else to both directions.
    /// </summary>
    public static readonly TimeSpan SwitchAfter = TimeSpan.FromMinutes(2);

    /// <summary>Frames beyond the valve limit on the forbidden side before both directions are allowed.</summary>
    public const int ValveFrames = 10;

    /// <summary>The valve limit is at least this far from the lock position (px) ...</summary>
    public const double ValveMinPx = 1.5;

    /// <summary>... and this many times the recent Dec RMS.</summary>
    public const double ValveRmsFactor = 3.0;

    // memory of the recent Dec RMS, frames
    private const int RmsFrames = 50;

    // frames between two summaries
    private const int SummaryFrames = 100;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly List<DecDirectionNote> notes = [];
    private DateTimeOffset? holdingSince;
    private DecGuideDirection holding;
    private DateTimeOffset? unsupportedSince;
    private int beyondFrames;
    private int valveFrames;
    private double squareSum;
    private double squareWeight;
    private int framesSinceSummary;

    /// <summary>The picked direction (<see cref="DecGuideDirection.Both"/> until the drift is significant).</summary>
    public DecGuideDirection Direction { get; private set; }

    /// <summary>True while the safety valve allows both directions.</summary>
    public bool ValveOpen { get; private set; }

    /// <summary>Direction changes since the reset.</summary>
    public int Switches { get; private set; }

    /// <summary>Valve openings since the reset.</summary>
    public int ValveOpenings { get; private set; }

    /// <summary>True once the direction went back to both directions since the reset (the drift faded).</summary>
    public bool Faded { get; private set; }

    /// <summary>The Dec direction algorithm moves may use now, null for both (not yet known, or the valve is open).</summary>
    public GuideDirection? Allowed => ValveOpen ? null : Direction switch
    {
        DecGuideDirection.North => GuideDirection.North,
        DecGuideDirection.South => GuideDirection.South,
        _ => null,
    };

    /// <summary>Recent Dec RMS (px) of the frames on the side the direction corrects (all while both), 0 before the first.</summary>
    public double RecentRmsPx => squareWeight > 0 ? Math.Sqrt(squareSum / squareWeight) : 0;

    /// <summary>The valve limit (px): <see cref="ValveMinPx"/> or <see cref="ValveRmsFactor"/> × the recent RMS.</summary>
    public double ValveLimitPx => Math.Max(ValveMinPx, ValveRmsFactor * RecentRmsPx);

    /// <summary>
    /// Forgets the direction, the valve and the RMS; with <paramref name="keepHold"/> not how long the drift has held (the
    /// Dec guide mode changed, the drift measured so far stays).
    /// </summary>
    public void Reset(bool keepHold = false)
    {
        Direction = DecGuideDirection.Both;
        ValveOpen = false;
        Switches = 0;
        ValveOpenings = 0;
        Faded = false;
        if (!keepHold)
        {
            holdingSince = null;
            holding = DecGuideDirection.Both;
        }

        unsupportedSince = null;
        beyondFrames = 0;
        valveFrames = 0;
        squareSum = 0;
        squareWeight = 0;
        framesSinceSummary = 0;
    }

    /// <summary>Notes since the last call, oldest first.</summary>
    public IReadOnlyList<DecDirectionNote> TakeNotes()
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var taken = notes.ToArray();
        notes.Clear();
        return taken;
    }

    /// <summary>
    /// One guide frame: <paramref name="drift"/> is the current estimate (null without enough data),
    /// <paramref name="decOffsetPx"/> the measured Dec offset (mount px, positive = corrected by South pulses).
    /// While <paramref name="settling"/> the valve waits (the guider allows both directions anyway) and the frame doesn't
    /// count for the recent RMS. <paramref name="pixelScale"/> (″/px) is for the notes.
    /// </summary>
    public void Update(DateTimeOffset now, DriftEstimate? drift, double decOffsetPx, bool settling, double pixelScale)
    {
        UpdateDirection(now, drift, pixelScale);
        if (double.IsFinite(decOffsetPx))
        {
            UpdateValve(decOffsetPx, settling);

            // the recent RMS leaves out the side the direction cannot correct: an error creeping away there must not
            // raise its own limit
            bool uncorrectable = Direction == DecGuideDirection.South ? decOffsetPx < 0 : Direction == DecGuideDirection.North && decOffsetPx > 0;
            if (!settling && !ValveOpen && !uncorrectable)
            {
                const double lambda = 1.0 - 1.0 / RmsFrames;
                squareSum = lambda * squareSum + decOffsetPx * decOffsetPx;
                squareWeight = lambda * squareWeight + 1;
            }
        }

        if (++framesSinceSummary >= SummaryFrames)
        {
            framesSinceSummary = 0;
            notes.Add(new DecDirectionNote(DecDirectionNoteKind.Summary, string.Create(Inv,
                $"{DriftText(drift, pixelScale)}, {DirectionText(Direction)}, {Switches} switch{(Switches == 1 ? "" : "es")}, valve {(ValveOpen ? "open" : "closed")} (opened {ValveOpenings}×), recent Dec RMS {RecentRmsPx:F2} px")));
        }
    }

    /// <summary>
    /// A guide frame in another Dec guide mode: only keeps track of how long the drift has held, so that choosing
    /// <see cref="DecGuideMode.Drift"/> picks the direction at once when it has held for <see cref="HoldFor"/> already.
    /// </summary>
    public void Observe(DateTimeOffset now, DriftEstimate? drift)
    {
        bool holds = drift is { } d && Holds(d);
        TrackHold(now, holds, holds ? Counter(drift!.Value.PxPerSec) : DecGuideDirection.Both);
    }

    /// <summary>Notes a reset for the logs (the caller resets the drift estimate).</summary>
    public void NoteReset(string reason) =>
        notes.Add(new DecDirectionNote(DecDirectionNoteKind.Reset, $"{reason}: both directions until a clear Dec drift is measured"));

    /// <summary>The pulse direction that counters a drift: South for a positive one, North for a negative one.</summary>
    public static DecGuideDirection Counter(double driftPxPerSec) => driftPxPerSec > 0 ? DecGuideDirection.South : DecGuideDirection.North;

    /// <summary>
    /// True when <paramref name="drift"/> is strong enough to guide one direction only: significant
    /// (<see cref="SignificanceSigmas"/>) and |drift| − σ ≥ its floor (the frame noise per <see cref="DriftFloorSec"/>).
    /// </summary>
    public static bool Holds(DriftEstimate drift) =>
        drift.PxPerSec != 0 && drift.Sigmas >= SignificanceSigmas
        && Math.Abs(drift.PxPerSec) - FloorSigmas * drift.SigmaPxPerSec >= FloorPxPerSec(drift);

    /// <summary>The weakest drift that holds (px/s): the frame noise per <see cref="DriftFloorSec"/>.</summary>
    public static double FloorPxPerSec(DriftEstimate drift) => double.IsFinite(drift.NoisePx) ? drift.NoisePx / DriftFloorSec : 0;

    /// <summary>
    /// True when <paramref name="drift"/> keeps <paramref name="direction"/>: in its favour by <see cref="KeepSigmas"/>, and
    /// that much stronger it would reach the floor.
    /// </summary>
    public static bool Supports(DriftEstimate drift, DecGuideDirection direction) =>
        drift.PxPerSec != 0 && drift.Sigmas >= KeepSigmas && Counter(drift.PxPerSec) == direction
        && Math.Abs(drift.PxPerSec) + KeepSigmas * drift.SigmaPxPerSec >= FloorPxPerSec(drift);

    private void UpdateDirection(DateTimeOffset now, DriftEstimate? drift, double pixelScale)
    {
        bool holds = drift is { } d && Holds(d);
        var counter = holds ? Counter(drift!.Value.PxPerSec) : DecGuideDirection.Both;
        if (Direction == DecGuideDirection.Both)
        {
            TrackHold(now, holds, counter);
            if (holds && now - holdingSince!.Value >= HoldFor)
            {
                Change(counter, $"guiding {counter} only: {DriftText(drift, pixelScale)}");
            }

            return;
        }

        if (drift is { } current && Supports(current, Direction))
        {
            unsupportedSince = null;
            return;
        }

        unsupportedSince ??= now;
        if (now - unsupportedSince.Value < SwitchAfter)
        {
            return;
        }

        var was = Direction;
        string since = string.Create(Inv, $"the drift has not favoured {was} for {SwitchAfter.TotalMinutes:F0} min");
        Change(counter, counter == DecGuideDirection.Both
            ? $"both directions ({since}): {DriftText(drift, pixelScale)}"
            : $"the drift reversed, guiding {counter} only ({since}): {DriftText(drift, pixelScale)}");
    }

    // since when the drift has held for the same direction (null while it doesn't)
    private void TrackHold(DateTimeOffset now, bool holds, DecGuideDirection counter)
    {
        if (counter != holding)
        {
            holding = counter;
            holdingSince = holds ? now : null;
        }
    }

    private void Change(DecGuideDirection next, string message)
    {
        Faded |= next == DecGuideDirection.Both;
        Direction = next;
        Switches++;
        holding = DecGuideDirection.Both;
        holdingSince = null;
        unsupportedSince = null;
        beyondFrames = 0;
        if (ValveOpen)
        {
            // the error the valve was bringing back is on a side the new direction corrects (or both do)
            ValveOpen = false;
            valveFrames = 0;
        }

        notes.Add(new DecDirectionNote(DecDirectionNoteKind.Switch, message));
    }

    private void UpdateValve(double offset, bool settling)
    {
        if (Direction == DecGuideDirection.Both)
        {
            beyondFrames = 0;
            ValveOpen = false;
            return;
        }

        // the side the allowed direction cannot correct: South pulses correct positive offsets only
        double forbidden = Direction == DecGuideDirection.South ? -offset : offset;
        if (ValveOpen)
        {
            valveFrames++;
            if (forbidden <= 0)
            {
                ValveOpen = false;
                notes.Add(new DecDirectionNote(DecDirectionNoteKind.ValveClosed, string.Create(Inv,
                    $"safety valve closed after {valveFrames} frames: the Dec error is back ({offset:+0.00;-0.00} px), guiding {Direction} only again")));
                valveFrames = 0;
            }

            return;
        }

        double limit = ValveLimitPx;
        beyondFrames = !settling && forbidden > limit ? beyondFrames + 1 : 0;
        if (beyondFrames >= ValveFrames)
        {
            ValveOpen = true;
            ValveOpenings++;
            beyondFrames = 0;
            valveFrames = 0;
            var side = Direction == DecGuideDirection.South ? DecGuideDirection.North : DecGuideDirection.South;
            notes.Add(new DecDirectionNote(DecDirectionNoteKind.ValveOpened, string.Create(Inv,
                $"safety valve: the Dec error stayed beyond {limit:F2} px on the {side} side for {ValveFrames} frames ({offset:+0.00;-0.00} px), both directions until it is back")));
        }
    }

    private static string DirectionText(DecGuideDirection d) => d == DecGuideDirection.Both ? "both directions" : $"{d} only";

    private static string DriftText(DriftEstimate? drift, double pixelScale)
    {
        if (drift is not { } d)
        {
            return "Dec drift not known yet";
        }

        // positive: the mount drifts the way North pulses move it
        string towards = d.PxPerSec > 0 ? "North" : "South";
        double perMin = Math.Abs(d.PxPerSec) * 60;
        string rate = pixelScale > 0
            ? string.Create(Inv, $"{perMin * pixelScale:F2}″/min ({perMin:F3} px/min)")
            : string.Create(Inv, $"{perMin:F3} px/min");
        string sigmas = double.IsPositiveInfinity(d.Sigmas) ? "exact" : string.Create(Inv, $"{d.Sigmas:F1} σ");
        string floor = string.Create(Inv, $"{FloorPxPerSec(d) * 60:F3} px/min");
        return string.Create(Inv, $"Dec drift {rate} towards {towards} ({sigmas}, floor {floor}, {d.SpanSec / 60:F1} min)");
    }
}
