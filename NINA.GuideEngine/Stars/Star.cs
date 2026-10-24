// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Stars;

// API CONTRACT. Implementations may add members but must keep these signatures.

public enum StarFindMode
{
    Centroid,
    Peak,
}

public enum StarFindResult
{
    Ok = 0,
    Saturated,
    LowSnr,
    LowMass,
    LowHfd,
    HighHfd,
    TooNearEdge,
    MassChange,
    Error,
}

/// <summary>A measured star (port of PHD2 Star).</summary>
public class Star
{
    public Star()
    {
    }

    /// <summary>Copy constructor (PHD2 <c>Star newStar(m_primaryStar)</c>).</summary>
    public Star(Star other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Position = other.Position;
        Mass = other.Mass;
        Snr = other.Snr;
        Hfd = other.Hfd;
        PeakValue = other.PeakValue;
        LastFindResult = other.LastFindResult;
    }

    public GuidePoint Position { get; set; } = GuidePoint.Invalid;

    public double X => Position.X;

    public double Y => Position.Y;

    public bool IsValid => Position.IsValid;

    public double Mass { get; set; }

    public double Snr { get; set; }

    public double Hfd { get; set; }

    public ushort PeakValue { get; set; }

    public StarFindResult LastFindResult { get; set; } = StarFindResult.Error;

    public static bool WasFound(StarFindResult r) => r is StarFindResult.Ok or StarFindResult.Saturated;

    public bool WasFound() => IsValid && WasFound(LastFindResult);

    /// <summary>
    /// Marks the star invalid and clears mass/SNR/HFD (PHD2 Star::Invalidate). Like PHD2 the last X/Y are
    /// kept so they can still serve as a search hint.
    /// </summary>
    public void Invalidate()
    {
        Mass = 0.0;
        Snr = 0.0;
        Hfd = 0.0;
        LastFindResult = StarFindResult.Error;
        Position = Position with { IsValid = false };
    }

    public void SetError(StarFindResult error) => LastFindResult = error;

    /// <summary>
    /// Finds the star near (<paramref name="baseX"/>, <paramref name="baseY"/>) within
    /// ±<paramref name="searchRegion"/>. Returns true when the star was found (Ok or Saturated).
    /// Port of PHD2 Star::Find. <paramref name="saturation"/> is the saturation ADU, 0 for
    /// auto-detection from the star profile.
    /// </summary>
    public virtual bool Find(GuideFrame img, int searchRegion, int baseX, int baseY, StarFindMode mode, double minHfd, double maxHfd, ushort saturation)
    {
        ArgumentNullException.ThrowIfNull(img);
        return StarFinder.Find(this, img, searchRegion, baseX, baseY, mode, minHfd, maxHfd, saturation);
    }

    /// <summary>
    /// Find using the star's current position as the search hint. As in PHD2 (implicit double→int
    /// conversion) the hint is truncated, not rounded.
    /// </summary>
    public bool Find(GuideFrame img, int searchRegion, StarFindMode mode, double minHfd, double maxHfd, ushort saturation)
        => Find(img, searchRegion, (int)X, (int)Y, mode, minHfd, maxHfd, saturation);
}

/// <summary>A star tracked by the guider (port of PHD2 GuideStar).</summary>
public class GuideStar : Star
{
    public GuideStar()
    {
    }

    public GuideStar(Star s)
    {
        Position = s.Position;
        Mass = s.Mass;
        Snr = s.Snr;
        Hfd = s.Hfd;
        PeakValue = s.PeakValue;
        LastFindResult = s.LastFindResult;
        ReferencePoint = s.Position;
    }

    public GuidePoint ReferencePoint { get; set; } = GuidePoint.Invalid;

    public int MissCount { get; set; }

    public int ZeroCount { get; set; }

    /// <summary>Offset from the primary star, set in AutoFind.</summary>
    public GuidePoint OffsetFromPrimary { get; set; } = new(0, 0);

    public bool WasLost { get; set; }

    /// <summary>
    /// Multi-star AutoFind (port of PHD2 GuideStar::AutoFind). Returns the found stars, primary first; an
    /// empty list when no star was found. <paramref name="maxStars"/> is the list size (PHD2 passes
    /// MAX_LIST_SIZE = 12 in multi-star mode and 1 otherwise); <paramref name="roi"/> empty = full frame.
    /// </summary>
    /// <param name="image">Full frame (AutoFind fails on subframes, as in PHD2).</param>
    /// <param name="extraEdgeAllowance">Extra edge margin, e.g. the calibration distance when uncalibrated.</param>
    /// <param name="searchRegion">Star search region half-size (px).</param>
    /// <param name="roi">Region of interest, empty for the full frame.</param>
    /// <param name="maxStars">Maximum list size.</param>
    /// <param name="options">Detection options (HFD limits, min SNR, saturation, downsampling, scoring).</param>
    /// <param name="diagnostics">Optional sink for intermediate results (candidates, saturation level...).</param>
    public static List<GuideStar> AutoFind(GuideFrame image, int extraEdgeAllowance, int searchRegion, IntRect roi, int maxStars,
        StarFinderOptions options, AutoFindDiagnostics? diagnostics = null)
        => StarAutoFinder.AutoFind(image, extraEdgeAllowance, searchRegion, roi, maxStars, options, diagnostics);
}
