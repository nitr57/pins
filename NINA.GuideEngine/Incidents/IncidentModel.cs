// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Incidents;

/// <summary>What started (or joined) an incident. Order matters only for display.</summary>
public enum IncidentKind
{
    StarLost,
    Runaway,
    MountNotResponding,
    SettleTimeout,
    CameraFailure,
    MountPaused,
    CalibrationFailed,
    PulseLimited,
    PulseOutputFailed,
    DecFlipCorrected,
    Spike,
    Manual,
}

public enum IncidentEndReason
{
    /// <summary>Still open.</summary>
    Recording,

    /// <summary>Star found again and the error calm for 10 frames, plus the 30 s tail.</summary>
    Recovered,

    /// <summary>Guiding or calibration ended (stopped, failed) before it recovered.</summary>
    Stopped,

    /// <summary>The 5-minute limit was reached.</summary>
    Cap,

    /// <summary>A manual mark's fixed window.</summary>
    Manual,
}

public enum IncidentMarkerType
{
    Trigger,
    Recovered,
    Note,

    /// <summary>Frames between repeats of an ongoing incident whose images were not kept (telemetry is complete).</summary>
    Gap,
    End,
}

/// <summary>Why the frame images of an incident are missing.</summary>
public enum IncidentFramesOmitted
{
    None,

    /// <summary>Saving them would have left less than the minimum free disk space.</summary>
    DiskSpace,

    /// <summary>Only kept incidents were left to rotate out and they fill the budget.</summary>
    Budget,
}

/// <summary>Likely cause found by <c>IncidentDiagnoser</c>.</summary>
public enum IncidentCause
{
    Clouds,
    Dew,
    FieldJump,
    GuideStarOnly,
    DriftTooFast,
    MountNotMoving,
    CalibrationMismatch,
    MountMoved,
    Camera,
    PeriodicSpike,
    Unclear,
}

public sealed record IncidentTrigger(DateTimeOffset Time, IncidentKind Kind, GuideErrorCode? Code, string Message, string? Detail, long? Frame);

public sealed record IncidentMarker(DateTimeOffset Time, long? Frame, IncidentMarkerType Type, string? Text);

/// <summary>Where an incident was recorded.</summary>
public sealed record IncidentTags
{
    public string? ProfileId { get; init; }

    public string? ProfileName { get; init; }

    public string? GuideCamera { get; init; }

    public string? Mount { get; init; }

    public bool Simulator { get; init; }

    /// <summary>Guide camera scale, arcsec/px (0 when unknown).</summary>
    public double PixelScale { get; init; }

    /// <summary>Imaging camera scale, arcsec/px, null when unknown.</summary>
    public double? ImagingScale { get; init; }
}

/// <summary>What the diagnosis needs to know about the guider beyond the frames.</summary>
public sealed record IncidentContext
{
    /// <summary>Star search region half-size, px.</summary>
    public int SearchRegionPx { get; init; } = 15;

    public int MaxRaDurationMs { get; init; }

    public int MaxDecDurationMs { get; init; }

    /// <summary>Calibrated guide rates, px per ms of pulse, null when not calibrated.</summary>
    public double? RaRatePxPerMs { get; init; }

    public double? DecRatePxPerMs { get; init; }

    /// <summary>Camera angles of the RA and Dec axes (degrees), null when not calibrated.</summary>
    public double? RaAngleDeg { get; init; }

    public double? DecAngleDeg { get; init; }

    /// <summary>
    /// Worm period of the Predictive RA algorithm's periodic-error prediction (seconds) when it is the worm's (tooth count
    /// set or snapped), null otherwise.
    /// </summary>
    public double? WormPeriodSeconds { get; init; }

    /// <summary>Times of the earlier spike triggers of the same guiding session (for periodic spikes).</summary>
    public IReadOnlyList<DateTimeOffset> EarlierSpikes { get; init; } = [];
}

/// <summary>Telemetry of one frame of an incident. Positions are in sensor pixels.</summary>
public sealed record IncidentFrameRecord
{
    public long Frame { get; init; }

    public DateTimeOffset Time { get; init; }

    public double ExposureMs { get; init; }

    public GuiderState State { get; init; }

    public bool Settling { get; init; }

    public bool Dithering { get; init; }

    /// <summary>A Coach measurement frame (guiding output off or deliberate moves).</summary>
    public bool CoachMeasurement { get; init; }

    public bool StarFound { get; init; }

    public bool PrimaryEstimated { get; init; }

    /// <summary>Tracker status when the star was not found (StarLostEvent.Status), else null.</summary>
    public string? LostStatus { get; init; }

    public GuidePoint? Lock { get; init; }

    public GuidePoint? Star { get; init; }

    public double? Dx { get; init; }

    public double? Dy { get; init; }

    public double? RaDistanceRaw { get; init; }

    public double? DecDistanceRaw { get; init; }

    public int RaDurationMs { get; init; }

    public GuideDirection? RaDirection { get; init; }

    public int DecDurationMs { get; init; }

    public GuideDirection? DecDirection { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }

    public double? Snr { get; init; }

    public double? StarMass { get; init; }

    public double? Hfd { get; init; }

    /// <summary>Uncertainty of the measured position from the noise in the frame, σ per axis in px; null when unknown.</summary>
    public double? MeasurementSigmaPx { get; init; }

    /// <summary>All tracked stars of the frame (primary and secondaries), as the tracker reported them.</summary>
    public IReadOnlyList<StarInfo> Stars { get; init; } = [];

    /// <summary>Mount state when the frame was taken, null when unknown.</summary>
    public MountSnapshot? Mount { get; init; }

    /// <summary>Calibration step direction (West, East, Backlash, North, South, ...), null when not calibrating.</summary>
    public string? CalibrationDirection { get; init; }

    public int? CalibrationStep { get; init; }

    public bool HasContext { get; init; }

    public bool HasKey { get; init; }

    public int Crops { get; init; }
}

public sealed record IncidentEvidence(string Code, string Message, IReadOnlyDictionary<string, object?> Parameters, long? Frame);

public sealed record IncidentDiagnosis(IncidentCause Cause, string Message, IReadOnlyDictionary<string, object?> Parameters, IReadOnlyList<IncidentEvidence> Evidence);

/// <summary>A recorded incident: triggers, timeline, per-frame telemetry and the diagnosis. Images are stored beside it.</summary>
public sealed record Incident
{
    public required string Id { get; init; }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    /// <summary>Kind of the first trigger.</summary>
    public IncidentKind Kind { get; init; }

    public IReadOnlyList<IncidentTrigger> Triggers { get; init; } = [];

    public IReadOnlyList<IncidentMarker> Markers { get; init; } = [];

    /// <summary>How often the first kind happened; above 1 the incident is ongoing.</summary>
    public int Occurrences { get; init; } = 1;

    public bool Ongoing => Occurrences > 1;

    public IncidentEndReason EndReason { get; init; } = IncidentEndReason.Recording;

    public bool Kept { get; init; }

    public string? Note { get; init; }

    public IncidentTags Tags { get; init; } = new();

    public IncidentContext Context { get; init; } = new();

    public IncidentDiagnosis? Diagnosis { get; init; }

    public int SensorWidth { get; init; }

    public int SensorHeight { get; init; }

    public int ContextBinning { get; init; } = 1;

    public IncidentFramesOmitted FramesOmitted { get; init; }

    /// <summary>Bytes on disk (telemetry and images).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Guider settings at the start of the incident, as JSON.</summary>
    public string? SettingsJson { get; init; }

    public IReadOnlyList<IncidentFrameRecord> Frames { get; init; } = [];

    /// <summary>Number of frames (also set in summaries, whose <see cref="Frames"/> are empty).</summary>
    public int FrameCount { get; init; }
}
