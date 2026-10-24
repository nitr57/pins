// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Coach;

// Engine mirror of the Guiding Coach DTOs of PINS' IAdvancedGuider (AdvancedCoach*): same property names and value
// conventions so the plugin can map them 1:1. Instances published in snapshots are never modified afterwards.

/// <summary>Session options (AdvancedCoachOptions).</summary>
public sealed record CoachOptions
{
    /// <summary>Steps to run: CameraCheck, Drift, MountResponse, Trials (the report is always built). Empty = all.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Camera check exposures in seconds; empty = 1, 2, 3 s.</summary>
    public IReadOnlyList<double> ExposureSeconds { get; init; } = [];

    /// <summary>Camera check gains; empty = current gain plus two spread over the camera's gain range (current only without a range).</summary>
    public IReadOnlyList<int> Gains { get; init; } = [];

    public int FramesPerCombination { get; init; } = 5;

    /// <summary>Drift measurement duration (seconds, at least 120).</summary>
    public double DriftSeconds { get; init; } = 180;

    /// <summary>Guided time per trial (seconds, at least 30), after settling.</summary>
    public double TrialSeconds { get; init; } = 120;

    /// <summary>Guide with the current settings again at the end of the trials to detect changing conditions.</summary>
    public bool RepeatBaseline { get; init; } = true;

    /// <summary>Calibrate when a step needs a calibration and none is valid (otherwise such steps fail).</summary>
    public bool AllowCalibration { get; init; } = true;
}

/// <summary>Session status snapshot (AdvancedCoachStatus).</summary>
public sealed record CoachStatus
{
    /// <summary><see cref="CoachPhases"/>: Idle, Running, Complete, Cancelled or Failed.</summary>
    public string Phase { get; init; } = CoachPhases.Idle;

    /// <summary>Failure/rejection/interruption reason (English), null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>Stable code of <see cref="Message"/> (<see cref="CoachCodes"/>, e.g. coach.interrupted, coach.calibrationFailed).</summary>
    public string? MessageCode { get; init; }

    /// <summary>Parameters of <see cref="MessageCode"/> (e.g. { "reason": "slew" }).</summary>
    public Dictionary<string, object?> MessageParameters { get; init; } = new();

    public string? SessionId { get; init; }

    public DateTime? StartedAt { get; init; }

    /// <summary>Running step: CameraCheck, Calibrating, Drift, MountResponse, Trials or Report; null when not running.</summary>
    public string? Step { get; init; }

    public IReadOnlyList<CoachStepStatus> Steps { get; init; } = [];

    /// <summary>Overall progress 0..1.</summary>
    public double Progress { get; init; }

    public double ElapsedSeconds { get; init; }

    public double EstimatedTotalSeconds { get; init; }

    public int? GainMin { get; init; }

    public int? GainMax { get; init; }

    public int? CurrentGain { get; init; }

    public double CurrentExposureSeconds { get; init; }

    public CoachCameraCheck? Camera { get; init; }

    public CoachDrift? Drift { get; init; }

    public CoachResponse? Response { get; init; }

    public IReadOnlyList<CoachTrial> Trials { get; init; } = [];

    /// <summary>Findings so far (all steps).</summary>
    public IReadOnlyList<CoachFinding> Findings { get; init; } = [];

    /// <summary>Set when the session completed.</summary>
    public CoachReport? Report { get; init; }
}

/// <summary>Per-step status (AdvancedCoachStepStatus).</summary>
public sealed record CoachStepStatus
{
    /// <summary>CameraCheck, Drift, MountResponse or Trials.</summary>
    public required string Name { get; init; }

    /// <summary><see cref="CoachStepStates"/>: Pending, Running, Done, Skipped or Failed.</summary>
    public string State { get; init; } = CoachStepStates.Pending;

    /// <summary>English sub-phase text (logs); UIs render <see cref="DetailCode"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Sub-phase code (<see cref="CoachDetailCodes"/>): camera.combination {exposureSeconds, gain, index, total}, calibrating,
    /// drift.measuring, response.backlash, response.pulses {direction, ms}, trial.settling {id}, trial.running {id}; null when none.
    /// </summary>
    public string? DetailCode { get; init; }

    public Dictionary<string, object?> DetailParameters { get; init; } = new();

    public double Progress { get; init; }

    public double ElapsedSeconds { get; init; }

    public double EstimatedSeconds { get; init; }

    public string? Message { get; init; }

    public string? MessageCode { get; init; }

    public Dictionary<string, object?> MessageParameters { get; init; } = new();
}

/// <summary>Result of <see cref="GuidingCoach.Start"/> (AdvancedCoachStartResult).</summary>
public sealed record CoachStartResult
{
    public bool Accepted { get; init; }

    /// <summary>Rejection reason (English), its code (coach.busy, coach.notConnected, ...) and parameters; null when accepted.</summary>
    public string? Message { get; init; }

    public string? MessageCode { get; init; }

    public Dictionary<string, object?> MessageParameters { get; init; } = new();

    /// <summary>Status after the call: the new session when accepted, the unchanged status when rejected.</summary>
    public required CoachStatus Status { get; init; }
}

public sealed record CoachCameraCheck
{
    public IReadOnlyList<CoachCameraResult> Results { get; init; } = [];

    public CoachCameraResult? Recommended { get; init; }
}

/// <summary>One exposure × gain combination of the camera check.</summary>
public sealed record CoachCameraResult
{
    public double ExposureSeconds { get; init; }

    /// <summary>Gain used (-1 = camera default, no gain requested).</summary>
    public int Gain { get; init; }

    public int Frames { get; init; }

    public double? Snr { get; init; }

    /// <summary>Half flux diameter of the primary star, px.</summary>
    public double? Hfd { get; init; }

    /// <summary>Usable stars (AutoFind, SNR ≥ the minimum star SNR).</summary>
    public int Stars { get; init; }

    public bool Saturated { get; init; }

    /// <summary>Centroid scatter of the primary star (linear drift removed), px.</summary>
    public double? JitterPx { get; init; }

    public double? JitterArcsec { get; init; }

    public bool Feasible { get; init; }

    /// <summary>saturated, lowSnr or noStar; null when feasible.</summary>
    public string? Reason { get; init; }
}

public sealed record CoachSample(double T, double Ra, double Dec);

public sealed record CoachDrift
{
    public double ElapsedSeconds { get; init; }

    public double TargetSeconds { get; init; }

    /// <summary>Mount-axis positions relative to the start (arcsec); omitted in the history.</summary>
    public IReadOnlyList<CoachSample> Samples { get; init; } = [];

    public double? SnrAvg { get; init; }

    public double? SeeingRaArcsec { get; init; }

    public double? SeeingDecArcsec { get; init; }

    public double? SeeingTotalArcsec { get; init; }

    public double? RaPeakToPeakArcsec { get; init; }

    public double? RaMaxRateArcsecPerSec { get; init; }

    public double? RaDriftArcsecPerMin { get; init; }

    public double? DecDriftArcsecPerMin { get; init; }

    /// <summary>
    /// Periodic error fit (null when the run is too short for a period). Model on the RA samples:
    /// Ra(T) = PeriodicErrorOffsetArcsec + RaDriftArcsecPerMin·T/60 + PeriodicErrorAmplitudeArcsec·sin(2πT/PeriodicErrorPeriodSeconds + PeriodicErrorPhaseRad),
    /// T = <see cref="CoachSample.T"/>.
    /// </summary>
    public double? PeriodicErrorPeriodSeconds { get; init; }

    /// <summary>Amplitude (half peak-to-peak) of the fitted sinusoid; without a period half the peak-to-peak of the detrended smoothed RA motion, arcsec.</summary>
    public double? PeriodicErrorAmplitudeArcsec { get; init; }

    public double? PeriodicErrorPhaseRad { get; init; }

    public double? PeriodicErrorOffsetArcsec { get; init; }

    public double? PolarAlignmentErrorArcmin { get; init; }

    public bool DeclinationAssumed { get; init; }

    public double? DriftLimitingExposureSeconds { get; init; }

    /// <summary>Share of frame-to-frame jumps larger than 4 σ of the high-frequency jitter (wind, gusts), 0..100.</summary>
    public double? GustPercent { get; init; }
}

public sealed record CoachResponse
{
    /// <summary>
    /// Dec backlash for guiding and its compensation: the pulse time a reversal after small moves loses (reversal test),
    /// else the large-move value.
    /// </summary>
    public double? BacklashMs { get; init; }

    public double? BacklashArcsec { get; init; }

    /// <summary><see cref="CoachBacklashStates"/>: Measured, None, Unreliable or Skipped; Measuring (no value yet) in live status.</summary>
    public string? BacklashState { get; init; }

    /// <summary>Dec position (arcsec) vs cumulative pulse time (ms) of the backlash test.</summary>
    public IReadOnlyList<CoachPoint> BacklashPoints { get; init; } = [];

    /// <summary>Lost motion at a reversal after a long move (the backlash test with large pulses), ms; null when not measured.</summary>
    public double? LargeMoveBacklashMs { get; init; }

    public double? LargeMoveBacklashArcsec { get; init; }

    /// <summary>
    /// Pulse length of the reversal-test ladder step whose reversals moved the star (the guiding backlash's basis), ms; null
    /// when no step did (hard dead band, the large-move value stays) or the test did not run.
    /// </summary>
    public int? ReversalPulseMs { get; init; }

    /// <summary>Evaluated reversals of all ladder steps (without the lead-in pulse of each step); DurationMs is the step.</summary>
    public IReadOnlyList<CoachPulse> ReversalMoves { get; init; } = [];

    public IReadOnlyList<CoachPulse> Pulses { get; init; } = [];

    public int? MinEffectivePulseRaMs { get; init; }

    public int? MinEffectivePulseDecMs { get; init; }

    /// <summary>West/East move ratio (1 = symmetric).</summary>
    public double? AsymmetryRa { get; init; }

    /// <summary>North/South move ratio.</summary>
    public double? AsymmetryDec { get; init; }

    /// <summary>Measured / calibrated rate.</summary>
    public double? RateRatioRa { get; init; }

    public double? RateRatioDec { get; init; }
}

public sealed record CoachPoint(double X, double Y);

public sealed record CoachPulse
{
    /// <summary>West, East, North or South.</summary>
    public required string Direction { get; init; }

    public int DurationMs { get; init; }

    public double ExpectedArcsec { get; init; }

    public double MovedArcsec { get; init; }

    /// <summary>Moved / expected.</summary>
    public double Ratio { get; init; }
}

public sealed record CoachTrial
{
    /// <summary>A (current), B (suggestion), C (variant), A2 (current again).</summary>
    public required string Id { get; init; }

    /// <summary>current, suggestion, variant or currentRepeat.</summary>
    public required string Kind { get; init; }

    /// <summary>Settings that differ from the current settings (empty for A/A2).</summary>
    public IReadOnlyList<CoachSettingChange> Settings { get; init; } = [];

    /// <summary><see cref="CoachTrialStates"/>: Pending, Settling, Running, Done, Skipped or Failed.</summary>
    public string State { get; init; } = CoachTrialStates.Pending;

    public double ElapsedSeconds { get; init; }

    public int Frames { get; init; }

    public double? RmsRaArcsec { get; init; }

    public double? RmsDecArcsec { get; init; }

    public double? RmsTotalArcsec { get; init; }

    public double? PeakArcsec { get; init; }

    public double? OscillationIndex { get; init; }

    public double? SnrAvg { get; init; }

    public bool IsWinner { get; init; }

    public bool Applied { get; init; }
}

/// <summary>A plugin setting change (<see cref="CoachSettingNames"/>); values are invariant-culture strings.</summary>
public sealed record CoachSettingChange(string Name, string Value, string? CurrentValue = null);

public sealed record CoachFinding
{
    /// <summary>Unique within a session/report: the code, plus ":qualifier" for repeated codes (e.g. response.minPulse:Ra).</summary>
    public required string Id { get; init; }

    /// <summary>Stable code for localised texts (<see cref="CoachCodes"/>).</summary>
    public required string Code { get; init; }

    /// <summary>CameraCheck, Drift, MountResponse, Trials, Report or Live.</summary>
    public required string Step { get; init; }

    /// <summary><see cref="CoachSeverities"/>: good, info, warning or problem.</summary>
    public required string Severity { get; init; }

    /// <summary>Parameters for the text templates: double, int, string, bool or null values.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Estimated total RMS improvement (arcsec) if fixed; used to rank actions.</summary>
    public double? ImpactArcsec { get; init; }

    /// <summary>Setting changes applied by ApplyCoachActions (empty for advice only).</summary>
    public IReadOnlyList<CoachSettingChange> Changes { get; init; } = [];

    public bool Applied { get; init; }

    /// <summary>English fallback text (logs; UIs render from <see cref="Code"/>).</summary>
    public string Message { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; }

    /// <summary>Live hints only: when the hint stops being shown.</summary>
    public DateTime? ExpiresAt { get; init; }
}

public sealed record CoachReport
{
    public required string Id { get; init; }

    public DateTime Timestamp { get; init; }

    /// <summary>Observing night (noon to noon, local time), yyyy-MM-dd.</summary>
    public string Night { get; init; } = string.Empty;

    public string? ProfileName { get; init; }

    public string? CameraName { get; init; }

    public double? FocalLengthMm { get; init; }

    /// <summary>Guide pixel scale, arcsec/px.</summary>
    public double PixelScale { get; init; }

    /// <summary>Imaging camera scale, arcsec/px (null when unknown).</summary>
    public double? ImagingScale { get; init; }

    public double? DeclinationDeg { get; init; }

    public string? PierSide { get; init; }

    /// <summary>Steps that ran (Done).</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    public double? GuidedRmsArcsec { get; init; }

    public double? GuidedRmsRaArcsec { get; init; }

    public double? GuidedRmsDecArcsec { get; init; }

    /// <summary>trials, window or none.</summary>
    public string GuidedSource { get; init; } = CoachGuidedSources.None;

    public double? SeeingArcsec { get; init; }

    public double? CentroidNoiseArcsec { get; init; }

    public double? MountArcsec { get; init; }

    public double? BacklashArcsec { get; init; }

    public double? PolarAlignmentErrorArcmin { get; init; }

    public double? PeriodicErrorAmplitudeArcsec { get; init; }

    /// <summary>excellent, good, fair, poor or unknown.</summary>
    public string Grade { get; init; } = CoachGrades.Unknown;

    /// <summary>Guided RMS / imaging scale (or guided RMS in arcsec without an imaging scale).</summary>
    public double? GradeRatio { get; init; }

    /// <summary>Ranked action ids (warning/problem findings, biggest impact first).</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    public IReadOnlyList<CoachFinding> Findings { get; init; } = [];

    public CoachCameraCheck? Camera { get; init; }

    public CoachDrift? Drift { get; init; }

    public CoachResponse? Response { get; init; }

    public IReadOnlyList<CoachTrial> Trials { get; init; } = [];
}

public static class CoachPhases
{
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
}

/// <summary>Step names (options, <see cref="CoachStatus.Step"/>, <see cref="CoachFinding.Step"/>).</summary>
public static class CoachStepNames
{
    public const string CameraCheck = "CameraCheck";
    public const string Calibrating = "Calibrating";
    public const string Drift = "Drift";
    public const string MountResponse = "MountResponse";
    public const string Trials = "Trials";
    public const string Report = "Report";
    public const string Live = "Live";

    /// <summary>Selectable steps in execution order.</summary>
    public static IReadOnlyList<string> All { get; } = [CameraCheck, Drift, MountResponse, Trials];
}

public static class CoachStepStates
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

public static class CoachTrialStates
{
    public const string Pending = "Pending";
    public const string Settling = "Settling";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

public static class CoachTrialIds
{
    public const string A = "A";
    public const string B = "B";
    public const string C = "C";
    public const string A2 = "A2";
}

public static class CoachTrialKinds
{
    public const string Current = "current";
    public const string Suggestion = "suggestion";
    public const string Variant = "variant";
    public const string CurrentRepeat = "currentRepeat";
}

public static class CoachBacklashStates
{
    /// <summary>Only while the mount response runs: the backlash test or the reversal ladder is not finished yet (no value).</summary>
    public const string Measuring = "Measuring";

    public const string Measured = "Measured";
    public const string None = "None";
    public const string Unreliable = "Unreliable";
    public const string Skipped = "Skipped";
}

public static class CoachSeverities
{
    public const string Good = "good";
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Problem = "problem";
}

public static class CoachGrades
{
    public const string Excellent = "excellent";
    public const string Good = "good";
    public const string Fair = "fair";
    public const string Poor = "poor";
    public const string Unknown = "unknown";
}

public static class CoachGuidedSources
{
    public const string Trials = "trials";
    public const string Window = "window";
    public const string None = "none";
}

/// <summary>Sub-phase codes of <see cref="CoachStepStatus.DetailCode"/>.</summary>
public static class CoachDetailCodes
{
    public const string CameraCombination = "camera.combination";
    public const string Calibrating = "calibrating";
    public const string DriftMeasuring = "drift.measuring";
    public const string ResponseBacklash = "response.backlash";
    public const string ResponsePulses = "response.pulses";
    public const string TrialSettling = "trial.settling";
    public const string TrialRunning = "trial.running";
}

/// <summary>Values of the <c>reason</c> parameter of <see cref="CoachCodes.Interrupted"/>.</summary>
public static class CoachInterruptReasons
{
    public const string Guiding = "guiding";
    public const string Dither = "dither";
    public const string Stopped = "stopped";
    public const string Slew = "slew";

    /// <summary>Tracking stayed off (<see cref="Guiding.SafetySettings.CoachMountInterruptSeconds"/>).</summary>
    public const string TrackingOff = "trackingOff";

    public const string Disconnect = "disconnect";

    /// <summary>Internal: a measurement hook threw (reported as <see cref="CoachCodes.Internal"/>).</summary>
    internal const string Error = "error";
}

/// <summary>Stable message and finding codes (docs/COACH.md §6–7).</summary>
public static class CoachCodes
{
    // session / step messages
    public const string Busy = "coach.busy";
    public const string NotConnected = "coach.notConnected";
    public const string NoStar = "coach.noStar";
    public const string NoCalibration = "coach.noCalibration";
    public const string CalibrationFailed = "coach.calibrationFailed";
    public const string StarLost = "coach.starLost";
    public const string NoPulseOutput = "coach.noPulseOutput";
    public const string Interrupted = "coach.interrupted";
    public const string CameraError = "coach.cameraError";
    public const string Internal = "coach.internal";

    // camera check
    public const string CameraRecommendation = "camera.recommendation";
    public const string CameraGood = "camera.good";
    public const string CameraNoFeasible = "camera.noFeasible";
    public const string CameraSnrLow = "camera.snrLow";
    public const string CameraSaturated = "camera.saturated";
    public const string CameraDefocused = "camera.defocused";
    public const string CameraFewStars = "camera.fewStars";
    public const string CameraNoDarks = "camera.noDarks";

    // drift
    public const string DriftSeeing = "drift.seeing";
    public const string DriftMinMove = "drift.minMove";
    public const string DriftPeriodicError = "drift.periodicError";
    public const string DriftExposureLimit = "drift.exposureLimit";
    public const string DriftPolarAlignment = "drift.polarAlignment";
    public const string DriftWind = "drift.wind";
    public const string DriftDecGuideMode = "drift.decGuideMode";

    // mount response
    public const string ResponseDecBacklash = "response.decBacklash";
    public const string ResponseMinPulse = "response.minPulse";
    public const string ResponseAsymmetry = "response.asymmetry";
    public const string ResponseRateMismatch = "response.rateMismatch";
    public const string ResponseGood = "response.good";

    // trials
    public const string TrialsWinner = "trials.winner";
    public const string TrialsNoImprovement = "trials.noImprovement";
    public const string TrialsConditionsChanged = "trials.conditionsChanged";
    public const string TrialsNothingToTry = "trials.nothingToTry";

    // report
    public const string ReportSeeingLimited = "report.seeingLimited";
    public const string ReportMountLimited = "report.mountLimited";
    public const string ReportNoImagingScale = "report.noImagingScale";

    // live hints
    public const string HintRaOscillation = "hint.raOscillation";
    public const string HintRaSluggish = "hint.raSluggish";
    public const string HintPulseLimited = "hint.pulseLimited";
    public const string HintLowSnr = "hint.lowSnr";
    public const string HintDecDrift = "hint.decDrift";
    public const string HintSeeingBound = "hint.seeingBound";
}
