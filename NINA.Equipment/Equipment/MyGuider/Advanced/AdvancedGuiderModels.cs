#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable annotations

using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Equipment.Equipment.MyGuider.Advanced {

    /// <summary>Values of <see cref="AdvancedGuiderStatus.State"/> and <see cref="AdvancedIncidentFrame.State"/>.</summary>
    public static class AdvancedGuiderStates {
        public const string Stopped = "Stopped";
        public const string Looping = "Looping";
        public const string Selected = "Selected";
        public const string Calibrating = "Calibrating";
        public const string Guiding = "Guiding";
        public const string LostLock = "LostLock";

        /// <summary>The star was lost for longer than a frame while guiding; searching near the last position, then the full frame.</summary>
        public const string Reacquiring = "Reacquiring";

        public const string Paused = "Paused";

        /// <summary>Guiding was stopped by a safety check or an unrecoverable error; see <see cref="AdvancedGuiderStatus.LastError"/>.</summary>
        public const string Failed = "Failed";
    }

    /// <summary>
    /// Values of <see cref="AdvancedGuiderEventArgs.Type"/>, each with its payload. Anonymous payloads are listed with
    /// their property names (serialized camelCase as written).
    /// </summary>
    public static class AdvancedGuiderEventTypes {
        /// <summary>A guide step; payload <see cref="AdvancedGuideStep"/>.</summary>
        public const string Step = "step";

        /// <summary>An alert; payload <see cref="AdvancedGuiderAlert"/>.</summary>
        public const string Alert = "alert";

        /// <summary>
        /// The state changed (also when the Dec guide mode Drift switches direction or its safety valve opens or closes);
        /// payload <see cref="AdvancedGuiderStatus"/>.
        /// </summary>
        public const string State = "state";

        /// <summary>
        /// Calibration progress. Payload while calibrating: <c>{ step, progress (0..1), direction, dx, dy, distance (px) }</c>;
        /// when complete: <see cref="AdvancedGuiderCalibration"/>; when failed: <c>{ step, failed: true, reason }</c>.
        /// </summary>
        public const string Calibration = "calibration";

        /// <summary>
        /// Settling after a start or a dither. Payload <c>{ status: "begin" }</c>, then
        /// <c>{ status: "settling", distance (px), time (s), settleTime (s), starLocked }</c>, and finally
        /// <c>{ status: "done" | "failed", error, totalFrames, droppedFrames }</c>.
        /// </summary>
        public const string Settle = "settle";

        /// <summary>A new frame (fetch it with <see cref="IAdvancedGuider.GetLatestFrame"/>); payload <c>{ frameNumber, width, height, stars }</c>.</summary>
        public const string Frame = "frame";

        /// <summary>Statistics after a guide step; payload <c>{ window, session }</c>, each an <see cref="AdvancedGuiderStats"/>.</summary>
        public const string Stats = "stats";

        /// <summary>
        /// Dark library progress. Payload <c>{ status: "capturing", exposure (s), frame, framesPerExposure, index, total }</c>,
        /// then <c>{ status: "done", total, message }</c> or <c>{ status: "failed", message }</c>.
        /// </summary>
        public const string Darks = "darks";

        /// <summary>Guiding Coach progress (<see cref="IGuidingCoach"/>); payload <see cref="AdvancedCoachStatus"/>.</summary>
        public const string Coach = "coach";

        /// <summary>A new live coaching hint; payload <see cref="AdvancedCoachFinding"/>.</summary>
        public const string Hint = "hint";

        /// <summary>An incident started, was saved or deleted (<see cref="IGuideIncidentRecorder"/>); payload <see cref="AdvancedIncidentEvent"/>.</summary>
        public const string Incident = "incident";

        /// <summary>A dither was applied; payload <c>{ dx, dy }</c> (camera axes, px).</summary>
        public const string Dither = "dither";

        /// <summary>The guide star was lost this frame; payload <c>{ frame, snr, mass, status }</c>.</summary>
        public const string StarLost = "starlost";
    }

    /// <summary>Values of <see cref="AdvancedGuiderAlert.Severity"/>.</summary>
    public static class AdvancedGuiderAlertSeverities {
        public const string Info = "Info";
        public const string Warning = "Warning";

        /// <summary>Guiding stopped or cannot continue.</summary>
        public const string Critical = "Critical";
    }

    /// <summary>Values of <see cref="AdvancedDecDriftState.Direction"/>.</summary>
    public static class AdvancedDecDriftDirections {
        /// <summary>Both directions, while no clear drift is measured.</summary>
        public const string Both = "Both";

        public const string North = "North";
        public const string South = "South";
    }

    /// <summary>Snapshot of an <see cref="IAdvancedGuider"/>: state, star, calibration progress, statistics and learned state.</summary>
    public class AdvancedGuiderStatus {
        /// <summary>One of <see cref="AdvancedGuiderStates"/>.</summary>
        public string State { get; set; }

        public bool Connected { get; set; }
        public bool IsSettling { get; set; }
        public bool IsCalibrated { get; set; }
        public string CameraName { get; set; }

        /// <summary>Guide exposure, s.</summary>
        public double ExposureSeconds { get; set; }

        /// <summary>Guide camera scale, arcsec/px.</summary>
        public double PixelScale { get; set; }

        public long FrameNumber { get; set; }

        /// <summary>Processing time of the last frame, ms.</summary>
        public double LastProcessingMs { get; set; }

        /// <summary>Lock position, camera px; null when none.</summary>
        public double? LockX { get; set; }

        public double? LockY { get; set; }

        /// <summary>The primary guide star, null when none is selected.</summary>
        public AdvancedGuideStar? PrimaryStar { get; set; }

        public int StarsUsed { get; set; }
        public int StarCount { get; set; }

        /// <summary>Calibration step text, empty when not calibrating.</summary>
        public string CalibrationStep { get; set; }

        /// <summary>Calibration progress, 0..1.</summary>
        public double CalibrationProgress { get; set; }

        /// <summary>Statistics of the recent window and of the whole session; null when not connected.</summary>
        public AdvancedGuiderStats? WindowStats { get; set; }

        public AdvancedGuiderStats? SessionStats { get; set; }

        /// <summary>The last warning or critical alert, null when none.</summary>
        public AdvancedGuiderAlert? LastError { get; set; }

        /// <summary>Settle status text, empty when none.</summary>
        public string SettleStatus { get; set; }

        /// <summary>Dark library in use (file name and number of darks), empty when none.</summary>
        public string DarkLibrary { get; set; }

        /// <summary>Active (not expired, not dismissed) live coaching hints while guiding.</summary>
        public List<AdvancedCoachFinding> Hints { get; set; } = new List<AdvancedCoachFinding>();

        /// <summary>True while a Guiding Coach session is running (guiding commands are refused or cancel it).</summary>
        public bool CoachRunning { get; set; }

        /// <summary>What the RA guide algorithm has learned, for algorithms that learn (Predictive); null otherwise.</summary>
        public AdvancedGuideAlgorithmState? RaAlgorithmState { get; set; }

        /// <summary>What the Dec guide algorithm has learned, for algorithms that learn (Predictive); null otherwise.</summary>
        public AdvancedGuideAlgorithmState? DecAlgorithmState { get; set; }

        /// <summary>The direction Dec guides in and the drift it follows, in Dec guide mode Drift; null in the other Dec guide modes.</summary>
        public AdvancedDecDriftState? DecDrift { get; set; }
    }

    /// <summary>State of Dec guide mode Drift, which guides Dec in one direction only: the one that counters the measured drift.</summary>
    public class AdvancedDecDriftState {
        /// <summary>
        /// The Dec direction guided now (<see cref="AdvancedDecDriftDirections"/>): North or South (the one that counters
        /// the drift), or Both while no clear drift is measured.
        /// </summary>
        public string Direction { get; set; }

        /// <summary>
        /// The measured Dec drift the direction follows, arcsec/min, positive when the mount drifts the way North pulses
        /// move it (South pulses correct it); null before there is enough data.
        /// </summary>
        public double? DriftArcsecPerMin { get; set; }

        /// <summary>True while both directions bring back a large error on the side the direction can't correct.</summary>
        public bool SafetyValveOpen { get; set; }
    }

    /// <summary>State of a guide algorithm that learns the sky and the mount (the internal guider's Predictive algorithm) on one axis.</summary>
    public class AdvancedGuideAlgorithmState {
        /// <summary>Algorithm name, e.g. Predictive.</summary>
        public string Name { get; set; }

        /// <summary>Learning (first frames, see <see cref="Progress"/>) or Adapted.</summary>
        public string Phase { get; set; }

        /// <summary>Learning progress, 0 … 1.</summary>
        public double Progress { get; set; }

        /// <summary>Guide frames learned from since the algorithm started.</summary>
        public int FramesLearned { get; set; }

        /// <summary>Share of each guide frame's measurement the algorithm follows, 0 … 1 (the rest is taken as seeing).</summary>
        public double Gain { get; set; }

        /// <summary>Measurement noise per frame (seeing and centroid), σ, px and arcsec.</summary>
        public double SeeingPx { get; set; }

        public double SeeingArcsec { get; set; }

        /// <summary>The mount's own random motion per frame, σ, px and arcsec.</summary>
        public double WanderPx { get; set; }

        public double WanderArcsec { get; set; }

        /// <summary>Drift the algorithm predicts and corrects in advance, px/min and arcsec/min.</summary>
        public double DriftPxPerMin { get; set; }

        public double DriftArcsecPerMin { get; set; }

        /// <summary>Recent prediction error relative to the long-run one (about 1 while nothing changes), null before there is data.</summary>
        public double? Fit { get; set; }

        /// <summary>Times the algorithm switched to a better-fitting model.</summary>
        public int ModelChanges { get; set; }

        /// <summary>Guide frames since the last model change, null without one.</summary>
        public int? FramesSinceModelChange { get; set; }

        /// <summary>The RA worm's periodic error the algorithm learns and predicts; null on Dec.</summary>
        public AdvancedPeriodicErrorState? PeriodicError { get; set; }
    }

    /// <summary>Periodic error of the RA drive as learned by the Predictive algorithm.</summary>
    public class AdvancedPeriodicErrorState {
        /// <summary>
        /// Off, Learning (see <see cref="Progress"/>; also while the learned curve is not stable), Ready (learned and
        /// stable, not predicting better yet), Predicting, or Negligible (too small to matter, whether or not the curve
        /// is stable yet: between two guide frames it moves the star by a small fraction of the seeing, and predicting
        /// it does not help).
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// Learning progress, 0 … 1, from the data since the last slew, sync or meridian flip (period detection, then
        /// the data the curve needs to be trusted).
        /// </summary>
        public double Progress { get; set; }

        /// <summary>True while the curve is stable like a mechanical periodic error.</summary>
        public bool Stable { get; set; }

        /// <summary>Period in seconds (the worm's when <see cref="WormTeeth"/> is known), null while it is being detected.</summary>
        public double? PeriodSeconds { get; set; }

        /// <summary>
        /// Teeth of the worm wheel: set, or snapped from a stable detected period; null for any other period, which is
        /// then not taken for the worm's.
        /// </summary>
        public int? WormTeeth { get; set; }

        /// <summary>Semi-amplitude of the worm fundamental at the current target, arcsec and px; null before a curve is fitted.</summary>
        public double? AmplitudeArcsec { get; set; }

        public double? AmplitudePx { get; set; }

        /// <summary>Share of the prediction in the correction, 0 … 1 (it fades in and out).</summary>
        public double Weight { get; set; }
    }

    /// <summary>Guiding statistics over a window of recent frames or the whole session.</summary>
    public class AdvancedGuiderStats {
        /// <summary>Frames the values are computed from (dither and settle frames are left out).</summary>
        public int Frames { get; set; }

        /// <summary>RMS about the mean per mount axis and in total (hypot of both), arcsec and px.</summary>
        public double RmsRaArcsec { get; set; }

        public double RmsDecArcsec { get; set; }
        public double RmsTotalArcsec { get; set; }
        public double RmsRaPx { get; set; }
        public double RmsDecPx { get; set; }
        public double RmsTotalPx { get; set; }

        /// <summary>Largest error per mount axis, arcsec.</summary>
        public double PeakRaArcsec { get; set; }

        public double PeakDecArcsec { get; set; }

        /// <summary>Uncorrected drift per mount axis (linear fit), arcsec/min; null with too few frames.</summary>
        public double? DriftRaArcsecPerMin { get; set; }

        public double? DriftDecArcsecPerMin { get; set; }

        /// <summary>Polar alignment error estimated from the Dec drift, arcmin; null without a Dec drift.</summary>
        public double? PolarAlignmentErrorArcmin { get; set; }

        /// <summary>RA oscillation index as in PHD2, 0..1: the share of consecutive frames whose RA error is on opposite sides.</summary>
        public double OscillationIndex { get; set; }

        /// <summary>Share of frames with a pulse, per axis, %.</summary>
        public double RaDutyPercent { get; set; }

        public double DecDutyPercent { get; set; }

        /// <summary>Guide star SNR (lowest, mean, last); null without a star.</summary>
        public double? SnrMin { get; set; }

        public double? SnrAvg { get; set; }
        public double? SnrLast { get; set; }

        /// <summary>Mean number of stars used per frame; null without frames.</summary>
        public double? AvgStarCount { get; set; }

        public int StarLostCount { get; set; }

        /// <summary>Time covered, s.</summary>
        public double ElapsedSeconds { get; set; }
    }

    /// <summary>One guide step: the measured error and the pulses sent for one guide frame.</summary>
    public class AdvancedGuideStep {
        public long Frame { get; set; }

        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Seconds since guiding started.</summary>
        public double Time { get; set; }

        /// <summary>Star offset from the lock position on the camera axes, px.</summary>
        public double Dx { get; set; }

        public double Dy { get; set; }

        /// <summary>Mount-axis error in guide pixels.</summary>
        public double RaDistanceRaw { get; set; }

        public double DecDistanceRaw { get; set; }

        /// <summary>Mount-axis error in arcsec.</summary>
        public double RaArcsec { get; set; }

        public double DecArcsec { get; set; }

        /// <summary>RA pulse, ms.</summary>
        public int RaDuration { get; set; }

        /// <summary>East/West or empty.</summary>
        public string RaDirection { get; set; }

        /// <summary>Dec pulse, ms.</summary>
        public int DecDuration { get; set; }

        /// <summary>North/South or empty.</summary>
        public string DecDirection { get; set; }

        /// <summary>True when the pulse was cut to the maximum duration.</summary>
        public bool RaLimited { get; set; }

        public bool DecLimited { get; set; }
        public double Snr { get; set; }

        /// <summary>Star mass (background-subtracted flux) as in PHD2, ADU.</summary>
        public double StarMass { get; set; }

        /// <summary>Half-flux diameter, px.</summary>
        public double Hfd { get; set; }

        public int StarsUsed { get; set; }
        public bool IsSettling { get; set; }

        /// <summary>True for the fast recenter move after a dither.</summary>
        public bool IsRecenterMove { get; set; }

        /// <summary>True when the primary star was not measured and its position was estimated from the other stars.</summary>
        public bool PrimaryEstimated { get; set; }

        /// <summary>Smoothed distance from the lock position as in PHD2, px.</summary>
        public double AvgDist { get; set; }
    }

    /// <summary>An alert raised by the guider, with an explanation and a fix for the user.</summary>
    public class AdvancedGuiderAlert {
        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        public int Code { get; set; }
        public string CodeName { get; set; }

        /// <summary>One of <see cref="AdvancedGuiderAlertSeverities"/>.</summary>
        public string Severity { get; set; }

        public string Title { get; set; }
        public string Explanation { get; set; }
        public string Fix { get; set; }

        /// <summary>Details of this occurrence, empty when none.</summary>
        public string Detail { get; set; }

        /// <summary>The incident this alert started or joined (flight recorder), null when none.</summary>
        public string? IncidentId { get; set; }
    }

    /// <summary>A star measured in a guide frame, for overlays.</summary>
    public class AdvancedGuideStar {
        /// <summary>Position, camera px.</summary>
        public double X { get; set; }

        public double Y { get; set; }
        public double Snr { get; set; }

        /// <summary>Star mass (background-subtracted flux), ADU.</summary>
        public double Mass { get; set; }

        /// <summary>Half-flux diameter, px.</summary>
        public double Hfd { get; set; }

        public bool IsPrimary { get; set; }
        public bool Used { get; set; }

        /// <summary>Weight in the combined offset: 1 for the primary, less for used secondaries, 0 when not used.</summary>
        public double Weight { get; set; }

        /// <summary>Why the star was not used this frame (Miss, Lost, NotMeasured, ...), null when used.</summary>
        public string? RejectReason { get; set; }
    }

    /// <summary>Values of <see cref="AdvancedStarSelectionResult.Error"/>.</summary>
    public static class AdvancedStarSelectionErrors {
        /// <summary>No star was found around the position.</summary>
        public const string NoStar = "NoStar";

        /// <summary>The star is too close to the frame edge to track.</summary>
        public const string NearEdge = "NearEdge";

        /// <summary>The guider is guiding, calibrating, starting to guide or running the Coach.</summary>
        public const string Busy = "Busy";

        /// <summary>The guider is not looping exposures.</summary>
        public const string NotLooping = "NotLooping";

        /// <summary>A newer selection replaced it, or looping stopped first.</summary>
        public const string Cancelled = "Cancelled";

        /// <summary>No frame arrived in time.</summary>
        public const string TimedOut = "TimedOut";
    }

    /// <summary>Result of <see cref="IAdvancedGuider.SelectGuideStar"/>.</summary>
    public class AdvancedStarSelectionResult {
        public bool Success { get; set; }

        /// <summary>Why it failed (<see cref="AdvancedStarSelectionErrors"/>), null on success.</summary>
        public string? Error { get; set; }

        /// <summary>The reason in English, null on success.</summary>
        public string? Message { get; set; }

        /// <summary>The selected star, null on failure.</summary>
        public AdvancedGuideStar? Star { get; set; }

        /// <summary>Secondary stars found around it (multi-star mode).</summary>
        public int SecondaryStars { get; set; }
    }

    /// <summary>A processed guide frame with its stars and lock position.</summary>
    public class AdvancedGuiderFrame {
        public long FrameNumber { get; set; }

        /// <summary>Exposure start, UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Size, px.</summary>
        public int Width { get; set; }

        public int Height { get; set; }
        public int BitDepth { get; set; }

        /// <summary>Row-major 16-bit pixels (after dark/defect correction).</summary>
        public ushort[] Pixels { get; set; }

        /// <summary>Lock position, px; null when none.</summary>
        public double? LockX { get; set; }

        public double? LockY { get; set; }
        public List<AdvancedGuideStar> Stars { get; set; } = new List<AdvancedGuideStar>();
    }

    /// <summary>The guider's calibration: camera angles and guide rates of both mount axes.</summary>
    public class AdvancedGuiderCalibration {
        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Camera angle of the RA axis (direction a star moves for an East pulse), degrees.</summary>
        public double RaAngleDeg { get; set; }

        /// <summary>Camera angle of the Dec axis (direction a star moves for a North pulse), degrees.</summary>
        public double DecAngleDeg { get; set; }

        /// <summary>Guide rates, px/s and arcsec/s.</summary>
        public double RaRatePxPerSec { get; set; }

        public double DecRatePxPerSec { get; set; }
        public double RaRateArcsecPerSec { get; set; }
        public double DecRateArcsecPerSec { get; set; }

        /// <summary>How far the axes are from perpendicular, degrees.</summary>
        public double OrthogonalityErrorDeg { get; set; }

        /// <summary>Declination at calibration, degrees; null when unknown.</summary>
        public double? DeclinationDeg { get; set; }

        public string PierSide { get; set; }
        public int Binning { get; set; }
        public int RaSteps { get; set; }
        public int DecSteps { get; set; }
        public string LastIssue { get; set; }
        public bool DecFlipRequired { get; set; }

        /// <summary>Star position after every calibration step (camera px), empty for older calibrations.</summary>
        public List<AdvancedCalibrationPoint> Points { get; set; } = new List<AdvancedCalibrationPoint>();
    }

    /// <summary>Star position after one calibration step.</summary>
    public class AdvancedCalibrationPoint {
        /// <summary>Start, West, East, Backlash, North, South or NudgeSouth.</summary>
        public string Direction { get; set; }

        public int Step { get; set; }

        /// <summary>Position, camera px.</summary>
        public double X { get; set; }

        public double Y { get; set; }
    }

    /// <summary>A guider setting with the metadata to build a settings form.</summary>
    public class AdvancedGuiderSetting {
        public string Name { get; set; }
        public string Label { get; set; }
        public string Group { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// int, double, bool, string, enum, or action: a button, not a value; setting it to any value (e.g. "true") runs
        /// the action (<see cref="Description"/> says what it does).
        /// </summary>
        public string Type { get; set; }

        /// <summary>Current value as invariant-culture string.</summary>
        public string Value { get; set; }

        public string DefaultValue { get; set; }

        /// <summary>Range of numeric settings, null when unbounded.</summary>
        public double? Min { get; set; }

        public double? Max { get; set; }

        /// <summary>Unit of the value, null when it has none.</summary>
        public string? Unit { get; set; }

        /// <summary>Allowed values for enum settings, null for the other types.</summary>
        public List<string>? Options { get; set; }

        /// <summary>True when the value is only applied on the next connect.</summary>
        public bool RequiresReconnect { get; set; }

        /// <summary>True for the essential settings shown in the basic settings view.</summary>
        public bool Basic { get; set; }

        /// <summary>Name of the setting that decides whether this one applies (e.g. RaAlgorithm); empty when it always applies.</summary>
        public string DependsOn { get; set; }

        /// <summary>Values of <see cref="DependsOn"/> for which this setting applies; UIs hide it for other values. Null when it always applies.</summary>
        public List<string>? AppliesTo { get; set; }
    }

    /// <summary>Event pushed to UIs through <see cref="IAdvancedGuider.AdvancedGuiderEvent"/>.</summary>
    public class AdvancedGuiderEventArgs : EventArgs {
        /// <summary>One of <see cref="AdvancedGuiderEventTypes"/>.</summary>
        public string Type { get; set; }

        /// <summary>When it happened, UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>The payload of <see cref="Type"/> as listed in <see cref="AdvancedGuiderEventTypes"/>.</summary>
        public object? Payload { get; set; }
    }
}
