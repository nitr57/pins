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

    /// <summary>Values of <see cref="AdvancedCoachStatus.Phase"/>.</summary>
    public static class AdvancedCoachPhases {
        public const string Idle = "Idle";
        public const string Running = "Running";
        public const string Complete = "Complete";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";
    }

    /// <summary>
    /// Step names of <see cref="AdvancedCoachOptions.Steps"/>, <see cref="AdvancedCoachStatus.Step"/>,
    /// <see cref="AdvancedCoachStepStatus.Name"/> and <see cref="AdvancedCoachFinding.Step"/>.
    /// </summary>
    public static class AdvancedCoachSteps {
        public const string CameraCheck = "CameraCheck";

        /// <summary>A calibration the session needed (running step only).</summary>
        public const string Calibrating = "Calibrating";

        public const string Drift = "Drift";
        public const string MountResponse = "MountResponse";
        public const string Trials = "Trials";

        /// <summary>Building the report card (running step and findings only).</summary>
        public const string Report = "Report";

        /// <summary>Live hints while guiding (findings only).</summary>
        public const string Live = "Live";

        /// <summary>The steps <see cref="AdvancedCoachOptions.Steps"/> can select, in the order they run.</summary>
        public static IReadOnlyList<string> Selectable { get; } = new[] { CameraCheck, Drift, MountResponse, Trials };
    }

    /// <summary>Values of <see cref="AdvancedCoachFinding.Severity"/>.</summary>
    public static class AdvancedCoachSeverities {
        public const string Good = "good";
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Problem = "problem";
    }

    /// <summary>Values of <see cref="AdvancedCoachReport.Grade"/>.</summary>
    public static class AdvancedCoachGrades {
        public const string Excellent = "excellent";
        public const string Good = "good";
        public const string Fair = "fair";
        public const string Poor = "poor";
        public const string Unknown = "unknown";
    }

    /// <summary>Action ids of <see cref="IGuidingCoach.ApplyCoachActions"/>.</summary>
    public static class AdvancedCoachActions {
        /// <summary>Prefix of a trial's action id, followed by <see cref="AdvancedCoachTrial.Id"/> (e.g. "trial:B").</summary>
        public const string TrialPrefix = "trial:";
    }

    /// <summary>What a Guiding Coach session measures and for how long.</summary>
    public class AdvancedCoachOptions {
        /// <summary>Steps to run (<see cref="AdvancedCoachSteps.Selectable"/>; the report is always built). Empty = all.</summary>
        public List<string> Steps { get; set; } = new List<string>();

        /// <summary>Camera check exposures, s; empty = 1, 2, 3 s.</summary>
        public List<double> ExposureSeconds { get; set; } = new List<double>();

        /// <summary>Camera check gains; empty = current gain plus two spread over the camera's gain range (current only without a range).</summary>
        public List<int> Gains { get; set; } = new List<int>();

        /// <summary>Frames per exposure × gain combination of the camera check.</summary>
        public int FramesPerCombination { get; set; } = 5;

        /// <summary>Length of the drift measurement, s.</summary>
        public double DriftSeconds { get; set; } = 180;

        /// <summary>Length of each guided trial, s.</summary>
        public double TrialSeconds { get; set; } = 120;

        /// <summary>Guide with the current settings again at the end of the trials to detect changing conditions.</summary>
        public bool RepeatBaseline { get; set; } = true;

        /// <summary>Calibrate when a step needs a calibration and none is valid (otherwise such steps fail).</summary>
        public bool AllowCalibration { get; set; } = true;
    }

    /// <summary>Status of the running or last Guiding Coach session.</summary>
    public class AdvancedCoachStatus {
        /// <summary>One of <see cref="AdvancedCoachPhases"/>.</summary>
        public string Phase { get; set; }

        /// <summary>Failure/rejection reason (English), null otherwise.</summary>
        public string? Message { get; set; }

        /// <summary>Stable code of <see cref="Message"/> for localisation (e.g. coach.interrupted, coach.calibrationFailed), null otherwise.</summary>
        public string? MessageCode { get; set; }

        /// <summary>Parameters of <see cref="MessageCode"/> (e.g. { "reason": "slew" }).</summary>
        public Dictionary<string, object?> MessageParameters { get; set; } = new Dictionary<string, object?>();

        /// <summary>Id of the session, null before the first one.</summary>
        public string? SessionId { get; set; }

        /// <summary>UTC; null before the first session.</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>Running step (<see cref="AdvancedCoachSteps"/>): CameraCheck, Calibrating, Drift, MountResponse, Trials or Report; null when not running.</summary>
        public string? Step { get; set; }

        public List<AdvancedCoachStepStatus> Steps { get; set; } = new List<AdvancedCoachStepStatus>();

        /// <summary>Overall progress 0..1.</summary>
        public double Progress { get; set; }

        /// <summary>Time so far and estimated total, s.</summary>
        public double ElapsedSeconds { get; set; }

        public double EstimatedTotalSeconds { get; set; }

        /// <summary>Camera gain range and current settings (also when idle, for building the options); null when the camera has no gain.</summary>
        public int? GainMin { get; set; }

        public int? GainMax { get; set; }
        public int? CurrentGain { get; set; }

        /// <summary>Current guide exposure, s.</summary>
        public double CurrentExposureSeconds { get; set; }

        /// <summary>Results of the steps so far; null for steps that did not run yet.</summary>
        public AdvancedCoachCameraCheck? Camera { get; set; }

        public AdvancedCoachDrift? Drift { get; set; }
        public AdvancedCoachResponse? Response { get; set; }
        public List<AdvancedCoachTrial> Trials { get; set; } = new List<AdvancedCoachTrial>();

        /// <summary>Findings so far (all steps).</summary>
        public List<AdvancedCoachFinding> Findings { get; set; } = new List<AdvancedCoachFinding>();

        /// <summary>Set when the session completed, null otherwise.</summary>
        public AdvancedCoachReport? Report { get; set; }
    }

    /// <summary>Status of one step of a Guiding Coach session.</summary>
    public class AdvancedCoachStepStatus {
        /// <summary>CameraCheck, Drift, MountResponse or Trials.</summary>
        public string Name { get; set; }

        /// <summary>Pending, Running, Done, Skipped or Failed.</summary>
        public string State { get; set; }

        /// <summary>English sub-phase text (logs), null when none; UIs render <see cref="DetailCode"/>.</summary>
        public string? Detail { get; set; }

        /// <summary>
        /// Sub-phase code: camera.combination {exposureSeconds, gain, index, total}, calibrating, drift.measuring,
        /// response.backlash, response.pulses {direction, ms}, trial.settling {id}, trial.running {id}; null when none.
        /// </summary>
        public string? DetailCode { get; set; }

        public Dictionary<string, object?> DetailParameters { get; set; } = new Dictionary<string, object?>();

        /// <summary>0..1.</summary>
        public double Progress { get; set; }

        /// <summary>Time so far and estimated total, s.</summary>
        public double ElapsedSeconds { get; set; }

        public double EstimatedSeconds { get; set; }

        /// <summary>Why the step failed (English) and its code, null otherwise.</summary>
        public string? Message { get; set; }

        public string? MessageCode { get; set; }
        public Dictionary<string, object?> MessageParameters { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>Result of <see cref="IGuidingCoach.StartCoach"/>.</summary>
    public class AdvancedCoachStartResult {
        public bool Accepted { get; set; }

        /// <summary>Rejection reason (English), its code (coach.busy, coach.notConnected, ...) and parameters; null when accepted.</summary>
        public string? Message { get; set; }

        public string? MessageCode { get; set; }
        public Dictionary<string, object?> MessageParameters { get; set; } = new Dictionary<string, object?>();

        /// <summary>Status after the call (the new session when accepted, the unchanged status when rejected).</summary>
        public AdvancedCoachStatus Status { get; set; }
    }

    /// <summary>Result of the camera check: every exposure × gain combination and the recommended one.</summary>
    public class AdvancedCoachCameraCheck {
        public List<AdvancedCoachCameraResult> Results { get; set; } = new List<AdvancedCoachCameraResult>();

        /// <summary>The recommended combination, null when none is feasible.</summary>
        public AdvancedCoachCameraResult? Recommended { get; set; }
    }

    /// <summary>Camera check result of one exposure × gain combination.</summary>
    public class AdvancedCoachCameraResult {
        /// <summary>Exposure, s.</summary>
        public double ExposureSeconds { get; set; }

        public int Gain { get; set; }
        public int Frames { get; set; }
        public double? Snr { get; set; }

        /// <summary>Half-flux diameter, px.</summary>
        public double? Hfd { get; set; }

        public int Stars { get; set; }
        public bool Saturated { get; set; }

        /// <summary>Centroid scatter of the primary star (guiding off, linear drift removed), px and arcsec.</summary>
        public double? JitterPx { get; set; }

        public double? JitterArcsec { get; set; }
        public bool Feasible { get; set; }

        /// <summary>Why the combination is not feasible: saturated, lowSnr, noStar; null when feasible.</summary>
        public string? Reason { get; set; }
    }

    /// <summary>A sample of the drift measurement.</summary>
    public class AdvancedCoachSample {
        /// <summary>Seconds since the measurement started.</summary>
        public double T { get; set; }

        /// <summary>Mount-axis position relative to the start, arcsec.</summary>
        public double Ra { get; set; }

        public double Dec { get; set; }
    }

    /// <summary>Result of the drift measurement with the guiding output off: seeing, drift, periodic error and polar alignment.</summary>
    public class AdvancedCoachDrift {
        /// <summary>Time measured so far and planned, s.</summary>
        public double ElapsedSeconds { get; set; }

        public double TargetSeconds { get; set; }

        /// <summary>Raw samples (live plot); omitted in the history.</summary>
        public List<AdvancedCoachSample> Samples { get; set; } = new List<AdvancedCoachSample>();

        public double? SnrAvg { get; set; }

        /// <summary>High-frequency (seeing) RMS with guiding off, arcsec.</summary>
        public double? SeeingRaArcsec { get; set; }

        public double? SeeingDecArcsec { get; set; }
        public double? SeeingTotalArcsec { get; set; }

        /// <summary>RA peak-to-peak excursion, arcsec, and its fastest rate, arcsec/s.</summary>
        public double? RaPeakToPeakArcsec { get; set; }

        public double? RaMaxRateArcsecPerSec { get; set; }

        /// <summary>Drift per mount axis, arcsec/min.</summary>
        public double? RaDriftArcsecPerMin { get; set; }

        public double? DecDriftArcsecPerMin { get; set; }

        /// <summary>
        /// Periodic error fit (null when the run is too short for a period). Model on the RA samples:
        /// Ra(T) = PeriodicErrorOffsetArcsec + RaDriftArcsecPerMin * T / 60 + PeriodicErrorAmplitudeArcsec * sin(2π T / PeriodicErrorPeriodSeconds + PeriodicErrorPhaseRad),
        /// T = <see cref="AdvancedCoachSample.T"/>.
        /// </summary>
        public double? PeriodicErrorPeriodSeconds { get; set; }

        /// <summary>Amplitude (half peak-to-peak) of the fitted sinusoid, arcsec.</summary>
        public double? PeriodicErrorAmplitudeArcsec { get; set; }

        public double? PeriodicErrorPhaseRad { get; set; }
        public double? PeriodicErrorOffsetArcsec { get; set; }

        /// <summary>Polar alignment error estimated from the Dec drift, arcmin.</summary>
        public double? PolarAlignmentErrorArcmin { get; set; }

        /// <summary>True when the declination was unknown and 0° was assumed.</summary>
        public bool DeclinationAssumed { get; set; }

        /// <summary>Exposure in which the fastest RA drift moves the star as far as the RA seeing RMS, s.</summary>
        public double? DriftLimitingExposureSeconds { get; set; }

        /// <summary>Share of frame-to-frame jumps larger than 4 σ of the high-frequency jitter (wind, gusts), 0..100.</summary>
        public double? GustPercent { get; set; }
    }

    /// <summary>Result of the mount response step: Dec backlash and how the mount answers guide pulses.</summary>
    public class AdvancedCoachResponse {
        /// <summary>
        /// Dec backlash for guiding and its compensation, ms of pulse and arcsec: what a reversal after small moves loses
        /// (reversal test), else the large-move value.
        /// </summary>
        public double? BacklashMs { get; set; }

        public double? BacklashArcsec { get; set; }

        /// <summary>Measured, None (no backlash), Unreliable (e.g. Dec drift too strong, star lost) or Skipped; Measuring (no value yet) in live status; null while not measured.</summary>
        public string? BacklashState { get; set; }

        /// <summary>Dec position (arcsec) vs cumulative pulse time (ms) of the backlash test, for plotting (X = ms, Y = arcsec).</summary>
        public List<AdvancedCoachPoint> BacklashPoints { get; set; } = new List<AdvancedCoachPoint>();

        /// <summary>Lost motion at a reversal after a long move (the backlash test with large pulses), ms and arcsec; null when not measured.</summary>
        public double? LargeMoveBacklashMs { get; set; }

        public double? LargeMoveBacklashArcsec { get; set; }

        /// <summary>
        /// Pulse of the reversal-test step whose reversals moved the star (the basis of <see cref="BacklashMs"/>), ms; null when no
        /// step did (hard dead band, the large-move value stays) or the test did not run.
        /// </summary>
        public int? ReversalPulseMs { get; set; }

        /// <summary>Evaluated reversals of all reversal-test steps; DurationMs is the step's pulse.</summary>
        public List<AdvancedCoachPulse> ReversalMoves { get; set; } = new List<AdvancedCoachPulse>();

        public List<AdvancedCoachPulse> Pulses { get; set; } = new List<AdvancedCoachPulse>();

        /// <summary>Shortest pulse that moves the mount measurably, per axis, ms.</summary>
        public int? MinEffectivePulseRaMs { get; set; }

        public int? MinEffectivePulseDecMs { get; set; }

        /// <summary>West/East and North/South move ratios (1 = symmetric).</summary>
        public double? AsymmetryRa { get; set; }

        public double? AsymmetryDec { get; set; }

        /// <summary>Measured / calibrated rate.</summary>
        public double? RateRatioRa { get; set; }

        public double? RateRatioDec { get; set; }
    }

    /// <summary>A point of a coach plot.</summary>
    public class AdvancedCoachPoint {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>A test pulse of the mount response step and how far it moved the star.</summary>
    public class AdvancedCoachPulse {
        /// <summary>West, East, North or South.</summary>
        public string Direction { get; set; }

        /// <summary>Pulse, ms.</summary>
        public int DurationMs { get; set; }

        /// <summary>Expected and measured move, arcsec.</summary>
        public double ExpectedArcsec { get; set; }

        public double MovedArcsec { get; set; }

        /// <summary>Moved / expected.</summary>
        public double Ratio { get; set; }
    }

    /// <summary>A guided trial of one settings set.</summary>
    public class AdvancedCoachTrial {
        /// <summary>A (current), B (suggestion), C (variant), A2 (current again).</summary>
        public string Id { get; set; }

        /// <summary>current, suggestion, variant or currentRepeat.</summary>
        public string Kind { get; set; }

        /// <summary>Settings that differ from the current settings (empty for A/A2).</summary>
        public List<AdvancedCoachSettingChange> Settings { get; set; } = new List<AdvancedCoachSettingChange>();

        /// <summary>Pending, Settling, Running, Done, Skipped or Failed.</summary>
        public string State { get; set; }

        /// <summary>Time guided, s.</summary>
        public double ElapsedSeconds { get; set; }

        public int Frames { get; set; }

        /// <summary>Guided RMS per axis and in total, and the largest error, arcsec.</summary>
        public double? RmsRaArcsec { get; set; }

        public double? RmsDecArcsec { get; set; }
        public double? RmsTotalArcsec { get; set; }
        public double? PeakArcsec { get; set; }

        /// <summary>RA oscillation index, 0..1 (see <see cref="AdvancedGuiderStats.OscillationIndex"/>).</summary>
        public double? OscillationIndex { get; set; }

        public double? SnrAvg { get; set; }
        public bool IsWinner { get; set; }
        public bool Applied { get; set; }
    }

    /// <summary>A setting change proposed by a finding or a trial.</summary>
    public class AdvancedCoachSettingChange {
        /// <summary>Guider setting name (<see cref="AdvancedGuiderSetting.Name"/>).</summary>
        public string Name { get; set; }

        /// <summary>New value, invariant culture.</summary>
        public string Value { get; set; }

        /// <summary>Value at the time of the finding, invariant culture; null when unknown.</summary>
        public string? CurrentValue { get; set; }
    }

    /// <summary>A Guiding Coach finding or live hint: what was found, how much fixing it would gain and the setting changes that fix it.</summary>
    public class AdvancedCoachFinding {
        /// <summary>Unique within a session/report: the code, plus ":&lt;qualifier&gt;" for repeated codes (e.g. response.minPulse:Ra).</summary>
        public string Id { get; set; }

        /// <summary>Stable code for localised teaching texts, e.g. drift.polarAlignment, hint.raOscillation.</summary>
        public string Code { get; set; }

        /// <summary>CameraCheck, Drift, MountResponse, Trials, Report or Live.</summary>
        public string Step { get; set; }

        /// <summary>One of <see cref="AdvancedCoachSeverities"/>: good, info, warning or problem.</summary>
        public string Severity { get; set; }

        /// <summary>Numbers (double), strings, booleans and nulls for the text templates, e.g. { "arcmin": 7.3, "decAssumed": false }.</summary>
        public Dictionary<string, object?> Parameters { get; set; } = new Dictionary<string, object?>();

        /// <summary>Estimated total RMS improvement (arcsec) if fixed; used to rank actions.</summary>
        public double? ImpactArcsec { get; set; }

        /// <summary>Setting changes applied by <see cref="IGuidingCoach.ApplyCoachActions"/> (empty for advice only).</summary>
        public List<AdvancedCoachSettingChange> Changes { get; set; } = new List<AdvancedCoachSettingChange>();

        public bool Applied { get; set; }

        /// <summary>English fallback text (logs; UIs render from <see cref="Code"/>).</summary>
        public string Message { get; set; }

        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Live hints only: when the hint stops being shown, UTC.</summary>
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>The report card of a completed Guiding Coach session: error budget, grade, findings and ranked actions.</summary>
    public class AdvancedCoachReport {
        public string Id { get; set; }

        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Observing night (noon to noon), yyyy-MM-dd.</summary>
        public string Night { get; set; }

        /// <summary>Profile and guide camera, null when unknown.</summary>
        public string? ProfileName { get; set; }

        public string? CameraName { get; set; }

        /// <summary>Guide focal length, mm.</summary>
        public double? FocalLengthMm { get; set; }

        /// <summary>Guide pixel scale, arcsec/px.</summary>
        public double PixelScale { get; set; }

        /// <summary>Imaging camera scale from the profile, arcsec/px (null when unknown).</summary>
        public double? ImagingScale { get; set; }

        /// <summary>Declination, degrees, and pier side of the session; null when unknown.</summary>
        public double? DeclinationDeg { get; set; }

        public string? PierSide { get; set; }

        /// <summary>Steps that ran (Done).</summary>
        public List<string> Steps { get; set; } = new List<string>();

        /// <summary>Guided RMS (from the best trial, else the last guiding window), arcsec, and where it came from: trials, window or none.</summary>
        public double? GuidedRmsArcsec { get; set; }

        public double? GuidedRmsRaArcsec { get; set; }
        public double? GuidedRmsDecArcsec { get; set; }
        public string GuidedSource { get; set; }

        /// <summary>Error budget (arcsec RMS, quadrature): seeing + centroid noise + mount/other = guided.</summary>
        public double? SeeingArcsec { get; set; }

        public double? CentroidNoiseArcsec { get; set; }
        public double? MountArcsec { get; set; }

        /// <summary>Dec backlash, arcsec.</summary>
        public double? BacklashArcsec { get; set; }

        /// <summary>Polar alignment error, arcmin.</summary>
        public double? PolarAlignmentErrorArcmin { get; set; }

        /// <summary>Periodic error amplitude (half peak-to-peak), arcsec.</summary>
        public double? PeriodicErrorAmplitudeArcsec { get; set; }

        /// <summary>One of <see cref="AdvancedCoachGrades"/>: excellent, good, fair, poor or unknown.</summary>
        public string Grade { get; set; }

        /// <summary>Guided RMS / imaging scale (or guided RMS in arcsec when no imaging scale is known).</summary>
        public double? GradeRatio { get; set; }

        /// <summary>Ranked action ids (findings with warning/problem, biggest impact first).</summary>
        public List<string> Actions { get; set; } = new List<string>();

        public List<AdvancedCoachFinding> Findings { get; set; } = new List<AdvancedCoachFinding>();

        /// <summary>Results of the steps; null for steps that did not run.</summary>
        public AdvancedCoachCameraCheck? Camera { get; set; }

        public AdvancedCoachDrift? Drift { get; set; }
        public AdvancedCoachResponse? Response { get; set; }
        public List<AdvancedCoachTrial> Trials { get; set; } = new List<AdvancedCoachTrial>();
    }
}
