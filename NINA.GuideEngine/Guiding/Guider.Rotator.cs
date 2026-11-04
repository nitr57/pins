// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

// A rotator that turns the guide camera (GuiderSettings.RotatorTurnsGuideCamera, an off-axis guider behind the rotator).
// After a rotation the guide camera looks at another piece of sky, so the guide star is gone, and the frames taken while
// it turns are smeared. Guiding waits for the rotator to stop and starts again with a new star; AdjustCalibration turns
// the calibration when guiding starts.
public sealed partial class Guider
{
    /// <summary>A rotator that keeps reporting moving or turning for longer than this no longer holds guiding back.</summary>
    public static readonly TimeSpan RotatorWaitLimit = TimeSpan.FromMinutes(2);

    /// <summary>The largest rotator turn since the star was selected that keeps the star (degrees), as for the calibration.</summary>
    public const double RotatorTurnToleranceDeg = CalibrationAdjuster.RotatorToleranceDeg;

    /// <summary>
    /// Guide frames in a row that must see the rotator's angle turn before a running guide stops for it, so that a jittering
    /// readout does not stop it. The driver's moving state stops it at the first frame.
    /// </summary>
    public const int RotatorTurnFramesToStop = 2;

    // the rotator angle the current star was selected at, null when unknown
    private double? starRotatorAngle;

    // guide frames in a row in which a running guide saw the rotator's angle turn
    private int rotatorTurnFrames;

    // since when a start waits for the rotator. A wait that ran out ignores the flag that kept it (the driver's moving
    // state, or the angle's recent change) until that flag clears, also for later starts; the other one still counts
    private DateTimeOffset? rotatorWaitSince;
    private bool rotatorMovingIgnored;
    private bool rotatorTurningIgnored;

    // the run was stopped for the rotator (the wait says so), and the rotator move a new star was selected for: told
    // together with the turned calibration, so that one rotation gives one message
    private bool rotatorStoppedRun;
    private (double FromDeg, double ToDeg)? newStarAfterRotation;

    private bool RotatorTurnsGuideCamera => settings.RotatorTurnsGuideCamera != RotatorGuideCamera.No;

    // the star (or lock position) was chosen in this snapshot's field; an unreadable angle keeps the last one
    private void NoteStarRotatorAngle(MountSnapshot snapshot)
    {
        rotatorTurnFrames = 0;
        if (snapshot.RotatorAngleDeg is { } angle)
        {
            starRotatorAngle = angle;
        }
    }

    // the rotator turned the guide camera since the star was selected (the shorter way round)
    private double? RotatorTurnSinceSelection(MountSnapshot snapshot)
    {
        if (!RotatorTurnsGuideCamera || snapshot.RotatorAngleDeg is not { } now || starRotatorAngle is not { } then)
        {
            return null;
        }

        double turn = MountTransform.Norm(now - then, -180.0, 180.0);
        return Math.Abs(turn) > RotatorTurnToleranceDeg ? turn : null;
    }

    // the rotator moves or turns, as far as the wait still counts its flags
    private bool RotatorBusy(MountSnapshot snapshot)
    {
        if (!snapshot.RotatorMoving)
        {
            rotatorMovingIgnored = false;
        }

        if (!snapshot.RotatorTurning)
        {
            rotatorTurningIgnored = false;
        }

        return (snapshot.RotatorMoving && !rotatorMovingIgnored) || (snapshot.RotatorTurning && !rotatorTurningIgnored);
    }

    // a running guide stops for the rotator: at once when the driver reports it moving, for a turn since the star was
    // selected only when it shows on enough frames in a row. The host's RotatorTurning is not used here: every change sets
    // it, also a readout that jumps and comes back. While a wait gave up on an angle that kept changing (a slow creep), the
    // turn does not stop the run either; once the angle holds, it does
    private bool RotatorStopsRun(MountSnapshot snapshot, bool resume)
    {
        // also clears the flags a wait gave up on, once they cleared
        RotatorBusy(snapshot);

        if (snapshot.RotatorMoving && !rotatorMovingIgnored)
        {
            return true;
        }

        if (rotatorTurningIgnored || RotatorTurnSinceSelection(snapshot) is null)
        {
            rotatorTurnFrames = 0;
            return false;
        }

        return resume || ++rotatorTurnFrames >= RotatorTurnFramesToStop;
    }

    /// <summary>
    /// A start is due (LoopingFrame): false while the rotator that turns the guide camera still moves, so no star is selected
    /// in a smeared frame and guiding does not lock on the field from before the rotation. Gives up after
    /// <see cref="RotatorWaitLimit"/>. When the rotator turned since the star was selected, a new star is selected first.
    /// </summary>
    private bool RotatorAllowsStart(MountSnapshot snapshot, DateTimeOffset now)
    {
        if (!RotatorTurnsGuideCamera)
        {
            return true;
        }

        if (RotatorBusy(snapshot))
        {
            if (rotatorWaitSince is not { } since)
            {
                rotatorWaitSince = now;
                Alert(GuideErrorCode.RotatorTurnedGuideCamera, rotatorStoppedRun
                    ? "Guiding stopped while the rotator turns; it starts again once the rotator has stopped"
                    : "Waiting for the rotator to stop before guiding starts");
                rotatorStoppedRun = false;
                return false;
            }

            if (now - since < RotatorWaitLimit)
            {
                return false;
            }

            rotatorMovingIgnored = snapshot.RotatorMoving;
            rotatorTurningIgnored = snapshot.RotatorTurning;
            Alert(GuideErrorCode.RotatorTurnedGuideCamera, snapshot.RotatorMoving
                ? $"The rotator still reports moving after {RotatorWaitLimit.TotalMinutes:0} minutes; guiding starts anyway"
                : $"The rotator angle still changes after {RotatorWaitLimit.TotalMinutes:0} minutes; guiding starts anyway");
        }

        rotatorWaitSince = null;
        rotatorStoppedRun = false;

        if (RotatorTurnSinceSelection(snapshot) is not null)
        {
            newStarAfterRotation = (starRotatorAngle!.Value, snapshot.RotatorAngleDeg!.Value);
            if (!autoSelectRequested)
            {
                autoSelectRequested = true;
                autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
            }
        }

        return true;
    }

    /// <summary>
    /// While guiding or calibrating (not paused): the rotator that turns the guide camera started moving or turned. The run
    /// stops and starts again, which waits for the rotator and selects a new star. A pending start or dither settles on
    /// the new star, and a calibration that was asked for is made again. After a meridian flip the flip time stays, so a
    /// wrong Dec-flip setting is still inverted on the first Dec runaway. Per guide frame a turn of the angle counts only
    /// on <see cref="RotatorTurnFramesToStop"/> frames in a row; <paramref name="resume"/> (a single check after a pause)
    /// counts it at once.
    /// </summary>
    private bool RestartIfRotatorTurned(MountSnapshot snapshot, bool resume = false)
    {
        if (!RotatorTurnsGuideCamera)
        {
            return false;
        }

        if (!RotatorStopsRun(snapshot, resume))
        {
            return false;
        }

        rotatorTurnFrames = 0;

        if (calibrationProcess is not null)
        {
            // the old calibration is still kept: without this the restart would guide with it, turned
            forceCalibration = true;
        }

        rotatorStoppedRun = true;
        InterruptCoachOnLoop(CoachInterruptReasons.Guiding);
        StopRunCore();
        startGuidingRequested = true;

        // the star may have been lost already (the rotation smeared it): a start with no star selects one
        if (!tracker.IsLocked)
        {
            autoSelectRequested = true;
            autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
        }

        return true;
    }

    // the calibration was turned for a rotator move (AdjustCalibration), with a new star when one was selected for it
    private void AlertRotatorTurn(CalibrationAdjustment adj)
    {
        if (adj.RotatorTurn is not { } turn)
        {
            return;
        }

        string what = newStarAfterRotation is not null ? "new guide star selected, calibration" : "calibration";
        newStarAfterRotation = null;
        Alert(GuideErrorCode.RotatorTurnedGuideCamera, string.Create(CultureInfo.InvariantCulture,
            $"Rotator moved from {turn.FromDeg:0.0}° to {turn.ToDeg:0.0}°: {what} turned by {turn.CalibrationTurnDeg:+0.0;-0.0}°"));
    }

    // a new star was selected after a rotation, and no calibration was turned for it
    private void AlertNewStarAfterRotation()
    {
        if (newStarAfterRotation is not { } move)
        {
            return;
        }

        newStarAfterRotation = null;
        Alert(GuideErrorCode.RotatorTurnedGuideCamera, string.Create(CultureInfo.InvariantCulture,
            $"Rotator moved from {move.FromDeg:0.0}° to {move.ToDeg:0.0}°: new guide star selected"));
    }

    // a start that was asked for: the wait for the rotator begins afresh. A flag the last wait gave up on stays ignored
    // until it clears (RotatorBusy), so a driver stuck at moving does not hold back every start for the full wait
    private void ResetRotatorWait()
    {
        rotatorWaitSince = null;
        rotatorTurnFrames = 0;
        rotatorStoppedRun = false;
        newStarAfterRotation = null;
    }
}
