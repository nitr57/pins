// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.Guiding;

/// <summary>Per-axis algorithm selection and parameters (PHD2 parameter names, see <see cref="IGuideAlgorithm.ParamNames"/>).</summary>
public sealed record AlgorithmSettings(GuideAlgorithmKind Kind, IReadOnlyDictionary<string, double>? Parameters = null);

/// <summary>Dec backlash compensation settings (PHD2 defaults: off).</summary>
public sealed record BacklashSettings
{
    public bool Enabled { get; init; }

    public int PulseMs { get; init; } = 20;

    public int FloorMs { get; init; } = 20;

    /// <summary>0 = automatic (1.5 × pulse).</summary>
    public int CeilingMs { get; init; }

    /// <summary>
    /// Fixed: PHD2's pulse at every reversal. PlayModel: the gear's play is tracked, reversals the offset does not need
    /// are held back, and a reversal takes up the play that is left (<see cref="PlayModelBacklash"/>); <see cref="PulseMs"/>
    /// is then only its first guess of the play.
    /// </summary>
    public BacklashModel Model { get; init; } = BacklashModel.Fixed;

    /// <summary>
    /// Play model only: when guiding starts with a kept calibration after the mount moved more than this (degrees) or
    /// changed pier side since the play was last measured, the play is measured again first (<see cref="DecPlayProbe"/>):
    /// the gear is driven North until the star follows, then South back to the start. 0 = never.
    /// </summary>
    public double RemeasureAfterSlewDeg { get; init; } = 5;
}

/// <summary>All guider settings. Immutable; apply changes with <see cref="Guider.UpdateSettings"/>.</summary>
public sealed record GuiderSettings
{
    /// <summary>Guide exposure in milliseconds.</summary>
    public double ExposureMs { get; init; } = 2000;

    /// <summary>Hardware binning.</summary>
    public int Binning { get; init; } = 1;

    public int? Gain { get; init; }

    public int? Offset { get; init; }

    /// <summary>Guide optics focal length (mm); used for the image scale and calibration step.</summary>
    public double FocalLengthMm { get; init; }

    /// <summary>Pixel size override (µm); 0 uses the camera's pixel size.</summary>
    public double PixelSizeUm { get; init; }

    public StarFinderOptions StarFinder { get; init; } = new();

    public MultiStarOptions MultiStar { get; init; } = new();

    public Imaging.NoiseReduction NoiseReduction { get; init; } = Imaging.NoiseReduction.None;

    /// <summary>Calibration settings; <see cref="AutoCalibrationStep"/> recomputes StepMs/DistancePx from optics and guide rate.</summary>
    public CalibrationSettings Calibration { get; init; } = new();

    public bool AutoCalibrationStep { get; init; } = true;

    public AlgorithmSettings RaAlgorithm { get; init; } = new(GuideAlgorithmKind.Hysteresis);

    public AlgorithmSettings DecAlgorithm { get; init; } = new(GuideAlgorithmKind.ResistSwitch);

    /// <summary>Min-move override for both axes (px); null uses PHD2's smart default from the image scale.</summary>
    public double? MinMovePx { get; init; }

    public DecGuideMode DecGuideMode { get; init; } = DecGuideMode.Auto;

    public int MaxRaDurationMs { get; init; } = PulseLimiter.DefaultMaxDurationMs;

    public int MaxDecDurationMs { get; init; } = PulseLimiter.DefaultMaxDurationMs;

    /// <summary>Shortest guide pulse sent (ms, 0–50); shorter algorithm pulses are rounded to 0 or to it. 0 sends any length.</summary>
    public int MinPulseMs { get; init; } = AxisCorrector.DefaultMinPulseMs;

    public BacklashSettings Backlash { get; init; } = new();

    /// <summary>
    /// Size the pulses by what they really move: per axis the share of a calibrated pulse that moves the star, learned from
    /// the dithers (docs/notes/DEC-PULSE-MODEL.md).
    /// </summary>
    public bool PulseModel { get; init; }

    public DitherMode DitherMode { get; init; } = DitherMode.Random;

    public double DitherScale { get; init; } = 1.0;

    /// <summary>PHD2 CalibrationFlipRequiresDecFlip (per mount). May be corrected by the post-flip Dec self-check.</summary>
    public bool DecFlipRequired { get; init; }

    /// <summary>Run the post-flip Dec self-check.</summary>
    public bool VerifyDecAfterFlip { get; init; } = true;

    /// <summary>Issue RA and Dec pulses concurrently when the output supports it.</summary>
    public bool SimultaneousPulses { get; init; }

    public bool DecCompensation { get; init; } = true;

    /// <summary>Fast recenter after dither (PHD2 default on).</summary>
    public bool FastRecenter { get; init; } = true;

    public SafetySettings Safety { get; init; } = new();

    /// <summary>Frames in the statistics window.</summary>
    public int StatsWindow { get; init; } = 100;

    /// <summary>Frames to try auto-selecting a star before giving up when guiding is started.</summary>
    public int AutoSelectAttempts { get; init; } = 3;

    /// <summary>Consecutive guide-pulse failures before guiding fails.</summary>
    public int MaxPulseFailures { get; init; } = 3;

    /// <summary>Analyse the guiding statistics while guiding and raise live coaching hints (<see cref="Coach.LiveHintAnalyser"/>).</summary>
    public bool LiveHints { get; init; } = true;

    /// <summary>Downscaled image published with <see cref="FrameReadyEvent"/> every N frames (1 = every frame).</summary>
    public int FrameEventInterval { get; init; } = 1;

    /// <summary>Flight recorder (incidents); records only once the host set a store (<see cref="Guider.SetIncidentStore"/>).</summary>
    public Incidents.IncidentSettings Incidents { get; init; } = new();

    /// <summary>Image scale (″/px, binned) from optics; 1.0 when unknown.</summary>
    public double PixelScale(double cameraPixelSizeUm) =>
        new GuideOptics(FocalLengthMm, PixelSizeUm > 0 ? PixelSizeUm : cameraPixelSizeUm, Binning).PixelScale;
}
