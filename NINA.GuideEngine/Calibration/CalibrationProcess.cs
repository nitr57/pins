// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/scope.cpp (BeginCalibration, UpdateCalibrationState) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>Calibration state (PHD2 CALIBRATION_STATE plus Failed).</summary>
public enum CalibrationState
{
    Cleared,
    GoWest,
    GoEast,
    ClearBacklash,
    GoNorth,
    GoSouth,
    NudgeSouth,
    Complete,
    Failed,
}

/// <summary>Stable calibration failure codes.</summary>
public enum CalibrationErrorCode
{
    None,

    /// <summary>BeginCalibration without a valid star/lock position.</summary>
    InvalidLockPosition,

    /// <summary>"RA Calibration Failed: star did not move enough".</summary>
    RaStarDidNotMove,

    /// <summary>"Backlash Clearing Failed: star did not move enough".</summary>
    BacklashClearingFailed,

    /// <summary>"DEC Calibration Failed: star did not move enough".</summary>
    DecStarDidNotMove,
}

/// <summary>Calibration settings (PHD2 per-mount profile values).</summary>
public sealed record CalibrationSettings
{
    /// <summary>Calibration pulse duration, ms (PHD2 CalibrationDuration, default 750).</summary>
    public int StepMs { get; init; } = CalibrationStepCalculator.DefaultCalibrationDurationMs;

    /// <summary>Calibration distance, px (PHD2 CalibrationDistance, default 25).</summary>
    public int DistancePx { get; init; } = CalibrationStepCalculator.DefaultDistancePx;

    public int MaxRaDurationMs { get; init; } = 2500;

    public int MaxDecDurationMs { get; init; } = 2500;

    /// <summary>PHD2 fast recenter (on by default): return moves use the largest pulse that keeps the star in the search region.</summary>
    public bool FastRecenter { get; init; } = true;

    /// <summary>PHD2 Guider::GetMaxMovePixels = search region, px (default 15).</summary>
    public int MaxMovePixels { get; init; } = 15;

    /// <summary>PHD2 AssumeOrthogonal: force yAngle to xAngle ± 90°.</summary>
    public bool AssumeOrthogonal { get; init; }

    /// <summary>Dec guide mode; <see cref="DecGuideMode.Off"/> calibrates RA only.</summary>
    public DecGuideMode DecGuideMode { get; init; } = DecGuideMode.Auto;

    /// <summary>True when corrections go through an ST4 port (PHD2 !CanPulseGuide): enables the East-return advisory.</summary>
    public bool IsSt4 { get; init; }

    /// <summary>PHD2 UseDecComp, used by the sanity check.</summary>
    public bool DecCompensationEnabled { get; init; } = true;

    /// <summary>Run CheckCalibrationDuration at Begin (PHD2 always does).</summary>
    public bool RecomputeStepSize { get; init; } = true;

    /// <summary>Sanity issue types the user does not want alerts for.</summary>
    public IReadOnlySet<CalibrationIssueType>? SuppressedIssues { get; init; }
}

/// <summary>Equipment context of a calibration run.</summary>
public sealed record CalibrationContext
{
    /// <summary>Guide optics; <see cref="GuideOptics.Binning"/> is the current camera binning.</summary>
    public required GuideOptics Optics { get; init; }

    /// <summary>The previous calibration (for CheckCalibrationDuration and the "different" sanity check).</summary>
    public CalibrationData? PreviousCalibration { get; init; }

    /// <summary>Time source for the calibration timestamp.</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;
}

/// <summary>
/// Calibration step info (PHD2 CalibrationStepInfo). Direction is "West", "East", "Backlash", "North", "South" or
/// "NudgeSouth". Dx/Dy are start - current (px), Distance the distance from the start of the leg.
/// </summary>
public sealed record CalibrationStepInfo(string Direction, int StepNumber, double Dx, double Dy, GuidePoint Position, double Distance)
{
    /// <summary>Status message; the PHD2 'Calibrating' event field "State".</summary>
    public string? Message { get; init; }
}

/// <summary>One calibrated direction finished (PHD2 GuideLog.CalibrationDirectComplete).</summary>
public sealed record CalibrationDirectionComplete(string Direction, double AngleRad, double RatePxPerMs, GuideParity Parity);

/// <summary>A calibration advisory (not a failure).</summary>
/// <param name="Code">Stable code.</param>
/// <param name="Message">PHD2 text.</param>
/// <param name="ShowToUser">False for advisories PHD2 only writes to the debug log.</param>
public sealed record CalibrationAdvisory(string Code, string Message, bool ShowToUser);

/// <summary>Result of one <see cref="CalibrationProcess.Update"/> (or Begin) call.</summary>
public sealed record CalibrationUpdate
{
    /// <summary>State after this update.</summary>
    public required CalibrationState State { get; init; }

    /// <summary>Pulses to issue now, in order (usually zero or one). The guider waits for them before the next exposure.</summary>
    public IReadOnlyList<PulseCommand> Pulses { get; init; } = [];

    /// <summary>Calibration steps to write to the guide log (PHD2 GuideLog.CalibrationStep), in order.</summary>
    public IReadOnlyList<CalibrationStepInfo> LoggedSteps { get; init; } = [];

    /// <summary>Step to publish as a PHD2 'Calibrating' event (Message = State), null when PHD2 sends none.</summary>
    public CalibrationStepInfo? Status { get; init; }

    /// <summary>Directions completed during this update (West and/or North).</summary>
    public IReadOnlyList<CalibrationDirectionComplete> DirectionsCompleted { get; init; } = [];

    public IReadOnlyList<CalibrationAdvisory> Advisories { get; init; } = [];

    /// <summary>True when the frame had no star; nothing was done (PHD2 "blundering on").</summary>
    public bool StarLost { get; init; }

    /// <summary>Completed calibration (State == Complete).</summary>
    public CalibrationData? Result { get; init; }

    /// <summary>Sanity check of the completed calibration.</summary>
    public CalibrationSanityResult? Sanity { get; init; }

    public CalibrationErrorCode ErrorCode { get; init; }

    /// <summary>PHD2 failure message (State == Failed).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Calibration pulses issued so far (for a progress indicator).</summary>
    public int StepsIssued { get; init; }

    /// <summary>Estimated total pulses (≥ StepsIssued; refined as the calibration proceeds).</summary>
    public int EstimatedTotalSteps { get; init; }

    public bool IsComplete => State == CalibrationState.Complete;

    public bool IsFailed => State == CalibrationState.Failed;
}

/// <summary>
/// Host-agnostic port of PHD2 Scope::BeginCalibration / Scope::UpdateCalibrationState. Call <see cref="Begin"/> with
/// the current star position, then <see cref="Update"/> once per guide frame with the measured star position (invalid
/// when the star was lost) and issue the returned pulses.
/// </summary>
public sealed class CalibrationProcess
{
    public const int MaxCalibrationSteps = 60; // MAX_CALIBRATION_STEPS
    public const int MaxNudges = 3; // MAX_NUDGES
    public const double NudgeTolerance = 2.0; // NUDGE_TOLERANCE
    public const int BacklashMinCount = 3; // BL_BACKLASH_MIN_COUNT
    public const int BacklashMaxClearingTimeMs = 60000; // BL_MAX_CLEARING_TIME
    public const int BacklashMinClearingDistance = 3; // BL_MIN_CLEARING_DISTANCE

    private const double OneArcsecHours = 24.0 / (360.0 * 60.0 * 60.0);
    private const double OneArcsecDegrees = 1.0 / (60.0 * 60.0);

    private readonly CalibrationSettings settings;
    private readonly CalibrationContext context;
    private readonly List<GuidePoint> raSteps = [];
    private readonly List<GuidePoint> decSteps = [];

    private int calibrationSteps;
    private GuidePoint calibrationInitialLocation = GuidePoint.Invalid;
    private GuidePoint calibrationStartingLocation = GuidePoint.Invalid;
    private GuidePoint calibrationStartingCoords = GuidePoint.Invalid;
    private GuidePoint eastStartingLocation = GuidePoint.Invalid;
    private GuidePoint southStartingLocation = GuidePoint.Invalid;

    // Dec take-up after the reversal at the end of the North leg: South pulse time sent so far and the star positions after it
    private readonly List<(double PulseMs, GuidePoint Location)> southTrack = [];
    private double southPulseMs;
    private GuidePoint blMarkerPoint = GuidePoint.Invalid;
    private double blExpectedBacklashStep;
    private int blMaxClearingPulses;
    private double blLastCumDistance;
    private int blAcceptedMoves;
    private double blDistanceMoved;
    private int recenterRemaining;
    private int recenterDuration;
    private double northDirCosX;
    private double northDirCosY;
    private double totalSouthAmt;
    private int raStepCount;
    private int decStepCount;

    private double xAngle;
    private double yAngle;
    private double xRate;
    private double yRate;
    private GuideParity raParity;
    private GuideParity decParity;

    // progress bookkeeping
    private int stepsIssued;
    private int westEstimate;
    private int northEstimate;
    private int phasePulses;

    public CalibrationProcess(CalibrationSettings settings, CalibrationContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        this.settings = settings;
        this.context = context;
        StepMs = settings.StepMs > 0 ? settings.StepMs : CalibrationStepCalculator.DefaultCalibrationDurationMs;
        DistancePx = settings.DistancePx > 0 ? settings.DistancePx : CalibrationStepCalculator.DefaultDistancePx;
    }

    public CalibrationState State { get; private set; } = CalibrationState.Cleared;

    /// <summary>Calibration pulse duration in use (may have been refined at Begin; persist when <see cref="StepCheck"/> says so).</summary>
    public int StepMs { get; private set; }

    /// <summary>Calibration distance in use, px (PHD2 CalibrationTotDistance, also the AutoFind edge allowance).</summary>
    public int DistancePx { get; private set; }

    /// <summary>Result of CheckCalibrationDuration at Begin, null if not run.</summary>
    public CalibrationStepCheck? StepCheck { get; private set; }

    public int StepsIssued => stepsIssued;

    /// <summary>Completed calibration after <see cref="CalibrationState.Complete"/>.</summary>
    public CalibrationData? Result { get; private set; }

    /// <summary>Port of Scope::BeginCalibration.</summary>
    public CalibrationUpdate Begin(GuidePoint currentLocation, MountSnapshot mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (State != CalibrationState.Cleared)
        {
            throw new InvalidOperationException("Calibration already started");
        }

        if (!currentLocation.IsValid)
        {
            return Fail(CalibrationErrorCode.InvalidLockPosition, "Must have a valid lock position", []);
        }

        if (settings.RecomputeStepSize)
        {
            // Make sure guide speeds or binning haven't changed underneath us
            StepCheck = CalibrationStepCalculator.CheckCalibrationDuration(StepMs, DistancePx, context.Optics,
                mount.GuideRateRa, context.PreviousCalibration);
            StepMs = StepCheck.StepMs > 0 ? StepCheck.StepMs : StepMs;
            DistancePx = StepCheck.DistancePx > 0 ? StepCheck.DistancePx : DistancePx;
        }

        calibrationSteps = 0;
        points.Clear();
        points.Add(new CalibrationPoint("Start", 0, currentLocation.X, currentLocation.Y));
        calibrationInitialLocation = currentLocation;
        calibrationStartingLocation = GuidePoint.Invalid;
        calibrationStartingCoords = GuidePoint.Invalid;
        State = CalibrationState.GoWest;
        raSteps.Clear();
        decSteps.Clear();
        southTrack.Clear();
        southPulseMs = 0;
        raStepCount = 0;
        decStepCount = 0;
        InitEstimates(mount);

        return new CalibrationUpdate
        {
            State = State,
            StepsIssued = 0,
            EstimatedTotalSteps = EstimateTotal(),
        };
    }

    /// <summary>
    /// Port of Scope::UpdateCalibrationState, called once per guide frame. An invalid position (star lost) is
    /// skipped as in PHD2 ("blundering on").
    /// </summary>
    public CalibrationUpdate Update(GuidePoint currentLocation, MountSnapshot mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        // Complete without a result happens after an RA-only calibration: PHD2 finishes it on the next frame.
        if (State is CalibrationState.Cleared or CalibrationState.Failed || (State == CalibrationState.Complete && Result is not null))
        {
            throw new InvalidOperationException($"Calibration is not running (state {State})");
        }

        if (!currentLocation.IsValid)
        {
            return new CalibrationUpdate
            {
                State = State,
                StarLost = true,
                StepsIssued = stepsIssued,
                EstimatedTotalSteps = EstimateTotal(),
            };
        }

        var pulses = new List<PulseCommand>();
        var logged = new List<CalibrationStepInfo>();
        var directions = new List<CalibrationDirectionComplete>();
        var advisories = new List<CalibrationAdvisory>();
        CalibrationStepInfo? status = null;
        CalibrationData? result = null;
        CalibrationSanityResult? sanity = null;

        void Schedule(GuideDirection dir, int durationMs)
        {
            // Deviation from PHD2: zero-length pulses (possible for tiny nudges) are not emitted.
            if (durationMs > 0)
            {
                pulses.Add(new PulseCommand(dir, durationMs));
            }

            stepsIssued++;
            phasePulses++;
        }

        CalibrationStepInfo Status(CalibrationStepInfo info, string msg)
        {
            status = info with { Message = msg };
            return status;
        }

        if (!calibrationStartingLocation.IsValid)
        {
            calibrationStartingLocation = currentLocation;
            calibrationStartingCoords = GetRaDecCoordinates(mount);
        }

        double dX = calibrationStartingLocation.DX(currentLocation);
        double dY = calibrationStartingLocation.DY(currentLocation);
        double dist = calibrationStartingLocation.Distance(currentLocation);
        double distCrit = DistancePx;

        switch (State)
        {
            case CalibrationState.GoWest:
            {
                // step number in the log is the step that just finished
                var info = new CalibrationStepInfo("West", calibrationSteps, dX, dY, currentLocation, dist);
                logged.Add(info);
                raSteps.Add(new GuidePoint(dX, dY));

                if (dist < distCrit)
                {
                    if (calibrationSteps++ > MaxCalibrationSteps)
                    {
                        return Fail(CalibrationErrorCode.RaStarDidNotMove, "RA Calibration Failed: star did not move enough", logged);
                    }

                    Status(info, $"West step {calibrationSteps,3}, dist={dist,4:F1}");
                    Schedule(GuideDirection.West, StepMs);
                    break;
                }

                // West calibration complete
                xAngle = MountTransform.PhdAngle(calibrationStartingLocation, currentLocation);
                xRate = dist / (calibrationSteps * (double)StepMs);

                raParity = GuideParity.Unknown;
                if (calibrationStartingCoords.IsValid)
                {
                    GuidePoint endingCoords = GetRaDecCoordinates(mount);
                    if (endingCoords.IsValid)
                    {
                        // true westward motion decreases RA
                        // Deviation from PHD2: the RA difference is wrapped to ±12 h so a 0h/24h crossing is handled.
                        double dra = MountTransform.Norm(endingCoords.X - calibrationStartingCoords.X, -12.0, 12.0);
                        if (dra < -OneArcsecHours)
                        {
                            raParity = GuideParity.Even;
                        }
                        else if (dra > OneArcsecHours)
                        {
                            raParity = GuideParity.Odd;
                        }
                    }
                }

                raStepCount = calibrationSteps;
                directions.Add(new CalibrationDirectionComplete("West", xAngle, xRate, raParity));

                // for GO_EAST recenterRemaining contains the total remaining duration. Choose the largest pulse size
                // that will not lose the guide star or exceed the user-specified max pulse
                recenterRemaining = calibrationSteps * StepMs;

                if (settings.FastRecenter)
                {
                    recenterDuration = SafeFloorToInt(settings.MaxMovePixels / xRate);
                    if (recenterDuration > settings.MaxRaDurationMs)
                    {
                        recenterDuration = settings.MaxRaDurationMs;
                    }

                    if (recenterDuration < StepMs)
                    {
                        recenterDuration = StepMs;
                    }
                }
                else
                {
                    recenterDuration = StepMs;
                }

                calibrationSteps = DivRoundUp(recenterRemaining, recenterDuration);
                State = CalibrationState.GoEast;
                phasePulses = 0;
                eastStartingLocation = currentLocation;
                goto case CalibrationState.GoEast;
            }

            case CalibrationState.GoEast:
            {
                var info = new CalibrationStepInfo("East", calibrationSteps, dX, dY, currentLocation, dist);
                logged.Add(info);
                raSteps.Add(new GuidePoint(dX, dY));

                if (recenterRemaining > 0)
                {
                    int duration = recenterDuration;
                    if (duration > recenterRemaining)
                    {
                        duration = recenterRemaining;
                    }

                    Status(info, $"East step {calibrationSteps,3}, dist={dist,4:F1}");

                    recenterRemaining -= duration;
                    --calibrationSteps;

                    Schedule(GuideDirection.East, duration);
                    break;
                }

                // If not pulse-guiding check for obvious guide cable problem and no useful east moves
                if (settings.IsSt4)
                {
                    double eastDistMoved = eastStartingLocation.Distance(currentLocation);
                    double westDistMoved = calibrationStartingLocation.Distance(eastStartingLocation);
                    double eastAngle = MountTransform.PhdAngle(eastStartingLocation, currentLocation);

                    // Want a significant east movement that re-traces the west vector to within 30 degrees
                    if (Math.Abs(eastDistMoved) < 0.25 * westDistMoved ||
                        Math.Abs(MountTransform.NormAngle(eastAngle - (xAngle + Math.PI))) > MountTransform.Radians(30))
                    {
                        advisories.Add(new CalibrationAdvisory("CAL_NO_EAST_MOVEMENT",
                            "Advisory: Little or no east movement was measured, so guiding will probably be impaired. " +
                            "Check the guide cable and use the Manual Guide tool to confirm basic operation of the mount.",
                            true));
                    }
                }

                // setup for clear backlash
                calibrationSteps = 0;
                dist = dX = dY = 0.0;
                calibrationStartingLocation = currentLocation;

                if (settings.DecGuideMode == DecGuideMode.Off)
                {
                    // Skipping Dec calibration as DecGuideMode == NONE
                    State = CalibrationState.Complete;
                    yAngle = MountTransform.NormAngle(xAngle + Math.PI / 2.0); // arbitrary angle perpendicular to xAngle
                    yRate = 0.0; // lack of Dec calibration data (PHD2 CALIBRATION_RATE_UNCALIBRATED)
                    decParity = GuideParity.Unknown;
                    break;
                }

                State = CalibrationState.ClearBacklash;
                phasePulses = 0;
                blMarkerPoint = currentLocation;
                calibrationStartingCoords = GetRaDecCoordinates(mount);
                blExpectedBacklashStep = xRate * StepMs * 0.6;

                if (mount.GuideRateRa is { } raSpeed && mount.GuideRateDec is { } decSpeed && raSpeed != 0.0 && raSpeed != decSpeed)
                {
                    blExpectedBacklashStep *= decSpeed / raSpeed;
                }

                blMaxClearingPulses = Math.Max(8, BacklashMaxClearingTimeMs / StepMs);
                blLastCumDistance = 0;
                blAcceptedMoves = 0;
                goto case CalibrationState.ClearBacklash;
            }

            case CalibrationState.ClearBacklash:
            {
                var info = new CalibrationStepInfo("Backlash", calibrationSteps, dX, dY, currentLocation, dist);
                logged.Add(info);
                double blDelta = blMarkerPoint.Distance(currentLocation);
                double blCumDelta = dist;

                // Want to see the mount moving north for 3 moves of >= expected distance pixels without any direction reversals
                if (calibrationSteps == 0)
                {
                    // Get things moving with the first clearing pulse
                    Schedule(GuideDirection.North, StepMs);
                    calibrationSteps = 1;
                    Status(info, "Clearing backlash step 1");
                    break;
                }

                if (blDelta >= blExpectedBacklashStep)
                {
                    if (blAcceptedMoves == 0 || blCumDelta > blLastCumDistance) // Just starting or still moving in same direction
                    {
                        blAcceptedMoves++;
                    }
                    else
                    {
                        blAcceptedMoves = 0; // Reset on a direction reversal
                    }
                }
                else if (blCumDelta < blLastCumDistance)
                {
                    blAcceptedMoves = 0; // small direction reversal
                }

                if (blAcceptedMoves < BacklashMinCount) // More work to do
                {
                    if (calibrationSteps < blMaxClearingPulses && blCumDelta < distCrit)
                    {
                        // Still have attempts left, haven't moved the star by 25 px yet
                        Schedule(GuideDirection.North, StepMs);
                        calibrationSteps++;
                        blMarkerPoint = currentLocation;
                        calibrationStartingCoords = GetRaDecCoordinates(mount);
                        blLastCumDistance = blCumDelta;
                        Status(info, $"Clearing backlash step {calibrationSteps,3}");
                        break;
                    }

                    // Used up all our attempts - might be ok or not
                    if (blCumDelta >= BacklashMinClearingDistance)
                    {
                        // Exhausted all the clearing pulses without reaching the goal - but we did move the mount > 3 px
                        calibrationSteps = 0;
                        calibrationStartingLocation = currentLocation;
                        dX = 0;
                        dY = 0;
                        dist = 0;
                    }
                    else
                    {
                        return Fail(CalibrationErrorCode.BacklashClearingFailed, "Backlash Clearing Failed: star did not move enough", logged);
                    }
                }
                else
                {
                    // Got our 3 moves, move ahead. We know the last backlash clearing move was big enough - include
                    // that as a north calibration move. Log the starting point.
                    logged.Add(new CalibrationStepInfo("North", 0, 0.0, 0.0, blMarkerPoint, 0.0));
                    decSteps.Add(new GuidePoint(0.0, 0.0));

                    calibrationSteps = 1;
                    calibrationStartingLocation = blMarkerPoint;
                    dX = blMarkerPoint.DX(currentLocation);
                    dY = blMarkerPoint.DY(currentLocation);
                    dist = blMarkerPoint.Distance(currentLocation);
                }

                blDistanceMoved = blMarkerPoint.Distance(calibrationInitialLocation); // Need this to set nudging limit
                State = CalibrationState.GoNorth;
                phasePulses = calibrationSteps;
                goto case CalibrationState.GoNorth;
            }

            case CalibrationState.GoNorth:
            {
                var info = new CalibrationStepInfo("North", calibrationSteps, dX, dY, currentLocation, dist);
                logged.Add(info);
                decSteps.Add(new GuidePoint(dX, dY));

                if (dist < distCrit)
                {
                    if (calibrationSteps++ > MaxCalibrationSteps)
                    {
                        return Fail(CalibrationErrorCode.DecStarDidNotMove, "DEC Calibration Failed: star did not move enough", logged);
                    }

                    Status(info, $"North step {calibrationSteps,3}, dist={dist,4:F1}");
                    Schedule(GuideDirection.North, StepMs);
                    break;
                }

                // note: this calculation is reversed from the ra calculation, because that one was calibrating WEST,
                // but the angle is really relative to EAST
                if (settings.AssumeOrthogonal)
                {
                    double a1 = MountTransform.NormAngle(xAngle + Math.PI / 2.0);
                    double a2 = MountTransform.NormAngle(xAngle - Math.PI / 2.0);
                    double measured = MountTransform.PhdAngle(currentLocation, calibrationStartingLocation);
                    yAngle = Math.Abs(MountTransform.NormAngle(a1 - measured)) < Math.Abs(MountTransform.NormAngle(a2 - measured)) ? a1 : a2;
                    double decDist = dist * Math.Cos(measured - yAngle);
                    yRate = decDist / (calibrationSteps * (double)StepMs);
                }
                else
                {
                    yAngle = MountTransform.PhdAngle(currentLocation, calibrationStartingLocation);
                    yRate = dist / (calibrationSteps * (double)StepMs);
                }

                decStepCount = calibrationSteps;

                decParity = GuideParity.Unknown;
                if (calibrationStartingCoords.IsValid)
                {
                    GuidePoint endingCoords = GetRaDecCoordinates(mount);
                    if (endingCoords.IsValid)
                    {
                        // real Northward motion increases Dec
                        double ddec = endingCoords.Y - calibrationStartingCoords.Y;
                        if (ddec > OneArcsecDegrees)
                        {
                            decParity = GuideParity.Even;
                        }
                        else if (ddec < -OneArcsecDegrees)
                        {
                            decParity = GuideParity.Odd;
                        }
                    }
                }

                directions.Add(new CalibrationDirectionComplete("North", yAngle, yRate, decParity));

                // for GO_SOUTH recenterRemaining contains the total remaining duration.
                recenterRemaining = calibrationSteps * StepMs;

                if (settings.FastRecenter)
                {
                    recenterDuration = SafeFloorToInt(0.8 * settings.MaxMovePixels / yRate);
                    if (recenterDuration > settings.MaxDecDurationMs)
                    {
                        recenterDuration = settings.MaxDecDurationMs;
                    }

                    if (recenterDuration < StepMs)
                    {
                        recenterDuration = StepMs;
                    }
                }
                else
                {
                    recenterDuration = StepMs;
                }

                calibrationSteps = DivRoundUp(recenterRemaining, recenterDuration);
                State = CalibrationState.GoSouth;
                phasePulses = 0;
                southStartingLocation = currentLocation;
                southTrack.Clear();
                southPulseMs = 0;
                goto case CalibrationState.GoSouth;
            }

            case CalibrationState.GoSouth:
            {
                TrackSouth(currentLocation);
                var info = new CalibrationStepInfo("South", calibrationSteps, dX, dY, currentLocation, dist);
                logged.Add(info);
                decSteps.Add(new GuidePoint(dX, dY));

                if (recenterRemaining > 0)
                {
                    int duration = recenterDuration;
                    if (duration > recenterRemaining)
                    {
                        duration = recenterRemaining;
                    }

                    Status(info, $"South step {calibrationSteps,3}, dist={dist,4:F1}");

                    recenterRemaining -= duration;
                    --calibrationSteps;

                    Schedule(GuideDirection.South, duration);
                    southPulseMs += duration;
                    break;
                }

                // Check for obvious guide cable problem and no useful south moves
                double southDistMoved = southStartingLocation.Distance(currentLocation);
                double northDistMoved = calibrationStartingLocation.Distance(southStartingLocation);
                double southAngle = MountTransform.PhdAngle(currentLocation, southStartingLocation);

                // Want a significant south movement that re-traces the north vector to within 30 degrees
                if (Math.Abs(southDistMoved) < 0.25 * northDistMoved ||
                    Math.Abs(MountTransform.NormAngle(southAngle - (yAngle + Math.PI))) > MountTransform.Radians(30))
                {
                    string msg;
                    if (settings.IsSt4)
                    {
                        msg = Math.Abs(southDistMoved) < 0.10 * northDistMoved
                            ? "Advisory: Calibration successful but little or no south movement was measured, so guiding will " +
                              "probably be impaired. This is usually caused by a faulty guide cable or very large Dec backlash. " +
                              "Check the guide cable and read the online Help for how to identify these types of problems " +
                              "(Manual Guide, Declination backlash)."
                            : "Advisory: Calibration successful but little south movement was measured, so guiding will probably " +
                              "be impaired. This is usually caused by very large Dec backlash or other problems with the mount " +
                              "mechanics. Read the online Help for how to identify these types of problems (Manual Guide, " +
                              "Declination backlash).";
                    }
                    else
                    {
                        msg = "Advisory: Calibration successful but little south movement was measured, so guiding may be " +
                              "impaired. This is usually caused by very large Dec backlash or other problems with the mount " +
                              "mechanics. Read the online help for how to deal with this type of problem (Declination backlash).";
                    }

                    // PHD2 omits this alert (debug log only)
                    advisories.Add(new CalibrationAdvisory("CAL_LITTLE_SOUTH_MOVEMENT", msg, false));
                }

                // Compute the vector for the north moves we made - use it to make sure any nudging is going in the
                // correct direction. These are the direction cosines of the vector
                double initToSouth = calibrationInitialLocation.Distance(southStartingLocation);
                northDirCosX = calibrationInitialLocation.DX(southStartingLocation) / initToSouth;
                northDirCosY = calibrationInitialLocation.DY(southStartingLocation) / initToSouth;

                // Get magnitude and sign convention for the south moves we already made
                totalSouthAmt = MountCoordsY(southStartingLocation - currentLocation);
                State = CalibrationState.NudgeSouth;
                calibrationSteps = 0;
                phasePulses = 0;
                goto case CalibrationState.NudgeSouth;
            }

            case CalibrationState.NudgeSouth:
            {
                TrackSouth(currentLocation);
                // Nudge further South on Dec, get within 2 px North/South of starting point, don't try more than 3
                // times and don't do nudging at all if we're starting too far away from the target
                double nudgeAmt = currentLocation.Distance(calibrationInitialLocation);

                // Compute the direction cosines for the expected nudge op
                double nudgeDirCosX = currentLocation.DX(calibrationInitialLocation) / nudgeAmt;
                double nudgeDirCosY = currentLocation.DY(calibrationInitialLocation) / nudgeAmt;

                // Compute the angle between the nudge and north move vector - they should be reversed, i.e. close to 180°
                double cosTheta = nudgeDirCosX * northDirCosX + nudgeDirCosY * northDirCosY;
                double theta = Math.Acos(cosTheta);
                if (Math.Abs(Math.Abs(theta) * 180.0 / Math.PI - 180.0) < 40.0) // at least roughly in the right direction
                {
                    // Note: PHD2 tests steps <= MAX_NUDGES before incrementing, so up to MAX_NUDGES + 1 nudges are sent.
                    if (calibrationSteps <= MaxNudges && nudgeAmt > NudgeTolerance && nudgeAmt < distCrit + blDistanceMoved)
                    {
                        // Compute how much more south we need to go
                        double decAmt = MountCoordsY(currentLocation - calibrationInitialLocation);

                        if (decAmt * totalSouthAmt > 0.0) // still need to move south to reach target based on matching sign
                        {
                            decAmt = Math.Abs(decAmt); // Sign doesn't matter now, we're always moving south
                            decAmt = Math.Min(decAmt, settings.MaxMovePixels);
                            int pulseAmt = SafeFloorToInt(decAmt / yRate);
                            if (pulseAmt > StepMs)
                            {
                                pulseAmt = StepMs; // Be conservative, use durations that pushed us north in the first place
                            }

                            ++calibrationSteps;
                            Status(new CalibrationStepInfo("NudgeSouth", calibrationSteps, dX, dY, currentLocation, dist),
                                $"Nudge South {calibrationSteps,3}");
                            Schedule(GuideDirection.South, pulseAmt);
                            southPulseMs += pulseAmt;
                            break;
                        }
                    }
                }

                State = CalibrationState.Complete;
                goto case CalibrationState.Complete;
            }

            case CalibrationState.Complete:
            {
                var cal = new CalibrationData
                {
                    XAngle = xAngle,
                    YAngle = yAngle,
                    XRate = xRate,
                    YRate = yRate,
                    Declination = mount.IsConnected && mount.DeclinationDeg is { } decDeg ? MountTransform.Radians(decDeg) : null,
                    PierSide = mount.PierSide,
                    RotatorAngleDeg = mount.RotatorAngleDeg,
                    Binning = context.Optics.Binning,
                    RaGuideParity = raParity,
                    DecGuideParity = decParity,
                    GuideRateRa = mount.GuideRateRa,
                    GuideRateDec = mount.GuideRateDec,
                    PixelScale = context.Optics.PixelScale,
                    PixelSizeUm = context.Optics.PixelSizeUm,
                    FocalLengthMm = context.Optics.FocalLengthMm,
                    Timestamp = context.Clock.UtcNow,
                    RaStepCount = raStepCount,
                    DecStepCount = decStepCount,
                    RaSteps = raSteps.ToArray(),
                    DecSteps = decSteps.ToArray(),
                    Points = points.Concat(logged.Select(ToPoint)).ToArray(),
                    CalibrationStepMs = StepMs,
                    DecTakeUp = DecTakeUp(),
                    CalibrationDistancePx = DistancePx,
                };

                sanity = CalibrationSanityChecker.Check(cal, context.PreviousCalibration, settings.DecCompensationEnabled,
                    settings.SuppressedIssues);
                cal = cal with { LastIssue = sanity.ReportedType };
                result = cal;
                Result = cal;
                State = CalibrationState.Complete;
                break;
            }
        }

        points.AddRange(logged.Select(ToPoint));
        return new CalibrationUpdate
        {
            State = State,
            Pulses = pulses,
            LoggedSteps = logged,
            Status = status,
            DirectionsCompleted = directions,
            Advisories = advisories,
            Result = result,
            Sanity = sanity,
            StepsIssued = stepsIssued,
            EstimatedTotalSteps = EstimateTotal(),
        };
    }

    private readonly List<CalibrationPoint> points = [];

    private static CalibrationPoint ToPoint(CalibrationStepInfo s) => new(s.Direction, s.StepNumber, s.Position.X, s.Position.Y);

    private CalibrationUpdate Fail(CalibrationErrorCode code, string reason, IReadOnlyList<CalibrationStepInfo> logged)
    {
        State = CalibrationState.Failed;
        return new CalibrationUpdate
        {
            State = State,
            LoggedSteps = logged,
            ErrorCode = code,
            FailureReason = reason,
            StepsIssued = stepsIssued,
            EstimatedTotalSteps = stepsIssued,
        };
    }

    // Convert camera coords to mount coords (scope.cpp static MountCoords), Y component
    // the star's position after the South pulses so far (each frame of GoSouth/NudgeSouth enters once: a goto from
    // GoSouth to NudgeSouth sees the same frame again)
    private void TrackSouth(GuidePoint location)
    {
        if (southPulseMs > 0 && (southTrack.Count == 0 || southTrack[^1].PulseMs < southPulseMs))
        {
            southTrack.Add((southPulseMs, location));
        }
    }

    // the Dec return as pulse time and Dec motion since the reversal (mount px, positive towards South)
    private IReadOnlyList<DecTakeUpPoint> DecTakeUp()
    {
        if (southTrack.Count == 0 || !southStartingLocation.IsValid)
        {
            return [];
        }

        var moved = southTrack.Select(p => (p.PulseMs, Moved: MountCoordsY(southStartingLocation - p.Location))).ToList();
        double sign = Math.Sign(moved[^1].Moved);
        return sign == 0 ? [] : moved.Select(m => new DecTakeUpPoint(m.PulseMs, sign * m.Moved)).ToArray();
    }

    private double MountCoordsY(GuidePoint cameraVector) => new MountTransform(xAngle, yAngle, 1.0, 1.0).CameraToMount(cameraVector).Y;

    private static GuidePoint GetRaDecCoordinates(MountSnapshot mount) =>
        mount.IsConnected && mount.RightAscensionHours is { } ra && mount.DeclinationDeg is { } dec
            ? new GuidePoint(ra, dec)
            : GuidePoint.Invalid;

    private static int DivRoundUp(int x, int y) => (x + y - 1) / y;

    private static int SafeFloorToInt(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        double f = Math.Floor(v);
        return f >= int.MaxValue ? int.MaxValue : f <= int.MinValue ? int.MinValue : (int)f;
    }

    // ---- progress estimation (not in PHD2) ----

    private void InitEstimates(MountSnapshot mount)
    {
        double scale = context.Optics.PixelScale;
        double cosDec = mount.DeclinationDeg is { } dec ? Math.Max(Math.Cos(MountTransform.Radians(dec)), 0.1) : 1.0;
        westEstimate = EstimateLegSteps(mount.GuideRateRa, scale, cosDec);
        northEstimate = EstimateLegSteps(mount.GuideRateDec ?? mount.GuideRateRa, scale, 1.0);
        phasePulses = 0;
    }

    private int EstimateLegSteps(double? guideRate, double scale, double cosDec)
    {
        if (guideRate is not > 0 || !(scale > 0))
        {
            return CalibrationStepCalculator.DefaultSteps;
        }

        double pxPerStep = guideRate.Value * 15.0 * cosDec / scale * StepMs / 1000.0;
        int n = (int)Math.Ceiling(DistancePx / pxPerStep);
        return Math.Clamp(n, 1, MaxCalibrationSteps + 1);
    }

    private int RecenterEstimate(int legSteps, double ratePxPerMs, double factor, int maxDuration)
    {
        int dur = StepMs;
        if (settings.FastRecenter && ratePxPerMs > 0)
        {
            dur = Math.Clamp(SafeFloorToInt(factor * settings.MaxMovePixels / ratePxPerMs), StepMs, Math.Max(StepMs, maxDuration));
        }

        return DivRoundUp(legSteps * StepMs, dur);
    }

    private int EstimateTotal()
    {
        double estXRate = DistancePx / (westEstimate * (double)StepMs);
        double estYRate = DistancePx / (northEstimate * (double)StepMs);
        bool dec = settings.DecGuideMode != DecGuideMode.Off;
        int DecRemaining(int northLeft) => dec ? BacklashMinCount + northLeft + RecenterEstimate(northEstimate, estYRate, 0.8, settings.MaxDecDurationMs) + 1 : 0;

        int remaining = State switch
        {
            CalibrationState.Cleared or CalibrationState.GoWest => Math.Max(westEstimate - phasePulses, 1)
                + RecenterEstimate(Math.Max(westEstimate, phasePulses), estXRate, 1.0, settings.MaxRaDurationMs)
                + DecRemaining(northEstimate),
            CalibrationState.GoEast => Math.Max(calibrationSteps, 0) + DecRemaining(northEstimate),
            CalibrationState.ClearBacklash => Math.Max(BacklashMinCount - phasePulses, 1) + northEstimate
                + RecenterEstimate(northEstimate, estYRate, 0.8, settings.MaxDecDurationMs) + 1,
            CalibrationState.GoNorth => Math.Max(northEstimate - phasePulses, 1)
                + RecenterEstimate(Math.Max(northEstimate, phasePulses), estYRate, 0.8, settings.MaxDecDurationMs) + 1,
            CalibrationState.GoSouth => Math.Max(calibrationSteps, 0) + 1,
            CalibrationState.NudgeSouth => Math.Max(1 - phasePulses, 0),
            _ => 0,
        };

        return stepsIssued + remaining;
    }
}
