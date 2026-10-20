// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Logging;

/// <summary>Image noise reduction method as reported in the guide log header.</summary>
public enum GuideLogNoiseReduction
{
    None,
    Mean2x2,
    Median3x3,
}

/// <summary>Global guider settings (PHD2 MyFrame::GetSettingsSummary).</summary>
public sealed record GuideLogGlobalSettings
{
    public bool DitherRaOnly { get; init; }

    public double DitherScale { get; init; } = 1.0;

    public GuideLogNoiseReduction NoiseReduction { get; init; }

    public int TimeLapseMs { get; init; }

    public bool ServerEnabled { get; init; }

    /// <summary>Image scale in arcsec/px; null = unspecified.</summary>
    public double? PixelScale { get; init; }

    public int Binning { get; init; } = 1;

    /// <summary>Focal length in mm; null = unspecified.</summary>
    public int? FocalLengthMm { get; init; }
}

/// <summary>Star tracking settings (PHD2 GuiderMultiStar::GetSettingsSummary).</summary>
public sealed record GuideLogGuiderSettings
{
    public int SearchRegionPx { get; init; } = 15;

    /// <summary>Mass change threshold (0.5 = 50 %); null = mass change detection disabled.</summary>
    public double? MassChangeThreshold { get; init; } = 0.5;

    public bool MultiStar { get; init; } = true;

    /// <summary>Number of stars in the multi-star list.</summary>
    public int StarListSize { get; init; } = 1;
}

/// <summary>Guide camera settings (PHD2 GuideCamera::GetSettingsSummary).</summary>
public sealed record GuideLogCameraSettings
{
    public string Name { get; init; } = "";

    /// <summary>Camera gain; null when the camera has no gain control.</summary>
    public int? Gain { get; init; }

    public int FullWidth { get; init; }

    public int FullHeight { get; init; }

    /// <summary>Exposure of the dark frame in use (ms); null = no dark.</summary>
    public int? DarkExposureMs { get; init; }

    public bool DefectMapInUse { get; init; }

    /// <summary>Pixel size in µm; null = unspecified.</summary>
    public double? PixelSizeUm { get; init; }
}

/// <summary>Dec backlash compensation settings.</summary>
public sealed record GuideLogBacklash(bool Enabled, int PulseMs, bool PlayModel = false);

/// <summary>Mount settings (PHD2 Mount/Scope::GetSettingsSummary).</summary>
public sealed record GuideLogMountSettings
{
    public string Name { get; init; } = "";

    public bool IsConnected { get; init; } = true;

    public bool GuidingEnabled { get; init; } = true;

    /// <summary>Calibration in use; null or invalid = not calibrated.</summary>
    public CalibrationData? Calibration { get; init; }

    /// <summary>Orthogonality error of the calibration in degrees; null = unknown.</summary>
    public double? OrthoErrorDeg { get; init; }

    public GuideLogAlgorithm XAlgorithm { get; init; } = GuideLogAlgorithm.None;

    public GuideLogAlgorithm YAlgorithm { get; init; } = GuideLogAlgorithm.None;

    /// <summary>Backlash compensation; null when the mount has none.</summary>
    public GuideLogBacklash? Backlash { get; init; }

    /// <summary>Whether the pulse model sizes the pulses (docs/notes/DEC-PULSE-MODEL.md); null leaves the line out.</summary>
    public bool? PulseModel { get; init; }

    public int MaxRaDurationMs { get; init; } = 2500;

    public int MaxDecDurationMs { get; init; } = 2500;

    public DecGuideMode DecGuideMode { get; init; } = DecGuideMode.Auto;

    /// <summary>RA guide speed in arcsec per second (e.g. 7.5 for 0.5× sidereal); null = unknown.</summary>
    public double? RaGuideSpeedArcsecPerSec { get; init; }

    /// <summary>Dec guide speed in arcsec per second; null = unknown.</summary>
    public double? DecGuideSpeedArcsecPerSec { get; init; }

    /// <summary>Issue flagged by the last calibration sanity check: None, Steps, Orthogonality, Rates, Difference.</summary>
    public string LastCalibrationIssue { get; init; } = "None";

    /// <summary>Timestamp text of the last calibration.</summary>
    public string CalibrationTimestamp { get; init; } = "";
}

/// <summary>Calibration parameters (PHD2 Scope::CalibrationSettingsSummary).</summary>
public sealed record GuideLogCalibrationSettings(int StepMs, int DistancePx, bool AssumeOrthogonal);

/// <summary>Telescope pointing (PHD2 PointingInfo). Null values print as Unknown.</summary>
public sealed record GuideLogPointing
{
    public double? RaHours { get; init; }

    public double? DecDeg { get; init; }

    public double? HourAngleHours { get; init; }

    public PierSide PierSide { get; init; } = PierSide.Unknown;

    /// <summary>Rotator position in degrees; null = no rotator ("N/A").</summary>
    public double? RotatorDeg { get; init; }

    public double? AltitudeDeg { get; init; }

    public double? AzimuthDeg { get; init; }
}

/// <summary>Everything written in the header of a guiding or calibration section.</summary>
public sealed record GuideLogHeader
{
    public string EquipmentProfile { get; init; } = "";

    public GuideLogGlobalSettings Global { get; init; } = new();

    public GuideLogGuiderSettings Guider { get; init; } = new();

    public GuideLogCameraSettings? Camera { get; init; }

    public int ExposureMs { get; init; }

    public GuideLogMountSettings? Mount { get; init; }

    /// <summary>Calibration step settings; used by the calibration section header.</summary>
    public GuideLogCalibrationSettings? CalibrationSettings { get; init; }

    public GuideLogPointing Pointing { get; init; } = new();

    public GuidePoint LockPosition { get; init; } = GuidePoint.Invalid;

    public GuidePoint StarPosition { get; init; } = GuidePoint.Invalid;

    public double Hfd { get; init; }
}

/// <summary>One guide step row (PHD2 GuideStepInfo). Distances in pixels.</summary>
public sealed record GuideLogStep
{
    public long FrameNumber { get; init; }

    /// <summary>Seconds since guiding started.</summary>
    public double TimeSec { get; init; }

    /// <summary>Camera-frame offset of the star from the lock position (dx, dy).</summary>
    public double CameraDx { get; init; }

    public double CameraDy { get; init; }

    /// <summary>Offset transformed to mount coordinates (RARawDistance, DECRawDistance).</summary>
    public double RaRawDistance { get; init; }

    public double DecRawDistance { get; init; }

    /// <summary>Algorithm output (RAGuideDistance, DECGuideDistance).</summary>
    public double RaGuideDistance { get; init; }

    public double DecGuideDistance { get; init; }

    public int RaDurationMs { get; init; }

    /// <summary>Direction of the RA pulse; printed only when the duration is positive.</summary>
    public GuideDirection? RaDirection { get; init; }

    public int DecDurationMs { get; init; }

    public GuideDirection? DecDirection { get; init; }

    public double StarMass { get; init; }

    public double Snr { get; init; }

    /// <summary>PHD2 star error code (same numbering as <see cref="Stars.StarFindResult"/>).</summary>
    public int ErrorCode { get; init; }
}

/// <summary>A frame on which the star was not found (PHD2 FrameDroppedInfo).</summary>
public sealed record GuideLogFrameDropped
{
    public long FrameNumber { get; init; }

    /// <summary>Seconds since guiding started.</summary>
    public double TimeSec { get; init; }

    public double StarMass { get; init; }

    public double Snr { get; init; }

    public int ErrorCode { get; init; }

    /// <summary>Human-readable status, e.g. "Star lost - low SNR".</summary>
    public string Status { get; init; } = "";
}

/// <summary>Calibration direction labels as used in PHD2 calibration step rows.</summary>
public enum GuideLogCalibrationDirection
{
    West,
    East,
    Backlash,
    North,
    South,
    NudgeSouth,
}

/// <summary>One calibration step row (PHD2 CalibrationStepInfo). Distances in pixels.</summary>
public sealed record GuideLogCalibrationStep(GuideLogCalibrationDirection Direction, int Step, double Dx, double Dy, double X, double Y, double Distance);
