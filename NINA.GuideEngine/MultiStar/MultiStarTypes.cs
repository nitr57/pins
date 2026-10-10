// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.MultiStar;

/// <summary>Multi-star tracking settings (PHD2 GuiderMultiStar defaults plus the dropout fallback extension).</summary>
public sealed record MultiStarOptions
{
    /// <summary>Use secondary stars to refine the offset. Off = single-star guiding.</summary>
    public bool MultiStarEnabled { get; init; } = true;

    /// <summary>Enter stabilisation when the primary distance exceeds this many σ (PHD2 DEFAULT_STABILITY_SIGMAX).</summary>
    public double StabilitySigmaX { get; init; } = 5.0;

    public bool MassChangeThresholdEnabled { get; init; } = true;

    /// <summary>Relative mass change threshold (PHD2 DefaultMassChangeThreshold).</summary>
    public double MassChangeThreshold { get; init; } = MassChecker.DefaultThreshold;

    /// <summary>"Tolerate jumps" filter (PHD2 DistanceChecker), off by default as in PHD2.</summary>
    public bool TolerateJumpsEnabled { get; init; }

    public double TolerateJumpsThreshold { get; init; } = 4.0;

    /// <summary>Subframe mode: single star only (PHD2 UseSubframes).</summary>
    public bool UseSubframes { get; init; }

    /// <summary>Normalise masses by exposure in the mass checker (auto exposure).</summary>
    public bool AutoExposure { get; init; }

    /// <summary>AutoFind list size in multi-star mode (PHD2 MAX_LIST_SIZE).</summary>
    public int MaxListSize { get; init; } = 12;

    /// <summary>
    /// Extension: when the primary is lost (not found, mass change, or saturated when
    /// <see cref="SaturatedPrimaryIsLost"/>) while guiding, estimate its position from agreeing
    /// secondaries instead of reporting a lost star.
    /// </summary>
    public bool PrimaryDropoutFallback { get; init; } = true;

    /// <summary>Minimum number of agreeing secondaries for the fallback estimate.</summary>
    public int FallbackMinStars { get; init; } = 3;

    /// <summary>Agreement gate: secondaries must agree within max(2.5σ, this floor) px of the median estimate.</summary>
    public double FallbackAgreementFloorPx { get; init; } = 0.5;

    /// <summary>Treat a saturated primary as lost (PHD2 treats it as found).</summary>
    public bool SaturatedPrimaryIsLost { get; init; }
}

/// <summary>Guider state flags passed per frame.</summary>
/// <param name="IsGuiding">Guider is in the guiding state (PHD2 IsGuiding()).</param>
/// <param name="IsSettling">A settle is in progress (PHD2 PhdController::IsSettling()).</param>
/// <param name="IsPaused">Guiding is paused.</param>
/// <param name="RaOnly">Guiding RA only (distance measured along x only, PHD2 GuidingRAOnly).</param>
/// <param name="GuidingEnabled">Mount guiding enabled (PHD2 pMount->GetGuidingEnabled()).</param>
public readonly record struct TrackerState(bool IsGuiding, bool IsSettling = false, bool IsPaused = false, bool RaOnly = false, bool GuidingEnabled = true)
{
    public static TrackerState Looping => new(false);

    public static TrackerState Guiding => new(true);
}

/// <summary>Overall outcome of one tracked frame.</summary>
public enum TrackerOutcome
{
    /// <summary>Primary found; offset valid when a lock position is set.</summary>
    Found,

    /// <summary>Primary lost but its position was estimated from secondaries (extension).</summary>
    Estimated,

    /// <summary>Primary lost (see <see cref="MultiStarFrameResult.ErrorCode"/>).</summary>
    Lost,

    /// <summary>Frame rejected by the jump filter ("tolerate jumps").</summary>
    JumpRejected,

    /// <summary>No star selected yet.</summary>
    NoStarSelected,
}

/// <summary>What happened to a tracked star in the frame (for UI overlays and logs).</summary>
public enum TrackedStarStatus
{
    /// <summary>The primary star, measured.</summary>
    Primary,

    /// <summary>The primary star could not be used this frame.</summary>
    PrimaryLost,

    /// <summary>Secondary contributed to the weighted offset (PHD2 log flag "U").</summary>
    Used,

    /// <summary>Secondary excursion above 2.5σ, excluded this frame ("M").</summary>
    Miss,

    /// <summary>Secondary reference point reset after more than 10 misses ("R").</summary>
    ReferenceReset,

    /// <summary>Secondary not found in its search box ("L").</summary>
    Lost,

    /// <summary>Secondary evicted as a probable hot pixel (zero displacement, "DZ").</summary>
    DroppedZero,

    /// <summary>Secondary not measured this frame (not guiding, settling, stabilising or beyond MaxStars).</summary>
    NotMeasured,

    /// <summary>Secondary reference re-snapped after a lock position change.</summary>
    Resnapped,

    /// <summary>Secondary used for the primary-dropout estimate.</summary>
    FallbackUsed,

    /// <summary>Secondary measured for the dropout estimate but disagreed with the others.</summary>
    FallbackRejected,
}

/// <summary>Measurement snapshot of a star.</summary>
public readonly record struct StarSnapshot(GuidePoint Position, double Mass, double Snr, double Hfd, ushort PeakValue, StarFindResult Result)
{
    public static StarSnapshot Of(Star s) => new(s.Position, s.Mass, s.Snr, s.Hfd, s.PeakValue, s.LastFindResult);
}

/// <summary>Per-star information of a tracked frame.</summary>
/// <param name="Index">Index in the guide star list (0 = primary).</param>
/// <param name="Star">Latest measurement.</param>
/// <param name="ReferencePoint">Reference point displacements are measured against.</param>
/// <param name="Status">What happened this frame.</param>
/// <param name="Weight">Weight in the combined offset (1 for the primary, SNR ratio for used secondaries, else 0).</param>
/// <param name="Displacement">Displacement from the reference point (secondaries), invalid when not measured.</param>
/// <param name="MissCount">Excursion miss counter.</param>
/// <param name="ZeroCount">Zero-displacement counter.</param>
public sealed record TrackedStarInfo(int Index, StarSnapshot Star, GuidePoint ReferencePoint, TrackedStarStatus Status, double Weight,
    GuidePoint Displacement, int MissCount, int ZeroCount);

/// <summary>Result of <see cref="MultiStarTracker.ProcessFrame"/>.</summary>
public sealed record MultiStarFrameResult
{
    public required TrackerOutcome Outcome { get; init; }

    /// <summary>Primary star measurement (for <see cref="TrackerOutcome.Estimated"/> the estimated position with the last good mass/SNR/HFD).</summary>
    public required StarSnapshot Primary { get; init; }

    /// <summary>Primary find result: Ok/Saturated when found, otherwise why it was lost (MassChange, LowSnr, Error...).</summary>
    public required StarFindResult ErrorCode { get; init; }

    /// <summary>Offset to guide on, camera px relative to the lock position; invalid when unusable or no lock position.</summary>
    public required GuidePoint CameraOffset { get; init; }

    /// <summary>Offset of the primary alone (before multi-star refinement).</summary>
    public GuidePoint PrimaryOnlyOffset { get; init; } = GuidePoint.Invalid;

    /// <summary>True when the SNR-weighted multi-star offset replaced the primary-only offset.</summary>
    public bool Refined { get; init; }

    /// <summary>True when the primary position was estimated from secondaries (extension).</summary>
    public bool IsEstimated => Outcome == TrackerOutcome.Estimated;

    /// <summary>Stars contributing to the offset (PHD2 m_starsUsed).</summary>
    public int StarsUsed { get; init; }

    /// <summary>Multi-star stabilisation active (single-star offsets).</summary>
    public bool Stabilizing { get; init; }

    /// <summary>Distance from the lock position (after refinement), px; RA-only distance when guiding RA only.</summary>
    public double Distance { get; init; }

    /// <summary>Mass checker limits when the mass check ran.</summary>
    public MassLimits? MassLimits { get; init; }

    /// <summary>All guide stars (primary first) with per-star status.</summary>
    public required IReadOnlyList<TrackedStarInfo> Stars { get; init; }

    /// <summary>Human readable status (PHD2 StarStatusStr / StarStatus).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>True when an offset can be used for guiding.</summary>
    public bool HasOffset => CameraOffset.IsValid;

    public bool StarFound => Outcome is TrackerOutcome.Found or TrackerOutcome.Estimated;
}

/// <summary>Result of <see cref="MultiStarTracker.AutoSelect"/>.</summary>
public sealed record AutoSelectResult(bool Success, StarSnapshot Primary, IReadOnlyList<GuideStar> Stars, AutoFindDiagnostics? Diagnostics)
{
    /// <summary>Suggested lock position (the primary position), invalid on failure.</summary>
    public GuidePoint LockPosition => Success ? Primary.Position : GuidePoint.Invalid;
}

/// <summary>Why a manual star selection failed.</summary>
public enum StarSelectionError
{
    None,

    /// <summary>No star was found around the requested position.</summary>
    NoStar,

    /// <summary>The star is too close to the frame edge for the search region.</summary>
    NearEdge,

    /// <summary>The guider is guiding, calibrating, starting to guide or running the Coach.</summary>
    Busy,

    /// <summary>The guider is not looping exposures.</summary>
    NotLooping,

    /// <summary>The request was replaced by a newer one, or the loop stopped before a frame arrived.</summary>
    Cancelled,
}

/// <summary>Result of a manual star selection (<see cref="MultiStarTracker.SelectStar"/>).</summary>
/// <param name="Error">Why the selection failed, <see cref="StarSelectionError.None"/> on success.</param>
/// <param name="Primary">The selected star (on failure the last measurement, if any).</param>
/// <param name="SecondaryStars">Number of secondary stars found around it (multi-star mode).</param>
public sealed record StarSelectionResult(StarSelectionError Error, StarSnapshot Primary, int SecondaryStars)
{
    public bool Success => Error == StarSelectionError.None;

    public static StarSelectionResult Failed(StarSelectionError error, StarSnapshot primary = default) => new(error, primary, 0);
}

/// <summary>Result of <see cref="MultiStarTracker.RefreshSecondaryStars"/>.</summary>
/// <param name="Before">Secondary stars before the refresh.</param>
/// <param name="Found">Secondary stars found around the primary.</param>
/// <param name="Replaced">True when the found stars replaced the previous secondaries.</param>
public readonly record struct SecondaryRefreshResult(int Before, int Found, bool Replaced);

/// <summary>Guider state relevant to the subframe bounding box (PHD2 GetBoundingBox).</summary>
public enum BoundingBoxState
{
    Other,
    Selected,
    Calibrating,
    Guiding,
}
