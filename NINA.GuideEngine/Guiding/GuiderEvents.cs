// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Incidents;

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Base of all guider events. <see cref="Phd2Name"/> is the PHD2 event-server name so hosts can map
/// events 1:1 to PHD2 semantics (logs, UI markers, sequencer expectations).
/// </summary>
public abstract record GuiderEvent(DateTimeOffset Timestamp)
{
    public abstract string Phd2Name { get; }
}

/// <summary>Per-star measurement for UI overlays.</summary>
public sealed record StarInfo(double X, double Y, double Snr, double Mass, double Hfd, bool IsPrimary, bool Used, double Weight, string? RejectReason);

/// <summary>PHD2 GuideStep plus extensions (per-star data, estimation flag, arcsec values).</summary>
public sealed record GuideStepEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuideStep";

    public long Frame { get; init; }

    /// <summary>Seconds since guiding started.</summary>
    public double Time { get; init; }

    public string Mount { get; init; } = "Mount";

    /// <summary>Camera-axis offset from the lock position, px.</summary>
    public double Dx { get; init; }

    public double Dy { get; init; }

    /// <summary>Mount-axis error, px (PHD2 RADistanceRaw / DECDistanceRaw).</summary>
    public double RaDistanceRaw { get; init; }

    public double DecDistanceRaw { get; init; }

    /// <summary>Algorithm output, px (PHD2 RADistanceGuide / DECDistanceGuide).</summary>
    public double RaDistanceGuide { get; init; }

    public double DecDistanceGuide { get; init; }

    public int RaDuration { get; init; }

    public GuideDirection? RaDirection { get; init; }

    public int DecDuration { get; init; }

    public GuideDirection? DecDirection { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }

    public double StarMass { get; init; }

    public double Snr { get; init; }

    public double Hfd { get; init; }

    /// <summary>Smoothed distance (PHD2 AvgDist).</summary>
    public double AvgDist { get; init; }

    public int ErrorCode { get; init; }

    /// <summary>Image scale used for arcsec conversion.</summary>
    public double PixelScale { get; init; }

    public GuidePoint LockPosition { get; init; }

    public GuidePoint StarPosition { get; init; }

    public int StarsUsed { get; init; }

    public bool PrimaryEstimated { get; init; }

    /// <summary>
    /// Uncertainty of the measured position from the noise in the frame (centroid of the stars used), σ per axis in px;
    /// null when unknown.
    /// </summary>
    public double? MeasurementSigmaPx { get; init; }

    /// <summary>
    /// Measurement variance of this frame relative to what the RA axis's Predictive algorithm learned (1 = a usual frame,
    /// larger = trusted less); null when the algorithm did not run or is a Classic one.
    /// </summary>
    public double? RaNoiseFactor { get; init; }

    /// <summary>As <see cref="RaNoiseFactor"/>, for the Dec axis.</summary>
    public double? DecNoiseFactor { get; init; }

    public bool IsSettling { get; init; }

    /// <summary>True for fast-recenter moves after dither (bypass algorithms).</summary>
    public bool IsRecenterMove { get; init; }

    public IReadOnlyList<StarInfo> Stars { get; init; } = [];

    public double ProcessingMs { get; init; }

    /// <summary>Guiding Coach measurement frame: guiding output off, excluded from statistics (no correction fields).</summary>
    public bool CoachMeasurement { get; init; }
}

public sealed record StarLostEvent(DateTimeOffset Timestamp, long Frame, double Time, double StarMass, double Snr, double Hfd, double AvgDist, int ErrorCode, string Status)
    : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StarLost";
}

public sealed record AppStateEvent(DateTimeOffset Timestamp, GuiderState State, GuiderState Previous) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "AppState";
}

public sealed record LoopingExposuresEvent(DateTimeOffset Timestamp, long Frame) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LoopingExposures";
}

public sealed record LoopingExposuresStoppedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LoopingExposuresStopped";
}

public sealed record StarSelectedEvent(DateTimeOffset Timestamp, double X, double Y) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StarSelected";
}

public sealed record LockPositionSetEvent(DateTimeOffset Timestamp, double X, double Y) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LockPositionSet";
}

public sealed record LockPositionLostEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LockPositionLost";
}

/// <summary>Settings were applied to a running guider (PHD2 ConfigurationChange); the guide log records what changed.</summary>
public sealed record SettingsChangedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "ConfigurationChange";
}

public sealed record StartCalibrationEvent(DateTimeOffset Timestamp, string Mount) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StartCalibration";
}

public sealed record CalibratingEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Calibrating";

    public string Mount { get; init; } = "Mount";

    /// <summary>Direction name as PHD2 ("West", "East", "Backlash", "North", "South").</summary>
    public string Direction { get; init; } = string.Empty;

    public double Distance { get; init; }

    public double Dx { get; init; }

    public double Dy { get; init; }

    public GuidePoint Position { get; init; }

    public int Step { get; init; }

    public string State { get; init; } = string.Empty;

    /// <summary>Progress hint for the UI (0..1).</summary>
    public double Progress { get; init; }
}

public sealed record CalibrationCompleteEvent(DateTimeOffset Timestamp, string Mount, Calibration.CalibrationData Calibration) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationComplete";
}

public sealed record CalibrationFailedEvent(DateTimeOffset Timestamp, string Mount, string Reason, GuideErrorCode Code) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationFailed";
}

public sealed record CalibrationDataFlippedEvent(DateTimeOffset Timestamp, string Mount) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationDataFlipped";
}

public sealed record StartGuidingEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StartGuiding";
}

public sealed record GuidingStoppedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuidingStopped";
}

public sealed record PausedEvent(DateTimeOffset Timestamp, string Reason) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Paused";
}

public sealed record ResumedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Resumed";
}

public sealed record GuidingDitheredEvent(DateTimeOffset Timestamp, double Dx, double Dy) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuidingDithered";
}

public sealed record SettleBeginEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "SettleBegin";
}

public sealed record SettlingEvent(DateTimeOffset Timestamp, double Distance, double Time, double SettleTime, bool StarLocked) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Settling";
}

public sealed record SettleDoneEvent(DateTimeOffset Timestamp, int Status, string? Error, int TotalFrames, int DroppedFrames) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "SettleDone";

    public bool Succeeded => Status == 0;
}

/// <summary>Alert with a stable error code (PHD2 'Alert' carries Msg + Type).</summary>
public sealed record AlertEvent(DateTimeOffset Timestamp, GuideErrorCode Code, GuideErrorSeverity Severity, string Message, string? Detail = null) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Alert";

    /// <summary>The incident this alert started or joined (flight recorder), null when none.</summary>
    public string? IncidentId { get; init; }

    public string Type => Severity switch
    {
        GuideErrorSeverity.Info => "info",
        GuideErrorSeverity.Warning => "warning",
        _ => "error",
    };
}

/// <summary>Diagnostic note of the Predictive guide algorithm of one axis (takeover, jump, ...). Not part of PHD2's event set.</summary>
public sealed record AlgorithmNoteEvent(DateTimeOffset Timestamp, GuideAxis Axis, PredictiveNoteKind Kind, string Message) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "AlgorithmNote";
}

/// <summary>
/// Dec guide mode Drift: a direction was picked or switched, the safety valve opened or closed, a reset or a summary.
/// <see cref="Direction"/> is the direction after it. Not part of PHD2's event set.
/// </summary>
public sealed record DecDirectionNoteEvent(DateTimeOffset Timestamp, DecDirectionNoteKind Kind, DecGuideDirection Direction, string Message)
    : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "DecDirectionNote";
}

/// <summary>The learned periodic error of the RA axis is worth storing (it helps). Not part of PHD2's event set.</summary>
public sealed record PeriodicErrorModelEvent(DateTimeOffset Timestamp, PeriodicErrorModel Model) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "PeriodicErrorModel";
}

/// <summary>
/// The stored periodic error of the RA axis (<see cref="Model"/>, as restored or last stored) no longer holds: its
/// detected period stopped being stable or another period took over. The host deletes its stored copy if it is still
/// this one. Not part of PHD2's event set.
/// </summary>
public sealed record PeriodicErrorModelDiscardedEvent(DateTimeOffset Timestamp, PeriodicErrorModel Model) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "PeriodicErrorModelDiscarded";
}

/// <summary>
/// The adaptive Dec backlash compensation changed its pulse (PHD2 <c>BacklashComp</c>). The host stores <see cref="State"/>
/// for the next session (<see cref="Guider.RestoreBacklash"/>). Not part of PHD2's event set.
/// </summary>
public sealed record BacklashPulseLearnedEvent(DateTimeOffset Timestamp, BacklashState State) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "BacklashPulseLearned";
}

/// <summary>
/// The pulse model learned from a dither (docs/notes/DEC-PULSE-MODEL.md); it learns with <see cref="GuiderSettings.PulseModel"/>
/// off too. The host stores <see cref="State"/> for the next session (<see cref="Guider.RestorePulseModel"/>). Not part of
/// PHD2's event set.
/// </summary>
public sealed record PulseModelLearnedEvent(DateTimeOffset Timestamp, PulseModelState State) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "PulseModelLearned";
}

/// <summary>
/// The pulse model's values in use while it is on: when guiding starts, when it is switched on, and whenever they change.
/// Not part of PHD2's event set.
/// </summary>
public sealed record PulseModelUpdatedEvent(DateTimeOffset Timestamp, PulseModelValues Values) : GuiderEvent(Timestamp)
{
    /// <summary>RA pulses are this many times their calibrated length.</summary>
    public double RaPulseFactor => 1.0 / Values.RaEffect;

    /// <summary>Dec pulses are this many times their calibrated length.</summary>
    public double DecPulseFactor => 1.0 / Values.DecEffect;

    public override string Phd2Name => "PulseModelUpdated";
}

/// <summary>A processed frame is available (for the live view). Not part of PHD2's event set.</summary>
public sealed record FrameReadyEvent(DateTimeOffset Timestamp, GuideFrame Frame, IReadOnlyList<StarInfo> Stars, GuidePoint LockPosition) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "FrameReady";
}

/// <summary>The flight recorder started recording an incident (or reopened one). Not part of PHD2's event set.</summary>
public sealed record IncidentStartedEvent(DateTimeOffset Timestamp, string Id, IncidentKind Kind) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "IncidentStarted";
}

/// <summary>An incident was saved (also again after it was reopened); <see cref="Summary"/> has no frames. Not part of PHD2's event set.</summary>
public sealed record IncidentSavedEvent(DateTimeOffset Timestamp, Incident Summary) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "IncidentSaved";
}

/// <summary>An incident was deleted (by request or by the budget rotation). Not part of PHD2's event set.</summary>
public sealed record IncidentDeletedEvent(DateTimeOffset Timestamp, string Id) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "IncidentDeleted";
}

/// <summary>
/// An engine component failed and the guider carried on without it (the flight recorder, the incident diagnosis, a
/// Coach callback): <see cref="Source"/> names the component, <see cref="Message"/> the exception. At most one per source
/// and minute; <see cref="Suppressed"/> counts the faults of that source left out since its last report. Hosts log it.
/// Not part of PHD2's event set.
/// </summary>
public sealed record EngineFaultEvent(DateTimeOffset Timestamp, string Source, string Message, int Suppressed = 0) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "EngineFault";
}
