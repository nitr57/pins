// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

// API CONTRACT. Implementations may add members but must keep these.

public enum GuideParity
{
    Unknown,
    Even,
    Odd,
    Unchanged,
}

/// <summary>Result of a calibration (port of PHD2 Calibration struct).</summary>
public sealed record CalibrationData
{
    /// <summary>
    /// Camera angle of the RA axis, radians (PHD2 xAngle). Following PHD2 this is the direction the star
    /// moves on the sensor for an East pulse, i.e. opposite to the measured West calibration motion.
    /// </summary>
    public double XAngle { get; init; }

    /// <summary>Camera angle of the Dec axis, radians (PHD2 yAngle): direction the star moves for a North pulse.</summary>
    public double YAngle { get; init; }

    /// <summary>RA rate, pixels per millisecond of pulse at <see cref="Binning"/>.</summary>
    public double XRate { get; init; }

    /// <summary>Dec rate, pixels per millisecond; NaN/0 when Dec was not calibrated.</summary>
    public double YRate { get; init; }

    /// <summary>Declination at calibration, radians; null when unknown.</summary>
    public double? Declination { get; init; }

    public PierSide PierSide { get; init; }

    public double? RotatorAngleDeg { get; init; }

    public int Binning { get; init; } = 1;

    public GuideParity RaGuideParity { get; init; }

    public GuideParity DecGuideParity { get; init; }

    public double? GuideRateRa { get; init; }

    public double? GuideRateDec { get; init; }

    public double PixelScale { get; init; }

    public double PixelSizeUm { get; init; }

    public double FocalLengthMm { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public bool IsValid => XRate > 0 && !double.IsNaN(XAngle);

    // ---- Additive members ----

    /// <summary>True when a Dec rate was measured (PHD2: yRate != CALIBRATION_RATE_UNCALIBRATED).</summary>
    [JsonIgnore]
    public bool HasDecCalibration => YRate > 0 && !double.IsNaN(YRate) && !double.IsInfinity(YRate);

    /// <summary>Number of West steps used to measure the RA rate (PHD2 CalibrationDetails.raStepCount).</summary>
    public int RaStepCount { get; init; }

    /// <summary>Number of North steps used to measure the Dec rate; 0 when Dec was not calibrated.</summary>
    public int DecStepCount { get; init; }

    /// <summary>Star displacement (start - current, px) logged at each RA calibration step (West then East).</summary>
    public IReadOnlyList<GuidePoint> RaSteps { get; init; } = [];

    /// <summary>Star displacement (start - current, px) logged at each Dec calibration step (North then South).</summary>
    public IReadOnlyList<GuidePoint> DecSteps { get; init; } = [];

    /// <summary>
    /// Star position after every calibration step (camera px), in order: "Start", then "West", "East", "Backlash",
    /// "North", "South", "NudgeSouth" legs. For plotting the calibration like KStars; empty for older calibrations.
    /// </summary>
    public IReadOnlyList<CalibrationPoint> Points { get; init; } = [];

    /// <summary>
    /// Dec take-up after the reversal that ends the North leg (gear engaged North): South pulse time since the reversal and
    /// the Dec motion it produced, per frame of the South return and nudges. Empty when Dec was not calibrated or for older
    /// calibrations. The backlash play model measures the shape of the play from it.
    /// </summary>
    public IReadOnlyList<DecTakeUpPoint> DecTakeUp { get; init; } = [];

    /// <summary>Calibration pulse duration used, ms.</summary>
    public int CalibrationStepMs { get; init; }

    /// <summary>Calibration distance used, px.</summary>
    public int CalibrationDistancePx { get; init; }

    /// <summary>Sanity issue flagged for this calibration (PHD2 CalibrationDetails.lastIssue).</summary>
    public CalibrationIssueType LastIssue { get; init; }

    /// <summary>Deviation of the RA/Dec axes from perpendicular, degrees (PHD2 orthoError).</summary>
    [JsonIgnore]
    public double OrthogonalityErrorDeg => MountTransform.OrthogonalityErrorDegrees(XAngle, YAngle);
}

/// <summary>Star position recorded after a calibration step.</summary>
/// <param name="Direction">Start, West, East, Backlash, North, South or NudgeSouth.</param>
/// <param name="Step">Step number within the leg.</param>
/// <param name="X">Star x (camera px).</param>
/// <param name="Y">Star y (camera px).</param>
public sealed record CalibrationPoint(string Direction, int Step, double X, double Y);

/// <summary>One point of the Dec take-up after a reversal: cumulative motor time since the reversal and the star's motion.</summary>
/// <param name="PulseMs">Pulse time sent in the new direction since the reversal (ms).</param>
/// <param name="MovedPx">Star motion in that direction since the reversal (mount px).</param>
public sealed record DecTakeUpPoint(double PulseMs, double MovedPx);
