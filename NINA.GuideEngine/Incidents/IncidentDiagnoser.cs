// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Incidents;

/// <summary>
/// Names the likely cause of a recorded incident from its telemetry, with the rules of docs/INCIDENTS.md §1 "Diagnosis" in
/// their order (the first match wins, else <see cref="IncidentCause.Unclear"/>). Pure: the result depends only on the incident.
/// </summary>
/// <remarks>
/// Every result is worded as "likely" and carries evidence items (a code, parameters for localisation and the frame they
/// point at); the diagnosis-level parameters repeat those of the first (main) evidence item. Frames of deliberate moves
/// (settling, dithering, Coach measurements, calibration steps) are left out wherever such a move would look like the fault.
/// </remarks>
internal static class IncidentDiagnoser
{
    // general
    private const int LookaheadFrames = 3; // frames after the trigger that still show its cause: the mount snapshot precedes the exposure, a jumped star is found again
    private const double CauseWindowSeconds = 30; // camera failures, frame gaps and the deepest point of a fade this close to the first trigger belong to it
    private const double LookbackSeconds = 120; // the brightness before a fade is looked for up to 2 minutes back (the recorder's PreSeconds)
    private const double LockTolerancePx = 0.01; // lock positions are set exactly: any change is a dither or a new lock

    // camera
    private const double FrameGapFactor = 3; // a frame 3× later than the usual cadence is missing, not jitter
    private const double FrameGapMinSeconds = 5; // ... and at least 5 s late: slow downloads and processing stay below that

    // calibration mismatch
    private const int ErrorGrewSteps = 4; // "the error grew over ≥ 4 frames"
    private const double ErrorGrewMinPx = 3; // the runaway detector's minimum error: smaller growth is seeing
    private const double ErrorGrewFactor = 2; // the error at least doubled
    private const double ErrorGrewAcceleration = 1.3; // wrong-sign pulses add to the error, so it grows faster and faster; a drift grows it evenly or slower
    private const double ErrorGrewCorrectionShare = 0.75; // the error grew by at least 3/4 of what the pulses should have removed: they pushed instead of pulled

    // mount not moving
    private const int NoMotionPulses = 3; // SafetySettings.NotRespondingPulses
    private const double NoMotionMinExpectedPx = 3; // SafetySettings.NotRespondingMinExpectedPx: smaller pulses drown in seeing
    private const double NoMotionFraction = 0.25; // SafetySettings.NotRespondingFraction
    private const int DecBacklashPulses = 2; // extra Dec pulses: backlash can swallow the first pulses after a reversal
    private const double SeeingPx = 1; // the direction of a smaller star motion means nothing (seeing)

    // field jump
    private const double JumpMinPx = 3; // smaller moves are seeing or wind shake
    private const double JumpRegionFraction = 1.0 / 3; // ... or a third of the search region when that is smaller
    private const double JumpTolerancePx = 1.5; // secondaries within 1.5 px of the primary's move moved with it
    private const int JumpMinSecondaries = 2; // two other stars rule out a hot pixel or a neighbour star
    private const int JumpMaxLostFrames = 10; // a jump loses the star (beyond its search box, or smeared by a jump during the exposure) until the guider finds it again
    private const double JumpMaxLostSeconds = 30; // ... within the reacquire's full-frame searches, which start after 10 s
    private const int JumpShortLossFrames = 3; // secondaries measured when the star is back only count after a short loss: a longer one can hide a drift
    private const int JumpRecheckFrames = 8; // PHD2 skips the secondaries after a large primary move; they are measured again once the (max-length) corrections brought the star back
    private const double JumpDriftFactor = 3; // a jump moves the field at least 3× farther than the drift the corrections before a loss fought would in that time
    private const double JumpRateTolerance = 0.15; // calibrated rates are good to about 15 %, so is the predicted motion of the pulses

    // clouds
    private const double FadeFraction = 0.5; // "fell by ≥ 50 %"
    private const double CloudsMaxFadeSeconds = 60; // clouds dim the field within a minute; slower fades are dew or haze
    private const double UndimmedFraction = 0.8; // within 20 % of the peak the fade has not started (SNR scatters by 10-20 % per frame)
    private const double TroughFraction = 0.1; // the fade ends where the brightness is within 10 % (of the drop) of its deepest point
    private const double RecoveredFraction = 0.7; // back to 70 % of the brightness before the fade counts as recovered
    private const double TotalLossFraction = 0.9; // a 90 % drop leaves nothing to guide on

    // dew
    private const int DewWindowFrames = 5; // frames whose median gives the brightness and HFD at the start and at the end
    private const double DewMinFade = 0.25; // every star at least 25 % fainter
    private const double DewHfdGrowth = 0.3; // "HFD grows by ≥ 30 %"
    private const double DewMinSeconds = 60; // "over minutes"

    // guide star only
    private const int ReferenceFrames = 10; // frames before the event that give a star's normal brightness
    private const int GuideStarLookbackFrames = 3; // frames before the trigger in which the primary's trouble may have started
    private const double PrimaryChangeFraction = 0.5; // the primary's SNR or mass changed by ≥ 50 %
    private const double SecondaryNormalFraction = 0.3; // a secondary within ±30 % of its brightness stayed normal
    private const double SecondaryNormalShare = 2.0 / 3; // ... and at least two thirds of them did (a faint one may flicker)

    // drift too fast
    private const double DriftRegionFraction = 0.7; // "≥ 70 % of the search region"
    private const int DriftMinFrames = 5; // "over ≥ 5 frames"
    private const double DriftNoisePx = 0.3; // a step back by less than this is centroid noise, not the end of the drift

    // periodic spike
    private const double PeriodTolerance = 0.05; // "within ±5 %" of the multiple
    private const int MaxPeriodMultiple = 3; // "whole multiples (1-3)"
    private const int MinEarlierSpikes = 2; // "≥ 2 earlier spikes"
    private const int SpikeRmsFrames = 50; // IncidentSettings.SpikeWindow

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly GuideAxis[] Axes = [GuideAxis.Ra, GuideAxis.Dec];
    private static readonly char[] WordSeparators = [' ', ',', '.', ':', ';', '(', ')', '-', '/'];

    private static readonly Dictionary<string, (string Message, string Part)> MountStates = new()
    {
        ["mountSlewing"] = ("The mount reported slewing.", "it was slewing"),
        ["mountTrackingOff"] = ("The mount reported tracking off.", "tracking was off"),
        ["mountParked"] = ("The mount reported parked.", "it was parked"),
        ["mountDisconnected"] = ("The mount was disconnected.", "it was disconnected"),
    };

    /// <summary>Diagnoses an incident. Incomplete telemetry (no stars, no mount, no calibration, few frames) never throws.</summary>
    public static IncidentDiagnosis Diagnose(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        var a = new Analysis(incident);
        return Camera(a) ?? MountMoved(a) ?? CalibrationMismatch(a) ?? MountNotMoving(a) ?? FieldJump(a) ?? Clouds(a) ?? Dew(a)
            ?? GuideStarOnly(a) ?? DriftTooFast(a) ?? PeriodicSpike(a) ?? Unclear(a);
    }

    // Camera: a camera-failure incident, camera failures next to the first trigger, or frames missing around it.
    private static IncidentDiagnosis? Camera(Analysis a)
    {
        bool cameraIncident = a.Incident.Kind == IncidentKind.CameraFailure;
        var failures = a.Triggers
            .Where(t => IsCameraTrigger(t) && (cameraIncident || Math.Abs((t.Time - a.TriggerTime).TotalSeconds) <= CauseWindowSeconds))
            .ToList();
        var gap = FrameGap(a);
        if (!cameraIncident && failures.Count == 0 && gap is null)
        {
            return null;
        }

        var evidence = new List<IncidentEvidence>();
        var parts = new List<string>();
        if (cameraIncident || failures.Count > 0)
        {
            int count = Math.Max(1, failures.Count);
            string text = count == 1 ? "a capture failure" : $"{count} capture failures";
            evidence.Add(Ev("cameraFailures", $"The camera reported {text}.", failures.Count > 0 ? failures[0].Frame : a.TriggerFrame, ("count", count)));
            parts.Add(text);
        }

        if (gap is { } g)
        {
            evidence.Add(Ev("frameGap", $"No frame arrived for {F(g.Seconds)} s.", g.Frame, ("seconds", R1(g.Seconds))));
            parts.Add($"no frame for {F(g.Seconds)} s");
        }

        return Result(IncidentCause.Camera, $"Likely a camera problem: {JoinAnd(parts)}.", evidence);
    }

    private static bool IsCameraTrigger(IncidentTrigger t) =>
        t.Kind == IncidentKind.CameraFailure || t.Code is GuideErrorCode.CameraCaptureFailed or GuideErrorCode.CameraReconnecting or GuideErrorCode.CameraFailed;

    /// <summary>The longest pause between two frames around the first trigger that is far beyond the usual cadence.</summary>
    private static (double Seconds, long Frame)? FrameGap(Analysis a)
    {
        var fr = a.Frames;
        var intervals = new List<double>();
        for (int i = 1; i < fr.Count; i++)
        {
            double dt = (fr[i].Time - fr[i - 1].Time).TotalSeconds;
            if (dt > 0)
            {
                intervals.Add(dt);
            }
        }

        if (intervals.Count < 2)
        {
            return null;
        }

        double usual = Median(intervals);
        double limit = Math.Max(FrameGapFactor * usual, usual + FrameGapMinSeconds);
        (double Seconds, long Frame)? best = null;
        for (int i = 1; i < fr.Count; i++)
        {
            var p = fr[i - 1];
            var c = fr[i];
            double dt = (c.Time - p.Time).TotalSeconds;
            if (dt <= limit || (c.Time - a.TriggerTime).TotalSeconds < -CauseWindowSeconds || (p.Time - a.TriggerTime).TotalSeconds > CauseWindowSeconds)
            {
                continue;
            }

            // the recorder dropped these frames on purpose (a long calibration's middle, the gaps of an ongoing incident),
            // or calibration ended and guiding started
            bool dropped = a.Markers.Any(m => m.Type == IncidentMarkerType.Gap &&
                ((m.Time >= p.Time && m.Time <= c.Time) || (m.Frame is { } n && n >= p.Frame && n <= c.Frame)));
            if (dropped || (p.State == GuiderState.Calibrating) != (c.State == GuiderState.Calibrating))
            {
                continue;
            }

            if (best is null || dt > best.Value.Seconds)
            {
                best = (dt, c.Frame);
            }
        }

        return best;
    }

    // MountMoved: the mount snapshots (or the guider's mount alerts) up to the trigger show a slew, tracking off, park,
    // disconnect or a pier side change.
    private static IncidentDiagnosis? MountMoved(Analysis a)
    {
        var evidence = new List<IncidentEvidence>();
        var parts = new List<string>();

        void Add(string code, long? frame, string? message = null, string? part = null, params (string Key, object? Value)[] parameters)
        {
            if (evidence.Any(e => e.Code == code))
            {
                return;
            }

            var known = MountStates.GetValueOrDefault(code);
            evidence.Add(Ev(code, message ?? known.Message, frame, parameters));
            parts.Add(part ?? known.Part);
        }

        var side = PierSide.Unknown;
        for (int i = 0; i <= a.WindowEnd; i++)
        {
            var f = a.Frames[i];

            // INDI drivers can report "slewing" for seconds after the Coach's large measurement pulses
            if (f.Mount is not { } m || f.CoachMeasurement)
            {
                continue;
            }

            if (!m.IsConnected)
            {
                Add("mountDisconnected", f.Frame);
                continue;
            }

            if (m.IsParked)
            {
                Add("mountParked", f.Frame);
            }

            if (m.IsMoving)
            {
                Add("mountSlewing", f.Frame);
            }

            if (!m.IsTracking)
            {
                Add("mountTrackingOff", f.Frame);
            }

            if (m.PierSide == PierSide.Unknown)
            {
                continue;
            }

            if (side != PierSide.Unknown && m.PierSide != side)
            {
                Add("pierSideChanged", f.Frame, $"The pier side changed from {side} to {m.PierSide}.", $"the pier side changed from {side} to {m.PierSide}",
                    ("from", side.ToString()), ("to", m.PierSide.ToString()));
            }

            side = m.PierSide;
        }

        // the guider's own reading of the mount state, for telemetry without snapshots
        foreach (var t in a.Triggers.Where(a.InWindow))
        {
            string? code = t.Code switch
            {
                GuideErrorCode.MountSlewing => "mountSlewing",
                GuideErrorCode.MountParked => "mountParked",
                GuideErrorCode.MountTrackingOff => "mountTrackingOff",
                GuideErrorCode.MountDisconnected => "mountDisconnected",
                _ => null,
            };
            if (code is not null)
            {
                Add(code, t.Frame);
            }
        }

        return evidence.Count == 0 ? null : Result(IncidentCause.MountMoved, $"Likely the mount moved: {JoinAnd(parts)}.", evidence);
    }

    // CalibrationMismatch: a runaway or a Dec-flip correction, or the error grew over ≥ 4 frames while pulses were sent against it.
    private static IncidentDiagnosis? CalibrationMismatch(Analysis a)
    {
        var runaway = a.Triggers.FirstOrDefault(t => t.Kind == IncidentKind.Runaway || t.Code == GuideErrorCode.RunawayDetected);
        var decFlip = a.Triggers.FirstOrDefault(t => t.Kind == IncidentKind.DecFlipCorrected || t.Code == GuideErrorCode.DecFlipCorrected);

        // with such a trigger the whole incident may show the growth (a runaway stops guiding at the end of it)
        var grew = ErrorGrew(a, runaway is not null || decFlip is not null ? a.Frames.Count - 1 : a.WindowEnd);
        if (runaway is null && decFlip is null && grew is null)
        {
            return null;
        }

        var evidence = new List<IncidentEvidence>();
        var parts = new List<string>();
        if (runaway is not null)
        {
            var axis = AxisFromText(runaway.Detail) ?? AxisFromText(runaway.Message) ?? grew?.Axis;
            string text = axis is { } x ? $"runaway guiding in {AxisLabel(x)}" : "runaway guiding";
            evidence.Add(Ev("runaway", $"The guider detected {text}.", runaway.Frame, ("axis", axis is { } y ? AxisName(y) : null)));
            parts.Add(text);
        }

        if (decFlip is not null)
        {
            evidence.Add(Ev("decFlipCorrected", "Dec corrections made the error grow after the meridian flip; the guider inverted Dec.", decFlip.Frame));
            parts.Add("the Dec direction was wrong after the meridian flip");
        }

        if (grew is { } g)
        {
            string text = $"the {AxisLabel(g.Axis)} error grew from {F(g.FromPx)} to {F(g.ToPx)} px over {g.Steps} frames while corrections were sent against it";
            evidence.Add(Ev("errorGrew", Sentence(text), g.Frame,
                ("frames", g.Steps), ("fromPx", R1(g.FromPx)), ("toPx", R1(g.ToPx)), ("axis", AxisName(g.Axis))));
            parts.Add(text);
        }

        return Result(IncidentCause.CalibrationMismatch, $"Likely a calibration mismatch: {JoinAnd(parts)}.", evidence);
    }

    private sealed record Growth(GuideAxis Axis, int Steps, double FromPx, double ToPx, long Frame);

    /// <summary>
    /// The largest run of guide frames in which an axis error grew at every frame with unchanged sign although each frame
    /// sent an unlimited pulse against it (limited pulses are a drift the guider cannot keep up with, see DriftTooFast).
    /// </summary>
    private static Growth? ErrorGrew(Analysis a, int end)
    {
        Growth? best = null;
        var fr = a.Frames;
        foreach (var axis in Axes)
        {
            int start = -1;
            double correction = 0;
            for (int i = 0; i <= end; i++)
            {
                if (a.GuideError(fr[i], axis) is not { } e)
                {
                    start = -1;
                    continue;
                }

                if (start < 0 || !GrewAgainstPulse(a, fr[i - 1], fr[i], axis, out double expected))
                {
                    start = i;
                    correction = 0;
                    continue;
                }

                correction += expected;
                int steps = i - start;
                if (steps < ErrorGrewSteps)
                {
                    continue;
                }

                double from = Math.Abs(a.GuideError(fr[start], axis)!.Value);
                double to = Math.Abs(e);
                double early = Math.Abs(a.GuideError(fr[start + 2], axis)!.Value) - from;
                double late = to - Math.Abs(a.GuideError(fr[i - 2], axis)!.Value);
                bool pushed = double.IsNaN(correction) || to - from >= ErrorGrewCorrectionShare * correction;
                if (to >= ErrorGrewMinPx && to >= ErrorGrewFactor * from && late >= ErrorGrewAcceleration * early && pushed &&
                    (best is null || to > best.ToPx))
                {
                    best = new Growth(axis, steps, from, to, fr[start].Frame);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The error of <paramref name="f"/> grew from <paramref name="p"/> with the same sign, and <paramref name="p"/> sent an
    /// unlimited pulse against it. <paramref name="expectedPx"/> is that pulse's expected correction (NaN when unknown).
    /// </summary>
    private static bool GrewAgainstPulse(Analysis a, IncidentFrameRecord p, IncidentFrameRecord f, GuideAxis axis, out double expectedPx)
    {
        expectedPx = double.NaN;
        double ep = a.GuideError(p, axis)!.Value;
        double e = a.GuideError(f, axis)!.Value;
        var (ms, direction, limited) = Pulse(p, axis);
        int maxMs = a.MaxDurationMs(axis);
        if (ep == 0 || Math.Sign(e) != Math.Sign(ep) || Math.Abs(e) <= Math.Abs(ep) || !SameLock(p, f) || ms <= 0 || limited ||
            (maxMs > 0 && ms >= maxMs) || direction != Against(ep, axis))
        {
            return false;
        }

        expectedPx = ExpectedChange(ms, direction, a.Rate(axis, p)) is { } c ? Math.Abs(c) : double.NaN;
        return true;
    }

    // MountNotMoving: MountNotResponding, or ≥ 3 pulses whose expected motion (duration × rate) is ≥ 3 px but the star
    // moved < 25 % of it. Calibration steps are judged per run of steps in one direction.
    private static IncidentDiagnosis? MountNotMoving(Analysis a)
    {
        var trigger = a.Triggers.FirstOrDefault(t => t.Kind == IncidentKind.MountNotResponding || t.Code == GuideErrorCode.MountNotResponding);
        int end = trigger is null ? a.WindowEnd : Math.Min(a.Frames.Count - 1, Math.Max(a.WindowEnd, a.IndexOf(trigger) + LookaheadFrames));
        var run = GuidePulsesWithoutMotion(a, end) ?? CalibrationStepsWithoutMotion(a, end);
        if (run is { } r)
        {
            string moved = r.MovedPx <= -SeeingPx ? $"{F(-r.MovedPx)} px the other way" : $"{F(Math.Abs(r.MovedPx))} px";
            string text = $"{r.Pulses} {AxisLabel(r.Axis)} pulses should have moved the star {F(r.ExpectedPx)} px, it moved {moved}";
            var evidence = Ev("pulsesWithoutMotion", Sentence(text), r.Frame,
                ("pulses", r.Pulses), ("expectedPx", R1(r.ExpectedPx)), ("movedPx", R1(r.MovedPx)), ("axis", AxisName(r.Axis)));
            return Result(IncidentCause.MountNotMoving, $"Likely the mount is not moving: {text}.", [evidence]);
        }

        if (trigger is null)
        {
            return null;
        }

        // the guider judges RA pulses only (Dec pulses may be absorbed by backlash); the frames did not show the pulses
        var fromTrigger = Ev("pulsesWithoutMotion", "The mount did not respond to RA corrections.", trigger.Frame,
            ("pulses", null), ("expectedPx", null), ("movedPx", null), ("axis", AxisName(GuideAxis.Ra)));
        return Result(IncidentCause.MountNotMoving, "Likely the mount is not moving: it did not respond to RA corrections.", [fromTrigger]);
    }

    private sealed record NoMotion(GuideAxis Axis, int Pulses, double ExpectedPx, double MovedPx, long Frame);

    /// <summary>
    /// The longest run of large guide pulses on an axis that did not move the star (the pulse sent after frame i shows in
    /// frame i+1). Like the guider's monitor, small pulses and frames without a pulse don't break a run, a pulse that moved
    /// the star does; a Dec reversal also starts over (backlash).
    /// </summary>
    private static NoMotion? GuidePulsesWithoutMotion(Analysis a, int end)
    {
        NoMotion? best = null;
        var fr = a.Frames;
        foreach (var axis in Axes)
        {
            int needed = axis == GuideAxis.Ra ? NoMotionPulses : NoMotionPulses + DecBacklashPulses;
            int count = 0;
            double expectedSum = 0;
            double movedSum = 0;
            long first = 0;
            GuideDirection? runDirection = null;
            for (int i = 0; i < end; i++)
            {
                var f = fr[i];
                var g = fr[i + 1];
                if (!Judgeable(f) || !Judgeable(g))
                {
                    continue;
                }

                var (ms, direction, _) = Pulse(f, axis);
                if (ExpectedChange(ms, direction, a.Rate(axis, f)) is not { } expected || Math.Abs(expected) < NoMotionMinExpectedPx ||
                    a.AxisMotion(f, g, axis) is not { } motion)
                {
                    continue;
                }

                double along = motion * Math.Sign(expected);
                bool reversal = axis == GuideAxis.Dec && runDirection is not null && direction != runDirection;
                bool moved = along >= NoMotionFraction * Math.Abs(expected);
                if (reversal || moved)
                {
                    count = 0;
                    expectedSum = 0;
                    movedSum = 0;
                    runDirection = null;
                }

                if (moved)
                {
                    continue;
                }

                if (count == 0)
                {
                    first = f.Frame;
                    runDirection = direction;
                }

                count++;
                expectedSum += Math.Abs(expected);
                movedSum += along;
                if (count >= needed && (best is null || count > best.Pulses))
                {
                    best = new NoMotion(axis, count, expectedSum, movedSum, first);
                }
            }
        }

        return best;

        // guide frames with a measured star; dithers and Coach measurements move the lock or the star on purpose
        static bool Judgeable(IncidentFrameRecord f) => Measured(f) && !f.Dithering && !f.CoachMeasurement && f.CalibrationDirection is null;
    }

    /// <summary>
    /// Calibration: a run of ≥ 3 steps in one direction whose summed expected motion is ≥ 3 px while the star moved less
    /// than 25 % of it in any direction (single steps are often below 3 px; the axis angles of an earlier calibration may
    /// no longer apply). Backlash-clearing steps are not expected to move the star.
    /// </summary>
    private static NoMotion? CalibrationStepsWithoutMotion(Analysis a, int end)
    {
        NoMotion? best = null;
        var fr = a.Frames;
        int i = 0;
        while (i <= end)
        {
            var axis = CalibrationAxis(fr[i].CalibrationDirection);
            if (axis is null)
            {
                i++;
                continue;
            }

            int start = i;
            while (i + 1 <= end && string.Equals(fr[i + 1].CalibrationDirection, fr[start].CalibrationDirection, StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }

            int last = i;
            i++;

            // the motion of the steps from the first step to the frame after the last one (or the group's last frame)
            int stop = last + 1 < fr.Count && fr[last + 1].Star is { IsValid: true } ? last + 1 : last;
            int steps = 0;
            double expectedSum = 0;
            bool known = true;
            int firstStep = -1;
            for (int j = start; j < stop; j++)
            {
                var (ms, d, _) = Pulse(fr[j], axis.Value);
                if (ms <= 0)
                {
                    continue;
                }

                if (firstStep < 0)
                {
                    firstStep = j;
                }

                steps++;
                if (ExpectedChange(ms, d, a.Rate(axis.Value, fr[j])) is { } e)
                {
                    expectedSum += Math.Abs(e);
                }
                else
                {
                    known = false;
                }
            }

            if (steps < NoMotionPulses || !known || expectedSum < NoMotionMinExpectedPx ||
                fr[firstStep].Star is not { IsValid: true } p0 || fr[stop].Star is not { IsValid: true } p1)
            {
                continue;
            }

            double moved = p0.Distance(p1);
            if (moved < NoMotionFraction * expectedSum && (best is null || steps > best.Pulses))
            {
                best = new NoMotion(axis.Value, steps, expectedSum, moved, fr[firstStep].Frame);
            }
        }

        return best;
    }

    private static GuideAxis? CalibrationAxis(string? direction) => direction?.ToLowerInvariant() switch
    {
        "west" or "east" => GuideAxis.Ra,
        "north" or "south" => GuideAxis.Dec,
        _ => null,
    };

    // FieldJump: the primary and ≥ 2 secondaries moved by the same vector (within 1.5 px) of ≥ 3 px (or ≥ ⅓ search region)
    // between two consecutive frames, beyond what the pulse sent in between explains. The star may be lost in between (then
    // the secondaries are judged on the loss frames); when the secondaries were not measured on the jump frame, they are
    // judged where they are measured again (see SecondariesAfterJump).
    private static IncidentDiagnosis? FieldJump(Analysis a)
    {
        var fr = a.Frames;
        double minJump = Math.Min(JumpMinPx, JumpRegionFraction * a.SearchRegionPx);
        (double Px, int Stars, long Frame, double LostSeconds)? best = null;
        for (int i = 0; i <= Math.Min(a.TriggerIndex, fr.Count - 2); i++)
        {
            var f = fr[i];
            if (Deliberate(f) || !Measured(f) || PrimaryPosition(f) is not { } p0)
            {
                continue;
            }

            // the next frame with the star, across a loss
            int j = i + 1;
            while (j < fr.Count && !fr[j].StarFound && !Deliberate(fr[j]) && j - i <= JumpMaxLostFrames && (fr[j].Time - f.Time).TotalSeconds <= JumpMaxLostSeconds)
            {
                j++;
            }

            if (j >= fr.Count || Deliberate(fr[j]) || !fr[j].StarFound || (fr[j].Time - f.Time).TotalSeconds > JumpMaxLostSeconds ||
                PrimaryPosition(fr[j]) is not { } p1 || a.ExpectedCameraMotion(f) is not { } expected)
            {
                continue;
            }

            double rx = p1.X - p0.X;
            double ry = p1.Y - p0.Y;
            double jump = Math.Sqrt(((rx - expected.X) * (rx - expected.X)) + ((ry - expected.Y) * (ry - expected.Y)));
            if (Math.Sqrt((rx * rx) + (ry * ry)) < minJump || jump < minJump)
            {
                continue;
            }

            int lost = j - i - 1;
            var before = Analysis.StarsOf(f).Where(MeasuredSecondary).ToList();
            var after = Analysis.StarsOf(fr[j]).Where(MeasuredSecondary).ToList();
            int together;
            if (lost == 0)
            {
                together = after.Count >= JumpMinSecondaries ? MovedBy(before, after, rx, ry) : SecondariesAfterJump(a, i, j, rx, ry, expected);
            }
            else if (!PrimaryUnchanged(a, f, fr[j]))
            {
                // the star found again is not the one lost (or not as it was)
                continue;
            }
            else
            {
                // on the loss frames the secondaries may have been measured at their new places already (the primary-dropout
                // search); where the star is back, only after a short loss
                together = Enumerable.Range(i + 1, lost).Max(k => MovedBy(before, Analysis.StarsOf(fr[k]).Where(MeasuredSecondary).ToList(), rx, ry));
                if (lost <= JumpShortLossFrames && after.Count >= JumpMinSecondaries)
                {
                    together = Math.Max(together, MovedBy(before, after, rx, ry));
                }

                // a jump beyond the search boxes loses the secondaries too: judge them where they are measured again, when the
                // loss was sudden, the field did not stay where it was, and no drift explains the move
                if (together < JumpMinSecondaries && SuddenJumpLoss(a, i, j, before, expected, jump))
                {
                    together = SecondariesAfterJump(a, i, j, rx, ry, expected);
                }
            }

            if (together >= JumpMinSecondaries && (best is null || jump > best.Value.Px))
            {
                best = (jump, together + 1, fr[lost > 0 ? i + 1 : j].Frame, lost > 0 ? (fr[j].Time - fr[i + 1].Time).TotalSeconds : 0);
            }
        }

        if (best is not { } b)
        {
            return null;
        }

        double? arcsec = b.Px * a.PixelScale;
        string size = arcsec is { } s ? $"{F(b.Px)} px ({F(s)}″)" : $"{F(b.Px)} px";
        string text = $"{b.Stars} stars jumped {size} at once";
        string lostText = b.LostSeconds > 0 ? $"; the guide star was lost for {F(b.LostSeconds)} s" : string.Empty;
        var evidence = Ev("fieldJump", Sentence(text + lostText), b.Frame, ("jumpPx", R1(b.Px)), ("jumpArcsec", R1(arcsec)), ("stars", b.Stars));
        return Result(IncidentCause.FieldJump, $"Likely a field jump (bump, wind or cable snag): {text}{lostText}.", [evidence]);
    }

    /// <summary>
    /// The star was lost between frames <paramref name="i"/> and <paramref name="j"/> the way a jump loses it: suddenly (not
    /// fading before), with no loss frame showing the secondaries still where the pulse alone put them, and farther
    /// (<paramref name="jumpPx"/>) than the drift the corrections before the loss fought would move it in that time (clouds
    /// with a drift).
    /// </summary>
    private static bool SuddenJumpLoss(Analysis a, int i, int j, List<StarInfo> before, GuidePoint expected, double jumpPx)
    {
        var fr = a.Frames;
        var reference = Enumerable.Range(0, i).Reverse().Where(k => Measured(fr[k]) && !Deliberate(fr[k])).Take(ReferenceFrames).ToList();
        if (MedianOrNull(reference.Select(k => a.PrimarySignal(fr[k]))) is { } normal && a.PrimarySignal(fr[i]) is { } last && last < UndimmedFraction * normal)
        {
            return false;
        }

        if (Enumerable.Range(i + 1, j - i - 1).Any(k => MovedBy(before, Analysis.StarsOf(fr[k]).Where(MeasuredSecondary).ToList(), expected.X, expected.Y) >= JumpMinSecondaries))
        {
            return false;
        }

        // the drift: what the pulses of the guide frames before the loss corrected, per second
        double mx = 0;
        double my = 0;
        foreach (int k in reference)
        {
            if (a.ExpectedCameraMotion(fr[k]) is not { } e)
            {
                return false;
            }

            mx += e.X;
            my += e.Y;
        }

        double span = reference.Count > 1 ? (fr[reference[0]].Time - fr[reference[^1]].Time).TotalSeconds : 0;
        double drift = span > 0 ? Math.Sqrt((mx * mx) + (my * my)) / span * (fr[j].Time - fr[i].Time).TotalSeconds : 0;
        return jumpPx >= JumpDriftFactor * drift;
    }

    /// <summary>How many of the <paramref name="before"/> stars are among <paramref name="after"/> moved by (<paramref name="rx"/>, <paramref name="ry"/>).</summary>
    private static int MovedBy(List<StarInfo> before, List<StarInfo> after, double rx, double ry) =>
        after.Count == 0 ? 0 : before.Count(s => after.Any(t => Distance(t, s.X + rx, s.Y + ry) <= JumpTolerancePx));

    /// <summary>The primary's SNR and mass in <paramref name="now"/> are within ±50 % of <paramref name="then"/> (unknown values pass).</summary>
    private static bool PrimaryUnchanged(Analysis a, IncidentFrameRecord then, IncidentFrameRecord now) =>
        Comparable(a.PrimarySignal(now), a.PrimarySignal(then)) && Comparable(Finite(now.StarMass), Finite(then.StarMass));

    private static bool Comparable(double? now, double? then) => now is not { } n || then is not { } t || !(t > 0) || Math.Abs((n / t) - 1) < PrimaryChangeFraction;

    /// <summary>
    /// The secondaries of frame <paramref name="i"/> that followed the primary's jump to frame <paramref name="j"/> although
    /// they were not measured there; 0 when this cannot be judged.
    /// </summary>
    /// <remarks>
    /// PHD2 lists them unmeasured at their old places on a frame whose primary moved far, and not at all on the next one.
    /// Where they are measured again, a field that jumped is at its place before the jump plus the jump (<paramref name="rx"/>,
    /// <paramref name="ry"/>) plus the motion of the pulses sent since, i.e. pulled back by the corrections; a primary that
    /// jumped alone (hot pixel, neighbour star) leaves them where the pulses alone put them (<paramref name="expected"/> = the
    /// motion of the pulse sent before the jump). Requires the primary's SNR and mass to be unchanged by the jump (else it is
    /// the guide star's own trouble).
    /// </remarks>
    private static int SecondariesAfterJump(Analysis a, int i, int j, double rx, double ry, GuidePoint expected)
    {
        var fr = a.Frames;
        var before = fr[i];
        var jumped = fr[j];
        if (!Measured(jumped) || !PrimaryUnchanged(a, before, jumped))
        {
            return 0;
        }

        var secondaries = Analysis.StarsOf(before).Where(MeasuredSecondary).ToList();
        double px = 0;
        double py = 0;
        for (int k = j; k < fr.Count && k - j <= JumpRecheckFrames; k++)
        {
            var f = fr[k];
            if (Deliberate(f))
            {
                return 0;
            }

            var measured = Analysis.StarsOf(f).Where(MeasuredSecondary).ToList();
            if (k > j && measured.Count >= JumpMinSecondaries)
            {
                double tolerance = JumpTolerancePx + (JumpRateTolerance * Math.Sqrt((px * px) + (py * py)));
                return secondaries.Count(s => measured.Any(t =>
                {
                    double withJump = Distance(t, s.X + rx + px, s.Y + ry + py);
                    return withJump <= tolerance && withJump < Distance(t, s.X + expected.X + px, s.Y + expected.Y + py);
                }));
            }

            // the pulses sent after this frame (none while the star is lost)
            if (a.ExpectedCameraMotion(f) is not { } motion)
            {
                return 0;
            }

            px += motion.X;
            py += motion.Y;
        }

        return 0;
    }

    private static double Distance(StarInfo s, double x, double y) => Math.Sqrt(((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y)));

    /// <summary>A secondary measured in its frame ("FallbackRejected" was measured too, only left out of the primary-dropout estimate).</summary>
    private static bool MeasuredSecondary(StarInfo s) =>
        !s.IsPrimary && s.RejectReason is not ("DroppedZero" or "Lost") && !TrackedStarStatusExtensions.IsUnmeasuredReason(s.RejectReason);

    // Clouds: all tracked stars' SNR (mass) fell together by ≥ 50 % within ≤ 60 s near the trigger and recovered afterwards,
    // or the stars were lost.
    private static IncidentDiagnosis? Clouds(Analysis a)
    {
        if (a.Brightness is not { } b)
        {
            return null;
        }

        var fr = a.Frames;
        var s = b.Smooth;

        // the deepest point near the trigger and the brightness before it
        int t = -1;
        for (int i = 0; i < fr.Count; i++)
        {
            if (s[i] is { } v && Math.Abs((fr[i].Time - a.TriggerTime).TotalSeconds) <= CauseWindowSeconds && (t < 0 || v < s[t]!.Value))
            {
                t = i;
            }
        }

        if (t < 0)
        {
            return null;
        }

        double trough = s[t]!.Value;
        int lookback = t;
        double peak = double.NaN;
        for (int i = t - 1; i >= 0 && (fr[t].Time - fr[i].Time).TotalSeconds <= LookbackSeconds; i--)
        {
            lookback = i;
            if (s[i] is { } v && !(v <= peak))
            {
                peak = v;
            }
        }

        if (double.IsNaN(peak) || trough > (1 - FadeFraction) * peak)
        {
            return null;
        }

        // fade start (last undimmed frame), half-way point and end (near the trough)
        int u = LastIndex(lookback, t - 1, i => s[i] >= UndimmedFraction * peak);
        int h = FirstIndex(u + 1, t, i => s[i] <= (1 - FadeFraction) * peak);
        int end = FirstIndex(u + 1, t, i => s[i] <= trough + (TroughFraction * (peak - trough)));
        if (u < 0 || h < 0 || (fr[h].Time - fr[u].Time).TotalSeconds > CloudsMaxFadeSeconds)
        {
            return null;
        }

        // every star faded, not only the field median: each against its brightness before the fade
        var (stars, minDrop) = b.Faded(lookback, u, u + 1, t);
        if (stars == 0 || stars < Math.Min(2, b.ReferenceStars) || minDrop < FadeFraction)
        {
            return null;
        }

        bool totalLoss = minDrop >= TotalLossFraction || (!fr[t].StarFound && !IsSaturated(fr[t]));
        int recovered = FirstIndex(t + 1, fr.Count - 1, i => s[i] >= RecoveredFraction * s[u]!.Value);
        if (recovered < 0 && !totalLoss)
        {
            return null;
        }

        double seconds = (fr[Math.Max(h, end)].Time - fr[u].Time).TotalSeconds;
        int drop = Pct(minDrop);
        string who = stars == 1 ? "the guide star" : $"all {stars} stars";
        string faded = $"{who} faded by {drop} % within {F(seconds)} s";
        var evidence = new List<IncidentEvidence>
        {
            Ev("starsFaded", Sentence(faded), fr[t].Frame, ("stars", stars), ("dropPercent", drop), ("seconds", R1(seconds))),
        };
        string outcome;
        if (recovered >= 0)
        {
            double after = (fr[recovered].Time - fr[h].Time).TotalSeconds;
            evidence.Add(Ev("recoveredAfter", $"{(stars == 1 ? "The guide star" : "The stars")} came back after {F(after)} s.", fr[recovered].Frame,
                ("seconds", R1(after))));
            outcome = $"came back after {F(after)} s";
        }
        else
        {
            outcome = stars == 1 ? "was lost" : "were lost";
        }

        return Result(IncidentCause.Clouds, $"Likely clouds: {faded} and {outcome}.", evidence);
    }

    // Dew: all stars fading over minutes while the HFD grows by ≥ 30 %.
    private static IncidentDiagnosis? Dew(Analysis a)
    {
        if (a.Brightness is not { } b)
        {
            return null;
        }

        var fr = a.Frames;
        var usable = Enumerable.Range(0, a.WindowEnd + 1).Where(i => b.Field[i] is not null && Measured(fr[i])).ToList();
        if (usable.Count < 2 * DewWindowFrames)
        {
            return null;
        }

        var start = usable.Take(DewWindowFrames).ToList();
        var late = usable.Skip(usable.Count - DewWindowFrames).ToList();
        double seconds = (fr[late[DewWindowFrames / 2]].Time - fr[start[DewWindowFrames / 2]].Time).TotalSeconds;
        if (seconds < DewMinSeconds)
        {
            return null;
        }

        var (stars, minDrop) = b.FadedBetween(start, late);
        if (stars == 0 || stars < Math.Min(2, b.ReferenceStars) || minDrop < DewMinFade)
        {
            return null;
        }

        double? hfdStart = MedianOrNull(start.Select(i => a.FrameHfd(fr[i])));
        double? hfdLate = MedianOrNull(late.Select(i => a.FrameHfd(fr[i])));
        if (hfdStart is not { } h0 || hfdLate is not { } h1 || h0 <= 0 || h1 / h0 - 1 < DewHfdGrowth)
        {
            return null;
        }

        int drop = Pct(minDrop);
        int growth = Pct(h1 / h0 - 1);
        double minutes = seconds / 60;
        string who = stars == 1 ? "the guide star" : $"all {stars} stars";
        long frame = fr[late[^1]].Frame;
        var evidence = new List<IncidentEvidence>
        {
            Ev("starsFadingSlowly", Sentence($"{who} faded by {drop} % over {F(minutes)} min"), frame,
                ("stars", stars), ("dropPercent", drop), ("minutes", R1(minutes))),
            Ev("hfdGrew", $"The star size (HFD) grew by {growth} %.", frame, ("percent", growth)),
        };
        return Result(IncidentCause.Dew, $"Likely dew: {who} faded by {drop} % over {F(minutes)} min while the HFD grew by {growth} %.", evidence);
    }

    // GuideStarOnly: the primary was lost or its SNR/mass jumped while the secondaries stayed normal.
    private static IncidentDiagnosis? GuideStarOnly(Analysis a)
    {
        var fr = a.Frames;
        var b = a.Brightness;
        for (int i = Math.Max(0, a.TriggerIndex - GuideStarLookbackFrames); i <= a.WindowEnd; i++)
        {
            var f = fr[i];
            if (f.CoachMeasurement)
            {
                continue;
            }

            var before = Enumerable.Range(0, i).Reverse().Where(j => Measured(fr[j]) && !fr[j].CoachMeasurement).Take(ReferenceFrames).ToList();
            if (before.Count == 0)
            {
                continue;
            }

            bool lost = !Measured(f);
            bool saturated = IsSaturated(f);
            double? refSignal = MedianOrNull(before.Select(j => a.PrimarySignal(fr[j])));
            double? refMass = MedianOrNull(before.Select(j => Finite(fr[j].StarMass)));
            double? change = !lost && a.PrimarySignal(f) is { } signal && refSignal is > 0 ? (signal / refSignal) - 1 : null;
            double? massChange = (!lost || IsMassChange(f)) && Finite(f.StarMass) is { } mass && refMass is > 0 ? (mass / refMass) - 1 : null;
            bool jumped = Math.Abs(change ?? 0) >= PrimaryChangeFraction || Math.Abs(massChange ?? 0) >= PrimaryChangeFraction;
            if (!lost && !jumped)
            {
                continue;
            }

            // the secondaries in this frame against their brightness before
            var (compared, normal) = b is null ? (0, 0) : b.SecondariesNormal(i, before, SecondaryNormalFraction);
            bool primaryOnly = compared > 0
                ? normal >= Math.Ceiling(SecondaryNormalShare * compared)
                : saturated || massChange >= PrimaryChangeFraction; // no secondary measured: only what is certainly the primary's own trouble
            if (!primaryOnly)
            {
                continue;
            }

            int? drop = lost ? (saturated ? null : 100) : change is { } c ? Pct(-c) : null;
            string what = lost
                ? "the guide star was lost"
                : Math.Abs(change ?? 0) >= PrimaryChangeFraction
                    ? change < 0 ? $"the guide star faded by {Pct(-change!.Value)} %" : $"the guide star brightened by {Pct(change!.Value)} %"
                    : $"the guide star's mass changed by {Signed(Pct(massChange!.Value))} %";
            string others = normal == 0 ? string.Empty : normal == 1 ? " while 1 other star stayed normal" : $" while {normal} other stars stayed normal";
            var details = new List<string>();
            var evidence = new List<IncidentEvidence>
            {
                Ev("primaryOnly", Sentence(what + others), f.Frame, ("dropPercent", drop), ("secondaries", normal)),
            };
            if (saturated)
            {
                evidence.Add(Ev("saturated", "The guide star was saturated.", f.Frame));
                details.Add("saturated");
            }

            if (massChange is { } mc && (Math.Abs(mc) >= PrimaryChangeFraction || IsMassChange(f)))
            {
                evidence.Add(Ev("massJump", $"The guide star's mass changed by {Signed(Pct(mc))} %.", f.Frame, ("percent", Pct(mc))));
                if (lost || Math.Abs(change ?? 0) >= PrimaryChangeFraction)
                {
                    details.Add($"mass {Signed(Pct(mc))} %");
                }
            }
            else if (IsMassChange(f))
            {
                evidence.Add(Ev("massJump", "The guide star's mass changed abruptly.", f.Frame, ("percent", null)));
                details.Add("mass changed");
            }

            string detail = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
            return Result(IncidentCause.GuideStarOnly, $"Likely a problem with the guide star only: {what}{detail}{others}.", evidence);
        }

        return null;
    }

    // DriftTooFast: the star drifted steadily to ≥ 70 % of the search region over ≥ 5 frames while the pulses were limited
    // or at the max duration.
    private static IncidentDiagnosis? DriftTooFast(Analysis a)
    {
        var fr = a.Frames;
        double edge = DriftRegionFraction * a.SearchRegionPx;
        (GuideAxis Axis, int Frames, int Limited, double ErrorPx, long Frame, long FirstLimited)? best = null;
        foreach (var axis in Axes)
        {
            int maxMs = a.MaxDurationMs(axis);
            int start = -1;
            for (int i = 0; i <= a.WindowEnd; i++)
            {
                if (a.GuideError(fr[i], axis) is not { } e)
                {
                    start = -1;
                    continue;
                }

                if (start >= 0)
                {
                    var p = fr[i - 1];
                    double ep = a.GuideError(p, axis)!.Value;
                    bool steady = e != 0 && Math.Sign(e) == Math.Sign(ep) && Math.Abs(e) >= Math.Abs(ep) - DriftNoisePx && SameLock(p, fr[i]) &&
                        AtLimit(p, axis, maxMs);
                    if (!steady)
                    {
                        start = i;
                    }
                }
                else
                {
                    start = i;
                }

                int frames = i - start + 1;
                double first = Math.Abs(a.GuideError(fr[start], axis)!.Value);
                if (frames < DriftMinFrames || Math.Abs(e) < edge || Math.Abs(e) <= first + DriftNoisePx || (best is { } bb && Math.Abs(e) <= bb.ErrorPx))
                {
                    continue;
                }

                var limitedFrames = Enumerable.Range(start, frames).Where(j => AtLimit(fr[j], axis, maxMs)).ToList();
                best = (axis, frames, limitedFrames.Count, Math.Abs(e), fr[i].Frame, fr[limitedFrames[0]].Frame);
            }
        }

        if (best is not { } d)
        {
            return null;
        }

        int percent = Pct(d.ErrorPx / a.SearchRegionPx);
        string axisLabel = AxisLabel(d.Axis);
        string drift = $"the star drifted steadily to {percent} % of the search region in {axisLabel} over {d.Frames} frames";
        string limited = $"{d.Limited} {axisLabel} pulses were at the limit";
        var evidence = new List<IncidentEvidence>
        {
            Ev("driftToEdge", Sentence(drift), d.Frame, ("percentOfRegion", percent), ("frames", d.Frames), ("axis", AxisName(d.Axis))),
            Ev("pulsesLimited", Sentence(limited), d.FirstLimited, ("frames", d.Limited), ("axis", AxisName(d.Axis))),
        };
        return Result(IncidentCause.DriftTooFast, $"Likely a drift too fast for the corrections: {drift} while {limited}.", evidence);
    }

    private static bool AtLimit(IncidentFrameRecord f, GuideAxis axis, int maxMs)
    {
        var (ms, _, limited) = Pulse(f, axis);
        return limited || (maxMs > 0 && ms >= maxMs);
    }

    // PeriodicSpike: a spike incident, the worm period is known and ≥ 2 earlier spikes of the session lie within ±5 % of
    // whole multiples (1-3) of it before this one.
    private static IncidentDiagnosis? PeriodicSpike(Analysis a)
    {
        if (a.Incident.Kind != IncidentKind.Spike || a.Context.WormPeriodSeconds is not { } period || !(period > 0) || !double.IsFinite(period))
        {
            return null;
        }

        var spikeTime = a.SpikeTrigger?.Time ?? a.TriggerTime;
        int count = 0;
        foreach (var earlier in a.Context.EarlierSpikes ?? [])
        {
            double dt = (spikeTime - earlier).TotalSeconds;
            int k = (int)Math.Round(dt / period);
            if (dt > 0 && k >= 1 && k <= MaxPeriodMultiple && Math.Abs(dt - (k * period)) <= PeriodTolerance * k * period)
            {
                count++;
            }
        }

        if (count < MinEarlierSpikes)
        {
            return null;
        }

        string text = $"{count} earlier spikes came whole worm periods ({F(period)} s) before this one";
        var evidence = new List<IncidentEvidence>
        {
            Ev("spikeRepeats", Sentence(text), a.SpikeTrigger?.Frame ?? a.TriggerFrame, ("count", count), ("periodSeconds", R1(period))),
            SpikeEvidence(a),
        };
        return Result(IncidentCause.PeriodicSpike, $"Likely periodic error of the mount: {text}.", evidence);
    }

    // Unclear: what is known about the trigger.
    private static IncidentDiagnosis Unclear(Analysis a)
    {
        var evidence = new List<IncidentEvidence>();
        string? what = null;
        if (a.Incident.Kind == IncidentKind.Spike || a.SpikeTrigger is not null)
        {
            var spike = SpikeEvidence(a);
            evidence.Add(spike);
            what = spike.Parameters["errorArcsec"] is double error
                ? $"an error spike of {F(error)}″" + (spike.Parameters["rmsArcsec"] is double rms ? $" at {F(rms)}″ RMS" : string.Empty)
                : "an error spike";
        }

        int lostIndex = a.TriggerIndex < 0 ? -1 : FirstIndex(a.TriggerIndex, a.WindowEnd, i => !a.Frames[i].StarFound);
        if (lostIndex >= 0 || a.Incident.Kind == IncidentKind.StarLost)
        {
            string? status = lostIndex >= 0 ? a.Frames[lostIndex].LostStatus : null;
            status ??= a.Trigger?.Detail ?? a.Trigger?.Message;
            string text = string.IsNullOrWhiteSpace(status) ? "the guide star was lost" : $"the guide star was lost ({status})";
            evidence.Add(Ev("starLost", Sentence(text), lostIndex >= 0 ? a.Frames[lostIndex].Frame : a.TriggerFrame, ("status", status)));
            what ??= text;
        }

        var kind = a.Trigger?.Kind ?? a.Incident.Kind;
        evidence.Add(Ev("trigger", $"The incident was triggered by {kind}.", a.TriggerFrame, ("kind", kind.ToString())));
        what ??= $"no rule matched the {kind} incident";
        return Result(IncidentCause.Unclear, $"Likely cause unclear: {what}.", evidence);
    }

    /// <summary>The spike's total error and the RMS of the guide frames before it (arcsec, null without a pixel scale).</summary>
    private static IncidentEvidence SpikeEvidence(Analysis a)
    {
        int index = a.SpikeTrigger is { } st ? a.IndexOf(st) : a.TriggerIndex;
        double? error = null;
        double? rms = null;
        if (index >= 0)
        {
            var f = a.Frames[index];
            if (a.GuideError(f, GuideAxis.Ra) is { } ra)
            {
                double dec = a.GuideError(f, GuideAxis.Dec) ?? 0;
                error = Math.Sqrt((ra * ra) + (dec * dec));
            }

            var ras = new List<double>();
            var decs = new List<double>();
            for (int j = index - 1; j >= 0 && ras.Count < SpikeRmsFrames; j--)
            {
                if (a.GuideError(a.Frames[j], GuideAxis.Ra) is { } r)
                {
                    ras.Add(r);
                    decs.Add(a.GuideError(a.Frames[j], GuideAxis.Dec) ?? 0);
                }
            }

            if (ras.Count >= 2)
            {
                rms = Math.Sqrt(Variance(ras) + Variance(decs));
            }
        }

        double? errorArcsec = error * a.PixelScale;
        double? rmsArcsec = rms * a.PixelScale;
        string text = errorArcsec is { } e
            ? $"The error spiked to {F(e, "0.##")}″" + (rmsArcsec is { } r2 ? $" (RMS before: {F(r2, "0.##")}″)." : ".")
            : "The error spiked.";
        return Ev("spike", text, index >= 0 ? a.Frames[index].Frame : a.TriggerFrame,
            ("errorArcsec", R2(errorArcsec)), ("rmsArcsec", R2(rmsArcsec)));
    }

    // ---- helpers ----

    private static IncidentEvidence Ev(string code, string message, long? frame, params (string Key, object? Value)[] parameters)
    {
        var p = new Dictionary<string, object?>(parameters.Length);
        foreach (var (key, value) in parameters)
        {
            p[key] = value;
        }

        return new IncidentEvidence(code, message, p, frame);
    }

    private static IncidentDiagnosis Result(IncidentCause cause, string message, List<IncidentEvidence> evidence) =>
        new(cause, message, new Dictionary<string, object?>(evidence[0].Parameters), evidence);

    /// <summary>Frames in which the star is moved on purpose (or whose measurement is not a guide step).</summary>
    private static bool Deliberate(IncidentFrameRecord f) => f.Settling || f.Dithering || f.CoachMeasurement || f.CalibrationDirection is not null;

    /// <summary>The primary star itself was measured (not lost, not estimated from the secondaries).</summary>
    private static bool Measured(IncidentFrameRecord f) => f.StarFound && !f.PrimaryEstimated;

    private static bool IsSaturated(IncidentFrameRecord f) => f.LostStatus?.Contains("saturat", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsMassChange(IncidentFrameRecord f) => f.LostStatus?.Contains("mass change", StringComparison.OrdinalIgnoreCase) == true;

    private static GuidePoint? PrimaryPosition(IncidentFrameRecord f)
    {
        if (f.Star is { IsValid: true } p)
        {
            return p;
        }

        var primary = Analysis.StarsOf(f).FirstOrDefault(s => s.IsPrimary);
        return primary is null ? null : new GuidePoint(primary.X, primary.Y);
    }

    private static bool SameLock(IncidentFrameRecord a, IncidentFrameRecord b)
    {
        GuidePoint? la = a.Lock is { IsValid: true } x ? x : null;
        GuidePoint? lb = b.Lock is { IsValid: true } y ? y : null;
        return la is { } p && lb is { } q ? p.Distance(q) < LockTolerancePx : la is null && lb is null;
    }

    private static (int Ms, GuideDirection? Direction, bool Limited) Pulse(IncidentFrameRecord f, GuideAxis axis) =>
        axis == GuideAxis.Ra ? (f.RaDurationMs, f.RaDirection, f.RaLimited) : (f.DecDurationMs, f.DecDirection, f.DecLimited);

    /// <summary>Signed change of the mount-axis error a pulse should cause, px: West and South reduce a positive error (see <see cref="MountTransform"/>).</summary>
    private static double? ExpectedChange(int ms, GuideDirection? direction, double? rate) =>
        ms > 0 && direction is { } d && rate is { } r ? (d is GuideDirection.West or GuideDirection.South ? -1 : 1) * ms * r : null;

    /// <summary>The pulse direction that reduces an error of this sign.</summary>
    private static GuideDirection Against(double error, GuideAxis axis) => axis == GuideAxis.Ra
        ? error > 0 ? GuideDirection.West : GuideDirection.East
        : error > 0 ? GuideDirection.South : GuideDirection.North;

    private static string AxisName(GuideAxis axis) => axis == GuideAxis.Ra ? "ra" : "dec";

    private static string AxisLabel(GuideAxis axis) => axis == GuideAxis.Ra ? "RA" : "Dec";

    /// <summary>"Ra axis" / "Dec axis" (the guider's runaway detail) → axis.</summary>
    private static GuideAxis? AxisFromText(string? text)
    {
        foreach (var word in (text ?? string.Empty).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("ra", StringComparison.OrdinalIgnoreCase))
            {
                return GuideAxis.Ra;
            }

            if (word.Equals("dec", StringComparison.OrdinalIgnoreCase))
            {
                return GuideAxis.Dec;
            }
        }

        return null;
    }

    private static int FirstIndex(int from, int to, Func<int, bool> predicate)
    {
        for (int i = Math.Max(0, from); i <= to; i++)
        {
            if (predicate(i))
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndex(int from, int to, Func<int, bool> predicate)
    {
        for (int i = to; i >= Math.Max(0, from); i--)
        {
            if (predicate(i))
            {
                return i;
            }
        }

        return -1;
    }

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2;
    }

    private static double? MedianOrNull(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Median(list);
    }

    private static double Variance(List<double> values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Count;
    }

    private static double R1(double v) => Math.Round(v, 1);

    private static double? R1(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private static double? R2(double? v) => v is { } x ? Math.Round(x, 2) : null;

    private static int Pct(double fraction) => (int)Math.Round(fraction * 100);

    private static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString(Inv);

    private static string F(double v, string format = "0.#") => v.ToString(format, Inv);

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static string JoinAnd(List<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>The incident's frames and context, with the derived values the rules share.</summary>
    private sealed class Analysis
    {
        private FieldBrightness? brightness;
        private bool brightnessBuilt;

        public Analysis(Incident incident)
        {
            Incident = incident;
            Frames = (incident.Frames ?? []).Where(f => f is not null).OrderBy(f => f.Time).ToList();
            Triggers = (incident.Triggers ?? []).Where(t => t is not null).ToList();
            Markers = (incident.Markers ?? []).Where(m => m is not null).ToList();
            Context = incident.Context ?? new IncidentContext();
            PixelScale = incident.Tags is { PixelScale: > 0 } tags && double.IsFinite(tags.PixelScale) ? tags.PixelScale : null;
            SearchRegionPx = Math.Max(1, Context.SearchRegionPx);
            Trigger = Triggers.Count > 0 ? Triggers[0] : null;
            SpikeTrigger = Triggers.FirstOrDefault(t => t.Kind == IncidentKind.Spike);
            TriggerIndex = Frames.Count == 0 ? -1 : Trigger is null ? Frames.Count - 1 : IndexOf(Trigger);
            TriggerTime = Trigger?.Time ?? (TriggerIndex >= 0 ? Frames[TriggerIndex].Time : incident.Start);
            WindowEnd = TriggerIndex < 0 ? -1 : Math.Min(Frames.Count - 1, TriggerIndex + LookaheadFrames);
            Transform = Context.RaAngleDeg is { } ra && Context.DecAngleDeg is { } dec && double.IsFinite(ra) && double.IsFinite(dec)
                ? new MountTransform(MountTransform.Radians(ra), MountTransform.Radians(dec), Context.RaRatePxPerMs ?? 0, Context.DecRatePxPerMs ?? 0)
                : null;
            UseSnr = Frames.Any(f => f.Snr > 0 || (f.Stars ?? []).Any(s => s is not null && s.Snr > 0));
        }

        public Incident Incident { get; }

        /// <summary>Frames in time order.</summary>
        public List<IncidentFrameRecord> Frames { get; }

        public List<IncidentTrigger> Triggers { get; }

        public List<IncidentMarker> Markers { get; }

        public IncidentContext Context { get; }

        /// <summary>Guide camera scale, arcsec/px, null when unknown.</summary>
        public double? PixelScale { get; }

        public double SearchRegionPx { get; }

        public IncidentTrigger? Trigger { get; }

        public IncidentTrigger? SpikeTrigger { get; }

        /// <summary>Index of the first trigger's frame (the last frame without a trigger), -1 without frames.</summary>
        public int TriggerIndex { get; }

        public DateTimeOffset TriggerTime { get; }

        /// <summary>Last frame that can still show the first trigger's cause.</summary>
        public int WindowEnd { get; }

        /// <summary>Camera ⇄ mount transform from the calibrated axis angles, null when not calibrated.</summary>
        public MountTransform? Transform { get; }

        /// <summary>Brightness is the SNR; the mass when the telemetry has no SNR.</summary>
        public bool UseSnr { get; }

        public long? TriggerFrame => Trigger?.Frame ?? (TriggerIndex >= 0 ? Frames[TriggerIndex].Frame : null);

        public FieldBrightness? Brightness
        {
            get
            {
                if (!brightnessBuilt)
                {
                    brightness = FieldBrightness.Build(this);
                    brightnessBuilt = true;
                }

                return brightness;
            }
        }

        /// <summary>A telemetry-only incident has no star list: the primary from the frame values.</summary>
        public static IReadOnlyList<StarInfo> StarsOf(IncidentFrameRecord f)
        {
            if (f.Stars is { Count: > 0 } stars)
            {
                return stars.Where(s => s is not null).ToList();
            }

            return f.Star is { IsValid: true } p ? [new StarInfo(p.X, p.Y, f.Snr ?? 0, f.StarMass ?? 0, f.Hfd ?? 0, true, true, 1, null)] : [];
        }

        public int IndexOf(IncidentTrigger t)
        {
            if (Frames.Count == 0)
            {
                return -1;
            }

            if (t.Frame is { } n && Frames.FindIndex(f => f.Frame == n) is var byNumber and >= 0)
            {
                return byNumber;
            }

            int last = 0;
            for (int i = 0; i < Frames.Count; i++)
            {
                if (Frames[i].Time <= t.Time)
                {
                    last = i;
                }
            }

            return last;
        }

        /// <summary>A trigger up to the last frame that can still show the first trigger's cause.</summary>
        public bool InWindow(IncidentTrigger t) => t == Trigger || (WindowEnd >= 0 && t.Time <= Frames[WindowEnd].Time);

        public int MaxDurationMs(GuideAxis axis) => axis == GuideAxis.Ra ? Context.MaxRaDurationMs : Context.MaxDecDurationMs;

        /// <summary>Guide rate in px/ms: calibrated, else from the mount's guide rate and the pixel scale (first calibration).</summary>
        public double? Rate(GuideAxis axis, IncidentFrameRecord f)
        {
            if (Finite(axis == GuideAxis.Ra ? Context.RaRatePxPerMs : Context.DecRatePxPerMs) is { } calibrated && calibrated > 0)
            {
                return calibrated;
            }

            if (PixelScale is not { } scale || f.Mount is not { } m || Finite(axis == GuideAxis.Ra ? m.GuideRateRa : m.GuideRateDec) is not { } g || !(g > 0))
            {
                return null;
            }

            double cosDec = axis == GuideAxis.Ra && Finite(m.DeclinationDeg) is { } d ? Math.Abs(Math.Cos(MountTransform.Radians(d))) : 1;
            return g * Sidereal.ArcsecPerSecond * cosDec / scale / 1000; // 1× sidereal: the step motion of a mount that is not calibrated yet
        }

        /// <summary>Mount-axis error of a frame, px (the guider's raw distance, else the camera offset through the calibration).</summary>
        public double? AxisError(IncidentFrameRecord f, GuideAxis axis)
        {
            if (Finite(axis == GuideAxis.Ra ? f.RaDistanceRaw : f.DecDistanceRaw) is { } raw)
            {
                return raw;
            }

            if (Transform is { } t && Finite(f.Dx) is { } dx && Finite(f.Dy) is { } dy)
            {
                var m = t.CameraToMount(new GuidePoint(dx, dy));
                return axis == GuideAxis.Ra ? m.X : m.Y;
            }

            return null;
        }

        /// <summary>Mount-axis error of a guide frame; null for deliberate moves and frames without the star.</summary>
        public double? GuideError(IncidentFrameRecord f, GuideAxis axis) => Deliberate(f) || !f.StarFound ? null : AxisError(f, axis);

        /// <summary>Star motion along a mount axis between two frames, px, with the sign of the axis error.</summary>
        public double? AxisMotion(IncidentFrameRecord a, IncidentFrameRecord b, GuideAxis axis)
        {
            if (a.CalibrationDirection is null && b.CalibrationDirection is null && SameLock(a, b) && AxisError(a, axis) is { } ea &&
                AxisError(b, axis) is { } eb)
            {
                return eb - ea;
            }

            if (Transform is { } t && a.Star is { IsValid: true } pa && b.Star is { IsValid: true } pb)
            {
                var m = t.CameraToMount(pb - pa);
                return axis == GuideAxis.Ra ? m.X : m.Y;
            }

            return null;
        }

        /// <summary>Camera motion the pulses sent after a frame should cause, px; (0, 0) without pulses, null when unknown.</summary>
        public GuidePoint? ExpectedCameraMotion(IncidentFrameRecord f)
        {
            double mx = 0;
            double my = 0;
            bool any = false;
            foreach (var axis in Axes)
            {
                var (ms, direction, _) = Pulse(f, axis);
                if (ms <= 0)
                {
                    continue;
                }

                if (ExpectedChange(ms, direction, Rate(axis, f)) is not { } e)
                {
                    return null;
                }

                any = true;
                if (axis == GuideAxis.Ra)
                {
                    mx = e;
                }
                else
                {
                    my = e;
                }
            }

            if (!any)
            {
                return new GuidePoint(0, 0);
            }

            return Transform?.MountToCamera(new GuidePoint(mx, my));
        }

        /// <summary>Brightness of a tracked star, null when it was not measured in this frame.</summary>
        public double? Signal(IncidentFrameRecord f, StarInfo s)
        {
            if (s.IsPrimary)
            {
                return PrimarySignal(f, s);
            }

            if (s.RejectReason is "DroppedZero" || TrackedStarStatusExtensions.IsUnmeasuredReason(s.RejectReason))
            {
                return null;
            }

            return Finite(UseSnr ? s.Snr : s.Mass) is { } v ? Math.Max(0, v) : null;
        }

        public double? PrimarySignal(IncidentFrameRecord f) => PrimarySignal(f, StarsOf(f).FirstOrDefault(s => s.IsPrimary));

        /// <summary>
        /// Brightness of the primary. Lost: what the tracker measured in its search box (the frame value), nothing when
        /// unknown. The star list keeps the last good values of a lost primary, so it is not used then. Null for an estimated
        /// primary (stale values) and a saturated one (not faded).
        /// </summary>
        public double? PrimarySignal(IncidentFrameRecord f, StarInfo? s)
        {
            double? frameValue = Finite(UseSnr ? f.Snr : f.StarMass);
            if (Measured(f))
            {
                double? v = frameValue ?? (s is null ? null : Finite(UseSnr ? s.Snr : s.Mass));
                return v is { } x ? Math.Max(0, x) : null;
            }

            if (f.PrimaryEstimated || IsSaturated(f))
            {
                return null;
            }

            return Math.Max(0, frameValue ?? 0);
        }

        /// <summary>Median HFD of the measured stars of a frame, px.</summary>
        public double? FrameHfd(IncidentFrameRecord f)
        {
            var hfds = new List<double>();
            foreach (var s in StarsOf(f))
            {
                double? hfd = s.IsPrimary ? (Measured(f) ? Finite(f.Hfd) ?? s.Hfd : null) : Signal(f, s) is > 0 ? s.Hfd : null;
                if (hfd is { } h && h > 0 && double.IsFinite(h))
                {
                    hfds.Add(h);
                }
            }

            return hfds.Count == 0 ? null : Median(hfds);
        }

        /// <summary>For each star of <paramref name="a"/>, the same star in <paramref name="b"/> (nearest after the primary's shift), null when not found.</summary>
        public StarInfo?[] Match(IncidentFrameRecord a, IncidentFrameRecord b)
        {
            var sa = StarsOf(a);
            var sb = StarsOf(b);
            var matched = new StarInfo?[sa.Count];
            double sx = 0;
            double sy = 0;
            if (PrimaryPosition(a) is { } pa && PrimaryPosition(b) is { } pb)
            {
                sx = pb.X - pa.X;
                sy = pb.Y - pa.Y;
            }

            for (int k = 0; k < sa.Count; k++)
            {
                if (sa[k].IsPrimary)
                {
                    matched[k] = sb.FirstOrDefault(s => s.IsPrimary);
                    continue;
                }

                // guide stars are further apart than the search region (the star finder drops crowded ones)
                double nearest = SearchRegionPx;
                foreach (var s in sb)
                {
                    double d = Math.Sqrt(Math.Pow(s.X - sa[k].X - sx, 2) + Math.Pow(s.Y - sa[k].Y - sy, 2));
                    if (!s.IsPrimary && d <= nearest)
                    {
                        nearest = d;
                        matched[k] = s;
                    }
                }
            }

            return matched;
        }
    }

    /// <summary>
    /// Brightness of each star of a reference frame (the fullest frame before the trigger) through the incident, relative to
    /// the reference, and the field median of it.
    /// </summary>
    private sealed class FieldBrightness
    {
        private FieldBrightness(IReadOnlyList<StarInfo> stars, double?[][] ratio, double?[] field, double?[] smooth)
        {
            Stars = stars;
            Ratio = ratio;
            Field = field;
            Smooth = smooth;
            ReferenceStars = stars.Count;
        }

        /// <summary>The reference frame's stars with a brightness.</summary>
        public IReadOnlyList<StarInfo> Stars { get; }

        public int ReferenceStars { get; }

        /// <summary>[frame][star]: brightness relative to the reference frame, null when not measured.</summary>
        public double?[][] Ratio { get; }

        /// <summary>Median of the stars' ratios per frame, null without a measured star (and for Coach frames: exposure and gain change).</summary>
        public double?[] Field { get; }

        /// <summary><see cref="Field"/> smoothed over 3 frames (a single dark frame is not a fade).</summary>
        public double?[] Smooth { get; }

        public static FieldBrightness? Build(Analysis a)
        {
            var fr = a.Frames;
            int reference = -1;
            int bestCount = 0;
            double bestPrimary = 0;
            for (int pass = 0; pass < 2 && reference < 0; pass++)
            {
                // the fullest measurement before the trigger (else anywhere), brightest primary on a tie
                int last = pass == 0 ? a.TriggerIndex : fr.Count - 1;
                for (int i = 0; i <= last; i++)
                {
                    var f = fr[i];
                    if (!Measured(f) || f.CoachMeasurement || f.Settling || f.Dithering)
                    {
                        continue;
                    }

                    int count = Analysis.StarsOf(f).Count(s => a.Signal(f, s) > 0);
                    double primary = a.PrimarySignal(f) ?? 0;
                    if (count > bestCount || (count == bestCount && count > 0 && primary > bestPrimary))
                    {
                        reference = i;
                        bestCount = count;
                        bestPrimary = primary;
                    }
                }
            }

            if (reference < 0)
            {
                return null;
            }

            var refFrame = fr[reference];
            var all = Analysis.StarsOf(refFrame);
            var used = Enumerable.Range(0, all.Count).Where(k => a.Signal(refFrame, all[k]) > 0).ToList();
            var stars = used.Select(k => all[k]).ToList();
            var refSignal = stars.Select(s => a.Signal(refFrame, s)!.Value).ToArray();
            var ratio = new double?[fr.Count][];
            var field = new double?[fr.Count];
            for (int i = 0; i < fr.Count; i++)
            {
                var f = fr[i];
                ratio[i] = new double?[stars.Count];
                if (f.CoachMeasurement)
                {
                    continue;
                }

                var matched = a.Match(refFrame, f);
                var values = new List<double>();
                for (int k = 0; k < stars.Count; k++)
                {
                    var m = matched[used[k]];
                    double? signal = stars[k].IsPrimary ? a.PrimarySignal(f, m) : m is null ? null : a.Signal(f, m);
                    if (signal is { } v)
                    {
                        ratio[i][k] = v / refSignal[k];
                        values.Add(v / refSignal[k]);
                    }
                }

                field[i] = values.Count > 0 ? Median(values) : null;
            }

            var smooth = new double?[fr.Count];
            for (int i = 0; i < fr.Count; i++)
            {
                if (field[i] is not null)
                {
                    smooth[i] = MedianOrNull(Enumerable.Range(i - 1, 3).Where(j => j >= 0 && j < fr.Count).Select(j => field[j]));
                }
            }

            return new FieldBrightness(stars, ratio, field, smooth);
        }

        /// <summary>
        /// Per star: its median brightness in [<paramref name="beforeFrom"/>, <paramref name="beforeTo"/>] against its faintest in
        /// [<paramref name="from"/>, <paramref name="to"/>]. Returns the number of stars compared and the smallest drop.
        /// </summary>
        public (int Stars, double MinDrop) Faded(int beforeFrom, int beforeTo, int from, int to)
        {
            int stars = 0;
            double minDrop = 1;
            for (int k = 0; k < Stars.Count; k++)
            {
                double? before = MedianOrNull(Enumerable.Range(beforeFrom, Math.Max(0, beforeTo - beforeFrom + 1)).Select(i => Ratio[i][k]));
                var during = Enumerable.Range(from, Math.Max(0, to - from + 1)).Select(i => Ratio[i][k]).Where(v => v is not null).Select(v => v!.Value).ToList();
                if (before is not { } b || b <= 0 || during.Count == 0)
                {
                    continue;
                }

                stars++;
                minDrop = Math.Min(minDrop, Math.Max(0, 1 - (during.Min() / b)));
            }

            return (stars, minDrop);
        }

        /// <summary>Per star: median brightness in the <paramref name="early"/> frames against the <paramref name="late"/> ones.</summary>
        public (int Stars, double MinDrop) FadedBetween(List<int> early, List<int> late)
        {
            int stars = 0;
            double minDrop = 1;
            for (int k = 0; k < Stars.Count; k++)
            {
                double? before = MedianOrNull(early.Select(i => Ratio[i][k]));
                double? after = MedianOrNull(late.Select(i => Ratio[i][k]));
                if (before is not { } b || b <= 0 || after is not { } l)
                {
                    continue;
                }

                stars++;
                minDrop = Math.Min(minDrop, Math.Max(0, 1 - (l / b)));
            }

            return (stars, minDrop);
        }

        /// <summary>Secondaries measured in <paramref name="frame"/> and how many of them are within ±<paramref name="tolerance"/> of their brightness in <paramref name="before"/>.</summary>
        public (int Compared, int Normal) SecondariesNormal(int frame, List<int> before, double tolerance)
        {
            int compared = 0;
            int normal = 0;
            for (int k = 0; k < Stars.Count; k++)
            {
                if (Stars[k].IsPrimary || Ratio[frame][k] is not { } now || MedianOrNull(before.Select(i => Ratio[i][k])) is not { } b || b <= 0)
                {
                    continue;
                }

                compared++;
                double r = now / b;
                if (r >= 1 - tolerance && r <= 1 / (1 - tolerance))
                {
                    normal++;
                }
            }

            return (compared, normal);
        }
    }
}
